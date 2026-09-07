namespace KONGOR.MasterServer.Controllers.Accounts;

/// <summary>
///     Handles the legacy game-client form used to redeem a purchased nickname-change product.
/// </summary>
[ApiController]
[Route("client_require_nickchange.php")]
[Consumes("application/x-www-form-urlencoded")]
public class NicknameChangeController(MerrickContext databaseContext, IDatabase distributedCache, ILogger<NicknameChangeController> logger) : ControllerBase
{
    private MerrickContext MerrickContext { get; } = databaseContext;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    private const int NicknameChangeProductID = 161;
    private const int MinimumAccountNameLength = 4;
    private const int MaximumAccountNameLength = 12;

    private const int NicknameAlreadyExistsError = 0;
    private const int ForbiddenNicknamePrefixError = 2;
    private const int InvalidNicknameError = 3;
    private const int AccountInformationError = 4;
    private const int StoreTransactionError = 5;
    private const int InternalError = 6;
    private const int NicknameConfirmationError = 8;
    private const int InvalidPasswordError = 9;
    private const int MissingNicknameChangeProductError = 25;

    [HttpPost(Name = "Redeem Legacy Client Nickname Change")]
    public async Task<IActionResult> ChangeNickname()
    {
        string cookie = Request.Form["cookie"].ToString();
        string? authenticatedAccountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (authenticatedAccountName is null)
        {
            Logger.LogWarning("Nickname Change Request Used An Invalid Session Cookie From {IPAddress}",
                Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN");

            return ClientFailure(AccountInformationError);
        }

        RedisValue lockToken = Guid.CreateVersion7().ToString();
        TimeSpan lockExpiration = TimeSpan.FromSeconds(30);

        while (await DistributedCache.LockTakeAsync(AccountNameAllocation.LockKey, lockToken, lockExpiration) is false)
            await Task.Delay(TimeSpan.FromMilliseconds(25), HttpContext.RequestAborted);

        try
        {
            return await ChangeNickname(cookie, authenticatedAccountName);
        }

        finally
        {
            await DistributedCache.LockReleaseAsync(AccountNameAllocation.LockKey, lockToken);
        }
    }

    private async Task<IActionResult> ChangeNickname(string cookie, string authenticatedAccountName)
    {
        if (int.TryParse(Request.Form["account_id"], out int requestedAccountID).Equals(false))
            return ClientFailure(AccountInformationError);

        Account? account = await MerrickContext.Accounts
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.Name.Equals(authenticatedAccountName));

        if (account is null)
        {
            Logger.LogError("[BUG] Session Cookie Resolved To Non-Existent Account {AccountName} During A Nickname Change", authenticatedAccountName);

            return ClientFailure(AccountInformationError);
        }

        int? errorCode = await UserInventoryTransaction.ExecuteForAccount(MerrickContext, account.ID,
            lockedAccount => ChangeNickname(lockedAccount, requestedAccountID), HttpContext.RequestAborted);

        if (errorCode is not null)
            return ClientFailure(errorCode.Value);

        await SynchroniseRenamedAccountCache(cookie, authenticatedAccountName, Request.Form["nickname"].ToString());

        return ClientSuccess();
    }

