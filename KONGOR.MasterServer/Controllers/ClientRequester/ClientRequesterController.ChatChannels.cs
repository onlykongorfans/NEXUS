namespace KONGOR.MasterServer.Controllers.ClientRequester;

public partial class ClientRequesterController
{
    /// <summary>
    ///     Adds a chat channel to the authenticated account's auto-connect list.
    /// </summary>
    private async Task<IActionResult> AddAutoConnectChatChannel()
    {
        string chatChannelName = Request.Form["chatroom_name"].ToString();

        IActionResult? validationError = ValidateAutoConnectChatChannelName(chatChannelName);

        if (validationError is not null)
            return validationError;

        OneOf<Account, IActionResult> accountResult = await ResolveAutoConnectAccount();

        if (accountResult.IsT1)
            return accountResult.AsT1;

        Account account = accountResult.AsT0;
        bool channelWasAdded = account.AutoConnectChatChannels.Any(channelName => channelName.Equals(chatChannelName, StringComparison.OrdinalIgnoreCase)) is false;

        if (channelWasAdded)
        {
            account.AutoConnectChatChannels.Add(chatChannelName);

            await MerrickContext.SaveChangesAsync();
        }

        Logger.LogInformation(@"Account ""{AccountName}"" (ID: {AccountID}) Saved Chat Channel ""{ChatChannelName}"" To Its Auto-Connect List; Channel Was Newly Added: {ChannelWasAdded}",
            account.Name, account.ID, chatChannelName, channelWasAdded);

        return Ok(PhpSerialization.Serialize(new Dictionary<string, string> { { "add_room", "OK" } }));
    }

    /// <summary>
    ///     Removes a chat channel from the authenticated account's auto-connect list.
    /// </summary>
    private async Task<IActionResult> RemoveAutoConnectChatChannel()
    {
        string chatChannelName = Request.Form["chatroom_name"].ToString();

        IActionResult? validationError = ValidateAutoConnectChatChannelName(chatChannelName);

        if (validationError is not null)
            return validationError;

        OneOf<Account, IActionResult> accountResult = await ResolveAutoConnectAccount();

        if (accountResult.IsT1)
            return accountResult.AsT1;

        Account account = accountResult.AsT0;
        int removedChannelCount = account.AutoConnectChatChannels.RemoveAll(channelName => channelName.Equals(chatChannelName, StringComparison.OrdinalIgnoreCase));

        if (removedChannelCount > 0)
            await MerrickContext.SaveChangesAsync();

        Logger.LogInformation(@"Account ""{AccountName}"" (ID: {AccountID}) Removed Chat Channel ""{ChatChannelName}"" From Its Auto-Connect List; Removed Entry Count: {RemovedChannelCount}",
            account.Name, account.ID, chatChannelName, removedChannelCount);

        return Ok(PhpSerialization.Serialize(new Dictionary<string, string> { { "remove_room", "OK" } }));
    }

    /// <summary>
    ///     Clears every chat channel from the authenticated account's auto-connect list.
    /// </summary>
    private async Task<IActionResult> ClearAutoConnectChatChannels()
    {
        OneOf<Account, IActionResult> accountResult = await ResolveAutoConnectAccount();

        if (accountResult.IsT1)
            return accountResult.AsT1;

        Account account = accountResult.AsT0;
        int removedChannelCount = account.AutoConnectChatChannels.Count;

        if (removedChannelCount > 0)
        {
            account.AutoConnectChatChannels.Clear();

            await MerrickContext.SaveChangesAsync();
        }

        Logger.LogInformation(@"Account ""{AccountName}"" (ID: {AccountID}) Cleared Its Auto-Connect Chat Channel List; Removed Entry Count: {RemovedChannelCount}",
            account.Name, account.ID, removedChannelCount);

        return Ok(PhpSerialization.Serialize(new Dictionary<string, string> { { "clear_rooms", "OK" } }));
    }

    private async Task<OneOf<Account, IActionResult>> ResolveAutoConnectAccount()
    {
        if (int.TryParse(Request.Form["account_id"], out int requestedAccountID).Equals(false))
            return BadRequest(@"Missing Or Invalid Value For Form Parameter ""account_id""");

        string cookie = Request.Form["cookie"].ToString();
        string? accountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null)
            return Unauthorized(@"Unrecognised Session Cookie");

        Account? account = await MerrickContext.Accounts
            .SingleOrDefaultAsync(candidate => candidate.Name.Equals(accountName));

        if (account is null)
        {
            Logger.LogError(@"[BUG] Session Cookie Resolved To Non-Existent Account ""{AccountName}""", accountName);

            return Unauthorized(@"Unrecognised Session Account");
        }

        if (account.ID != requestedAccountID)
        {
            Logger.LogWarning(@"Auto-Connect Chat Channel Request Account ID Mismatch For ""{AccountName}"" (Expected {AccountID}, Received {RequestedAccountID})",
                account.Name, account.ID, requestedAccountID);

            return BadRequest(@"Value For Form Parameter ""account_id"" Does Not Match The Authenticated Account");
        }

        return account;
    }

    private IActionResult? ValidateAutoConnectChatChannelName(string chatChannelName)
    {
        if (string.IsNullOrWhiteSpace(chatChannelName))
            return BadRequest(@"Missing Value For Form Parameter ""chatroom_name""");

        if (chatChannelName.Length > ChatProtocol.CHAT_CHANNEL_MAX_LENGTH)
            return BadRequest($@"Value For Form Parameter ""chatroom_name"" Exceeds The Maximum Length Of {ChatProtocol.CHAT_CHANNEL_MAX_LENGTH}");

        return null;
    }
}
