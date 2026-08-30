namespace KONGOR.MasterServer.Controllers.Accounts;

/// <summary>
///     Handles the legacy game-client form used to redeem a purchased sub-account product.
/// </summary>
[ApiController]
[Route("client_require_identity.php")]
[Consumes("application/x-www-form-urlencoded")]
public class SubAccountController(MerrickContext databaseContext, IDatabase distributedCache, ILogger<SubAccountController> logger) : ControllerBase
{
    private MerrickContext MerrickContext { get; } = databaseContext;
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    private const int SubAccountProductID = 162;
    private const int MinimumAccountNameLength = 4;
    private const int MaximumAccountNameLength = 12;

    private const int NicknameTooLongError = 16;
    private const int InvalidNicknameError = 17;
    private const int NicknameAlreadyExistsError = 18;
    private const int MissingNicknameError = 19;
    private const int NicknameConfirmationError = 23;
    private const int MissingSubAccountProductError = 25;
    private const int InternalError = 27;

    /// <summary>
    ///     Creates one sub-account for the authenticated user's purchased sub-account product.
    ///     The legacy client also submits a password, but it is deliberately ignored: the session cookie is the credential for this endpoint.
    /// </summary>
    [HttpPost(Name = "Create Legacy Client Sub-Account")]
    public async Task<IActionResult> CreateSubAccount()
    {
        string cookie = Request.Form["cookie"].ToString();
        string? authenticatedAccountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (authenticatedAccountName is null)
        {
            Logger.LogWarning("Sub-Account Creation Request Used An Invalid Session Cookie From {IPAddress}",
                Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN");

            return ClientFailure(InternalError);
        }

        RedisKey creationLockKey = "SUB-ACCOUNT-CREATION-LOCK";
        RedisValue creationLockToken = Guid.CreateVersion7().ToString();
        TimeSpan creationLockExpiration = TimeSpan.FromSeconds(30);

        while (await DistributedCache.LockTakeAsync(creationLockKey, creationLockToken, creationLockExpiration) is false)
            await Task.Delay(TimeSpan.FromMilliseconds(25), HttpContext.RequestAborted);

        try
        {
            return await CreateSubAccount(authenticatedAccountName);
        }

        finally
        {
            await DistributedCache.LockReleaseAsync(creationLockKey, creationLockToken);
        }
    }

    private async Task<IActionResult> CreateSubAccount(string authenticatedAccountName)
    {
        if (int.TryParse(Request.Form["account_id"], out int requestedAccountID).Equals(false))
            return ClientFailure(InternalError);

        Account? authenticatedAccount = await MerrickContext.Accounts
            .Include(account => account.User).ThenInclude(user => user.Accounts)
            .SingleOrDefaultAsync(account => account.Name.Equals(authenticatedAccountName));

        if (authenticatedAccount is null)
        {
            Logger.LogError("[BUG] Session Cookie Resolved To Non-Existent Account {AccountName}", authenticatedAccountName);

            return ClientFailure(InternalError);
        }

        if (authenticatedAccount.ID != requestedAccountID)
        {
            Logger.LogWarning("Sub-Account Creation Account ID Mismatch For {AccountName} (Expected {AccountID}, Received {RequestedAccountID})",
                authenticatedAccount.Name, authenticatedAccount.ID, requestedAccountID);

            return ClientFailure(InternalError);
        }

        string nickname = Request.Form["nickname"].ToString();
        string confirmedNickname = Request.Form["confirmNickname"].ToString();

        if (string.IsNullOrWhiteSpace(nickname))
            return ClientFailure(MissingNicknameError);

        if (string.IsNullOrWhiteSpace(confirmedNickname) || nickname.Equals(confirmedNickname, StringComparison.Ordinal).Equals(false))
            return ClientFailure(NicknameConfirmationError);

        if (nickname.Length > MaximumAccountNameLength)
            return ClientFailure(NicknameTooLongError);

        if (nickname.Length < MinimumAccountNameLength || nickname.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Equals(false))
            return ClientFailure(InvalidNicknameError);

        if (await MerrickContext.Accounts.AnyAsync(account => account.Name.Equals(nickname)))
            return ClientFailure(NicknameAlreadyExistsError);

        StoreItem? subAccountProduct = JSONConfiguration.StoreItemsConfiguration.GetByID(SubAccountProductID);

        if (subAccountProduct is null)
        {
            Logger.LogError("[BUG] Sub-Account Store Product With ID {ProductID} Was Not Found In Store Configuration", SubAccountProductID);

            return ClientFailure(InternalError);
        }

        User user = authenticatedAccount.User;

        if (user.OwnedStoreItems.Contains(subAccountProduct.PrefixedCode).Equals(false))
            return ClientFailure(MissingSubAccountProductError);

        Account subAccount = new ()
        {
            Name = nickname,
            User = user,
            IsMain = false
        };

        user.Accounts.Add(subAccount);
        user.OwnedStoreItems.Remove(subAccountProduct.PrefixedCode);

        await MerrickContext.SaveChangesAsync();

        Logger.LogInformation("Account {AccountName} (ID: {AccountID}) Consumed Store Product {ProductID} And Created Sub-Account {SubAccountName} (ID: {SubAccountID})",
            authenticatedAccount.Name, authenticatedAccount.ID, SubAccountProductID, subAccount.Name, subAccount.ID);

        return ClientSuccess();
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