    private async Task<int?> ChangeNickname(Account account, int requestedAccountID)
    {
        if (account.ID != requestedAccountID)
        {
            Logger.LogWarning("Nickname Change Account ID Mismatch For {AccountName} (Expected {AccountID}, Received {RequestedAccountID})",
                account.Name, account.ID, requestedAccountID);

            return AccountInformationError;
        }

        string nickname = Request.Form["nickname"].ToString();
        string confirmedNickname = Request.Form["confirmNickname"].ToString();

        if (string.IsNullOrWhiteSpace(nickname)
            || nickname.Length is < MinimumAccountNameLength or > MaximumAccountNameLength
            || nickname.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Equals(false))
        {
            return InvalidNicknameError;
        }

        if (nickname.Equals(confirmedNickname, StringComparison.Ordinal).Equals(false))
            return NicknameConfirmationError;

        if (char.IsAsciiDigit(nickname[0])
            || nickname.StartsWith("S2", StringComparison.OrdinalIgnoreCase)
            || nickname.StartsWith("FB", StringComparison.OrdinalIgnoreCase)
            || nickname.StartsWith("Frostburn", StringComparison.OrdinalIgnoreCase))
        {
            return ForbiddenNicknamePrefixError;
        }

        if (await MerrickContext.Accounts.AnyAsync(candidate => candidate.Name.Equals(nickname)))
            return NicknameAlreadyExistsError;

        string submittedPasswordHash = Request.Form["password"].ToString();
        string computedPasswordHash = SRPAuthenticationHandlers.ComputeSRPPasswordHash(submittedPasswordHash, account.User.SRPPasswordSalt, passwordIsHashed: true);

        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedPasswordHash), Encoding.UTF8.GetBytes(account.User.SRPPasswordHash)).Equals(false))
            return InvalidPasswordError;

        StoreItem? nicknameChangeProduct = JSONConfiguration.StoreItemsConfiguration.GetByID(NicknameChangeProductID);

        if (nicknameChangeProduct is null)
        {
            Logger.LogError("[BUG] Nickname Change Store Product With ID {ProductID} Was Not Found In Store Configuration", NicknameChangeProductID);

            return InternalError;
        }

        if (account.User.OwnedStoreItems.Contains(nicknameChangeProduct.PrefixedCode).Equals(false))
            return MissingNicknameChangeProductError;

        string previousAccountName = account.Name;

        List<Account> accountsWithSocialReference = await MerrickContext.Accounts
            .Where(candidate => candidate.FriendedPeers.Any(peer => peer.ID == account.ID)
                || candidate.IgnoredPeers.Any(peer => peer.ID == account.ID)
                || candidate.BannedPeers.Any(peer => peer.ID == account.ID))
            .ToListAsync();

        foreach (Account referencingAccount in accountsWithSocialReference)
        {
            foreach (FriendedPeer peer in referencingAccount.FriendedPeers.Where(peer => peer.ID == account.ID))
                peer.Name = nickname;

            foreach (IgnoredPeer peer in referencingAccount.IgnoredPeers.Where(peer => peer.ID == account.ID))
                peer.Name = nickname;

            foreach (BannedPeer peer in referencingAccount.BannedPeers.Where(peer => peer.ID == account.ID))
                peer.Name = nickname;
        }

        account.Name = nickname;
        account.User.OwnedStoreItems.Remove(nicknameChangeProduct.PrefixedCode);

        try
        {
            // The account identity, every current social-name snapshot, and product redemption commit atomically.
            await MerrickContext.SaveChangesAsync();
        }

        catch (DbUpdateException exception)
        {
            Logger.LogError(exception, "Unable To Commit Nickname Change For Account {AccountName} (ID: {AccountID}) To {Nickname}",
                previousAccountName, account.ID, nickname);

            return StoreTransactionError;
        }

        Logger.LogInformation("Account {PreviousAccountName} (ID: {AccountID}) Consumed Store Product {ProductID} And Changed Nickname To {Nickname}",
            previousAccountName, account.ID, NicknameChangeProductID, nickname);

        return null;
    }

    private async Task SynchroniseRenamedAccountCache(string cookie, string previousAccountName, string newAccountName)
    {
        try
        {
            await DistributedCache.RenameAccountSessionCookies(previousAccountName, newAccountName);

            // Ensure the requesting cookie is remapped even if it was created or refreshed while the indexed cookies were being updated.
            await DistributedCache.SetAccountNameForSessionCookie(cookie, newAccountName);

            await DistributedCache.RemoveSRPAuthenticationSessionData(previousAccountName);
            await DistributedCache.RemoveSRPAuthenticationSessionData(newAccountName);
            await DistributedCache.RemoveSRPAuthenticationSystemInformation(previousAccountName);
            await DistributedCache.RemoveSRPAuthenticationSystemInformation(newAccountName);
        }

        catch (Exception exception)
        {
            // The committed database identity is authoritative. Cache entries expire naturally and must not make a completed redemption appear to have failed.
            Logger.LogError(exception, "Nickname Change From {PreviousAccountName} To {NewAccountName} Committed, But Authentication Cache Synchronisation Failed",
                previousAccountName, newAccountName);
        }

        try
        {
            // Chat sessions are keyed by the old name and retain an in-memory account snapshot, so terminate them and require the client to reconnect with the new identity.
            await DistributedCache.PublishAccountLogout(previousAccountName);
        }

        catch (Exception exception)
        {
            Logger.LogError(exception, "Nickname Change From {PreviousAccountName} To {NewAccountName} Committed, But The Old Chat Session Could Not Be Terminated",
                previousAccountName, newAccountName);
        }
    }

    private IActionResult ClientSuccess()
        => Ok(PhpSerialization.Serialize(new Dictionary<string, object>
        {
            ["success"] = 1,
            ["errors"] = string.Empty
        }));

    private IActionResult ClientFailure(int errorCode)
        => Ok(PhpSerialization.Serialize(new Dictionary<string, object>
        {
            ["success"] = 0,
            ["errors"] = $"nickname:{errorCode}"
        }));
}
