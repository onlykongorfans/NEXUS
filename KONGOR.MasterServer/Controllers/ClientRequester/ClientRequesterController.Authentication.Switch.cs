namespace KONGOR.MasterServer.Controllers.ClientRequester;

public partial class ClientRequesterController
{
    /// <summary>
    ///     Switches an authenticated game-client session to another identity belonging to the same user.
    ///     The existing session cookie authorises the switch, so no password or new SRP exchange is required.
    /// </summary>
    private async Task<IActionResult> HandleSwitchAuthentication()
    {
        string cookie = Request.Form["cookie"].ToString();
        string? requestedAccountName = Request.Form["login"];

        if (string.IsNullOrWhiteSpace(requestedAccountName))
            return BadRequest(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingLoginIdentifier)));

        if (int.TryParse(Request.Form["account_id"], out int requestedAccountID).Equals(false))
            return BadRequest(@"Missing Or Invalid Value For Form Parameter ""account_id""");

        string? authenticatedAccountName = await DistributedCache.GetAccountNameForSessionCookie(cookie);

        if (authenticatedAccountName is null)
            return Unauthorized(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound)));

        Account? authenticatedAccount = await MerrickContext.Accounts
            .Include(account => account.User)
            .SingleOrDefaultAsync(account => account.Name.Equals(authenticatedAccountName));

        if (authenticatedAccount is null)
        {
            Logger.LogError("[BUG] Session Cookie For Account {AccountName} Could Not Be Resolved During An Account Switch", authenticatedAccountName);

            return Unauthorized(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound)));
        }

        Account? targetAccount = await MerrickContext.Accounts
            .Include(account => account.User).ThenInclude(user => user.Accounts)
            .Include(account => account.Clan).ThenInclude(clan => clan!.Members)
            .Include(account => account.BannedPeers)
            .Include(account => account.FriendedPeers)
            .Include(account => account.IgnoredPeers)
            .SingleOrDefaultAsync(account => account.ID == requestedAccountID
                && account.Name.Equals(requestedAccountName)
                && account.User.ID == authenticatedAccount.User.ID);

        if (targetAccount is null)
        {
            Logger.LogWarning("Account {AccountName} (ID: {AccountID}) Attempted To Switch To An Unassociated Or Mismatched Account {RequestedAccountName} (ID: {RequestedAccountID})",
                authenticatedAccount.Name, authenticatedAccount.ID, requestedAccountName, requestedAccountID);

            return NotFound(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound)));
        }

        if (IsAccountUnavailableForClientAuthentication(targetAccount))
            return Unauthorized(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountIsDisabled, targetAccount.NameWithClanTag)));

        if (targetAccount.Type is AccountType.ServerHost)
            return Unauthorized(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.IsServerHostingAccount)));

        if (Request.HttpContext.Connection.RemoteIpAddress is null)
        {
            Logger.LogError("[BUG] Remote IP Address For Account Switch From {AccountName} To {TargetAccountName} Is NULL",
                authenticatedAccount.Name, targetAccount.Name);

            return UnprocessableEntity(PhpSerialization.Serialize(new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingIPAddress)));
        }

        string remoteIPAddress = Request.HttpContext.Connection.RemoteIpAddress.MapToIPv4().ToString();
        string chatServerHost = Environment.GetEnvironmentVariable("CHAT_SERVER_CLIENT_HOST")
            ?? Environment.GetEnvironmentVariable("CHAT_SERVER_HOST")
            ?? throw new NullReferenceException("Chat Server Client Host Is NULL");
        int chatServerClientConnectionsPort = int.Parse(Environment.GetEnvironmentVariable("CHAT_SERVER_PORT_CLIENT")
            ?? throw new NullReferenceException("Chat Server Client Connections Port Is NULL"));

        Dictionary<AccountStatisticsType, AccountStatistics> statisticsByType = await MerrickContext.AccountStatistics
            .Where(statistics => statistics.AccountID == targetAccount.ID).ToDictionaryAsync(statistics => statistics.Type);

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new ()
        {
            Account = targetAccount,
            Statistics = statisticsByType,
            ClanRoster = targetAccount.Clan?.Members ?? [],
            // Account Switching Is Authorised By The Existing Cookie And Has No SRP Challenge To Prove
            ServerProof = string.Empty,
            ClientIPAddress = remoteIPAddress,
            ChatServer = (chatServerHost, chatServerClientConnectionsPort),
            Notifications = await BuildLoginNotifications(targetAccount.ID)
        };

        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out string replacementCookie);

        targetAccount.TimestampLastActive = DateTimeOffset.UtcNow;

        await MerrickContext.SaveChangesAsync();

        // Store The Replacement Before Invalidating The Existing Cookie So A Cache Failure Cannot Leave The Client Without Any Valid Session
        await DistributedCache.SetAccountNameForSessionCookie(replacementCookie, targetAccount.Name);
        await DistributedCache.RemoveAccountNameForSessionCookie(cookie);
        await DistributedCache.PublishAccountLogout(authenticatedAccount.Name);

        Logger.LogInformation("Account {AccountName} (ID: {AccountID}) Switched To Account {TargetAccountName} (ID: {TargetAccountID})",
            authenticatedAccount.Name, authenticatedAccount.ID, targetAccount.Name, targetAccount.ID);

        return Ok(PhpSerialization.Serialize(response));
    }
}
