using MERRICK.DatabaseContext.Entities.Relational;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Accounts;

/// <summary>
///     Integration tests for the legacy <c>/client_require_nickchange.php</c> nickname-change form.
/// </summary>
public sealed class NicknameChangeTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const int NicknameChangeProductID = 161;
    private const string Password = "NicknamePassword123!";

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task A_Sub_Account_Is_Renamed_Without_Losing_Its_Identity_And_The_Product_Is_Consumed()
    {
        SeededSession session = await SeedSubAccountSession();
        string secondCookie = Guid.NewGuid().ToString("N");

        IDatabase distributedCache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        await distributedCache.SetAccountNameForSessionCookie(secondCookie, session.AccountName);
        await SeedStaleAuthenticationCache(distributedCache, session.AccountName, "RenamedSub");
        await SeedSocialReferences(session.AccountID, session.AccountName);

        TaskCompletionSource<string> logoutNotification = new (TaskCreationOptions.RunContinuationsAsynchronously);
        ISubscriber subscriber = distributedCache.Multiplexer.GetSubscriber();
        RedisChannel logoutChannel = RedisChannel.Literal(DistributedCacheExtensions.AccountLogoutChannel);

        await subscriber.SubscribeAsync(logoutChannel, (_, value) =>
        {
            if (value.ToString() == session.AccountName)
                logoutNotification.TrySetResult(session.AccountName);
        });

        IDictionary<object, object> response;
        string loggedOutAccountName;

        try
        {
            response = await SendRequest(session.Cookie, session.AccountID, "RenamedSub", "RenamedSub");
            loggedOutAccountName = await logoutNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        finally
        {
            await subscriber.UnsubscribeAsync(logoutChannel);
        }

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        User user = await databaseContext.Users
            .Include(candidate => candidate.Accounts)
            .SingleAsync(candidate => candidate.ID == session.UserID);

        Account renamedAccount = user.Accounts.Single(candidate => candidate.ID == session.AccountID);
        Account socialAccount = await databaseContext.Accounts.SingleAsync(candidate => candidate.Name == "SocialPeer");

        int statisticsCount = await databaseContext.AccountStatistics.CountAsync(statistics => statistics.AccountID == session.AccountID);
        bool masteryExists = await databaseContext.Masteries.AnyAsync(mastery => mastery.AccountID == session.AccountID);

        string? firstCookieAccountName = await distributedCache.GetAccountNameForSessionCookie(session.Cookie);
        string? secondCookieAccountName = await distributedCache.GetAccountNameForSessionCookie(secondCookie);

        SRPAuthenticationSessionDataStageOne? oldAuthentication = await distributedCache.GetSRPAuthenticationSessionData(session.AccountName);
        SRPAuthenticationSessionDataStageOne? newAuthentication = await distributedCache.GetSRPAuthenticationSessionData("RenamedSub");
        string? oldSystemInformation = await distributedCache.GetSRPAuthenticationSystemInformation(session.AccountName);
        string? newSystemInformation = await distributedCache.GetSRPAuthenticationSystemInformation("RenamedSub");

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(1);
            await Assert.That(response["errors"]?.ToString()).IsEmpty();
            await Assert.That(renamedAccount.Name).IsEqualTo("RenamedSub");
            await Assert.That(renamedAccount.ID).IsEqualTo(session.AccountID);
            await Assert.That(renamedAccount.IsMain).IsFalse();
            await Assert.That(user.Accounts.Single(candidate => candidate.IsMain).Name).IsEqualTo("NickOwner");
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetNicknameChangeProduct().PrefixedCode);
            await Assert.That(statisticsCount).IsEqualTo(Enum.GetValues<AccountStatisticsType>().Length);
            await Assert.That(masteryExists).IsTrue();

            await Assert.That(socialAccount.FriendedPeers.Single(peer => peer.ID == session.AccountID).Name).IsEqualTo("RenamedSub");
            await Assert.That(socialAccount.IgnoredPeers.Single(peer => peer.ID == session.AccountID).Name).IsEqualTo("RenamedSub");
            await Assert.That(socialAccount.BannedPeers.Single(peer => peer.ID == session.AccountID).Name).IsEqualTo("RenamedSub");

            await Assert.That(firstCookieAccountName).IsEqualTo("RenamedSub");
            await Assert.That(secondCookieAccountName).IsEqualTo("RenamedSub");
            await Assert.That(oldAuthentication).IsNull();
            await Assert.That(newAuthentication).IsNull();
            await Assert.That(oldSystemInformation).IsNull();
            await Assert.That(newSystemInformation).IsNull();
            await Assert.That(loggedOutAccountName).IsEqualTo(session.AccountName);
        }

        SRPAuthenticationData authentication = await new SRPAuthenticationService(webApplicationFactory).PerformFullAuthentication(renamedAccount, Password);
        await Assert.That(authentication.Success).IsTrue();
    }

    [Test]
    public async Task A_Wrong_Password_Is_Rejected_Without_Renaming_Or_Consuming_The_Product()
    {
        SeededSession session = await SeedMainAccountSession("nick.wrong-password@kongor.com", "NickPassword", ownsProduct: true);

        IDictionary<object, object> response = await SendRequest(session.Cookie, session.AccountID, "NewPassword", "NewPassword", "WrongPassword123!");
        (Account account, User user) = await LoadAccountAndUser(session.AccountID, session.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:9");
            await Assert.That(account.Name).IsEqualTo("NickPassword");
            await Assert.That(user.OwnedStoreItems).Contains(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    [Test]
    [Arguments("1Starts", 2)]
    [Arguments("S2Player", 2)]
    [Arguments("fbPlayer", 2)]
    [Arguments("FrostburnX", 2)]
    [Arguments("Bad Name", 3)]
    [Arguments("abc", 3)]
    [Arguments("FarTooLongName", 3)]
    public async Task Invalid_Or_Reserved_Nicknames_Are_Rejected(string requestedNickname, int expectedErrorCode)
    {
        SeededSession session = await SeedMainAccountSession("nick.validation@kongor.com", "NickValid", ownsProduct: true);

        IDictionary<object, object> response = await SendRequest(session.Cookie, session.AccountID, requestedNickname, requestedNickname);
        (Account account, User user) = await LoadAccountAndUser(session.AccountID, session.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo($"nickname:{expectedErrorCode}");
            await Assert.That(account.Name).IsEqualTo("NickValid");
            await Assert.That(user.OwnedStoreItems).Contains(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Duplicate_Nickname_And_A_Mismatched_Confirmation_Are_Rejected()
    {
        await SeedMainAccountSession("nick.existing@kongor.com", "ExistingNick", ownsProduct: false);
        SeededSession session = await SeedMainAccountSession("nick.duplicate@kongor.com", "NickDuplicate", ownsProduct: true);

        IDictionary<object, object> duplicateResponse = await SendRequest(session.Cookie, session.AccountID, "existingnick", "existingnick");
        IDictionary<object, object> confirmationResponse = await SendRequest(session.Cookie, session.AccountID, "WantedNick", "OtherNick");
        (Account account, User user) = await LoadAccountAndUser(session.AccountID, session.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(duplicateResponse["errors"]?.ToString()).IsEqualTo("nickname:0");
            await Assert.That(confirmationResponse["errors"]?.ToString()).IsEqualTo("nickname:8");
            await Assert.That(account.Name).IsEqualTo("NickDuplicate");
            await Assert.That(user.OwnedStoreItems).Contains(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Request_Must_Have_A_Valid_Cookie_And_Matching_Account_ID()
    {
        SeededSession requester = await SeedMainAccountSession("nick.requester@kongor.com", "NickRequest", ownsProduct: true);
        SeededSession target = await SeedMainAccountSession("nick.target@kongor.com", "NickTarget", ownsProduct: true);

        IDictionary<object, object> invalidCookieResponse = await SendRequest("forged-cookie", requester.AccountID, "ForgedNick", "ForgedNick");
        IDictionary<object, object> mismatchedAccountResponse = await SendRequest(requester.Cookie, target.AccountID, "StolenNick", "StolenNick");
        (Account requesterAccount, User requesterUser) = await LoadAccountAndUser(requester.AccountID, requester.UserID);
        (Account targetAccount, User targetUser) = await LoadAccountAndUser(target.AccountID, target.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(invalidCookieResponse["errors"]?.ToString()).IsEqualTo("nickname:4");
            await Assert.That(mismatchedAccountResponse["errors"]?.ToString()).IsEqualTo("nickname:4");
            await Assert.That(requesterAccount.Name).IsEqualTo("NickRequest");
            await Assert.That(targetAccount.Name).IsEqualTo("NickTarget");
            await Assert.That(requesterUser.OwnedStoreItems).Contains(GetNicknameChangeProduct().PrefixedCode);
            await Assert.That(targetUser.OwnedStoreItems).Contains(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task Each_Successful_Rename_Requires_A_New_Product_Purchase()
    {
        SeededSession session = await SeedMainAccountSession("nick.purchase@kongor.com", "NickPurchase", ownsProduct: true);

        IDictionary<object, object> firstResponse = await SendRequest(session.Cookie, session.AccountID, "FirstRename", "FirstRename");
        IDictionary<object, object> secondResponse = await SendRequest(session.Cookie, session.AccountID, "SecondRename", "SecondRename");
        (Account account, User user) = await LoadAccountAndUser(session.AccountID, session.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(firstResponse["success"])).IsEqualTo(1);
            await Assert.That(secondResponse["errors"]?.ToString()).IsEqualTo("nickname:25");
            await Assert.That(account.Name).IsEqualTo("FirstRename");
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task Concurrent_Requests_Cannot_Spend_One_Product_Twice()
    {
        SeededSession session = await SeedMainAccountSession("nick.concurrent@kongor.com", "NickRace", ownsProduct: true);

        Task<IDictionary<object, object>> firstRequest = SendRequest(session.Cookie, session.AccountID, "RaceNameOne", "RaceNameOne");
        Task<IDictionary<object, object>> secondRequest = SendRequest(session.Cookie, session.AccountID, "RaceNameTwo", "RaceNameTwo");

        IDictionary<object, object>[] responses = await Task.WhenAll(firstRequest, secondRequest);
        (Account account, User user) = await LoadAccountAndUser(session.AccountID, session.UserID);

        using (Assert.Multiple())
        {
            await Assert.That(responses.Count(response => Convert.ToInt32(response["success"]) == 1)).IsEqualTo(1);
            await Assert.That(responses.Count(response => Convert.ToInt32(response["success"]) == 0)).IsEqualTo(1);
            await Assert.That(account.Name is "RaceNameOne" or "RaceNameTwo").IsTrue();
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetNicknameChangeProduct().PrefixedCode);
        }
    }

    private async Task<SeededSession> SeedSubAccountSession()
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account mainAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("nick.success@kongor.com", "NickOwner", Password);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        Account persistedMainAccount = await databaseContext.Accounts.Include(account => account.User).SingleAsync(account => account.ID == mainAccount.ID);

        Account subAccount = new ()
        {
            Name = "OriginalSub",
            User = persistedMainAccount.User,
            IsMain = false
        };

        persistedMainAccount.User.Accounts.Add(subAccount);
        persistedMainAccount.User.OwnedStoreItems.Add(GetNicknameChangeProduct().PrefixedCode);
        await databaseContext.SaveChangesAsync();

        string cookie = Guid.NewGuid().ToString("N");
        await webApplicationFactory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, subAccount.Name);

        return new SeededSession(cookie, subAccount.ID, persistedMainAccount.User.ID, subAccount.Name);
    }

    private async Task<SeededSession> SeedMainAccountSession(string emailAddress, string accountName, bool ownsProduct)
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account account, string _) = await authenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, Password);

        if (ownsProduct)
            await GrantNicknameChangeProduct(account.User.ID);

        string cookie = Guid.NewGuid().ToString("N");
        await webApplicationFactory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, accountName);

        return new SeededSession(cookie, account.ID, account.User.ID, accountName);
    }

    private async Task GrantNicknameChangeProduct(int userID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        User user = await databaseContext.Users.SingleAsync(candidate => candidate.ID == userID);
        string productCode = GetNicknameChangeProduct().PrefixedCode;

        if (user.OwnedStoreItems.Contains(productCode).Equals(false))
            user.OwnedStoreItems.Add(productCode);

        await databaseContext.SaveChangesAsync();
    }

    private async Task SeedSocialReferences(int accountID, string accountName)
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account socialAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("nick.social@kongor.com", "SocialPeer", Password);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        Account persistedSocialAccount = await databaseContext.Accounts.SingleAsync(candidate => candidate.ID == socialAccount.ID);

        persistedSocialAccount.FriendedPeers.Add(new FriendedPeer { ID = accountID, Name = accountName, ClanTag = null, FriendGroup = "Buddy" });
        persistedSocialAccount.IgnoredPeers.Add(new IgnoredPeer { ID = accountID, Name = accountName });
        persistedSocialAccount.BannedPeers.Add(new BannedPeer { ID = accountID, Name = accountName, BanReason = "Test" });

        await databaseContext.SaveChangesAsync();
    }

    private static async Task SeedStaleAuthenticationCache(IDatabase distributedCache, string previousAccountName, string newAccountName)
    {
        SRPAuthenticationSessionDataStageOne staleAuthentication = new ()
        {
            ClientPublicEphemeral = "client",
            ServerPublicEphemeral = "server",
            ServerPrivateEphemeral = "private",
            SessionSalt = "session",
            PasswordSalt = "password",
            LoginIdentifier = previousAccountName,
            PasswordHash = "hash",
            Verifier = "verifier"
        };

        await distributedCache.SetSRPAuthenticationSessionData(previousAccountName, staleAuthentication);
        await distributedCache.SetSRPAuthenticationSessionData(newAccountName, staleAuthentication);
        await distributedCache.SetSRPAuthenticationSystemInformation(previousAccountName, "old-system-information");
        await distributedCache.SetSRPAuthenticationSystemInformation(newAccountName, "new-system-information");
    }

    private async Task<(Account Account, User User)> LoadAccountAndUser(int accountID, int userID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        Account account = await databaseContext.Accounts.SingleAsync(candidate => candidate.ID == accountID);
        User user = await databaseContext.Users.SingleAsync(candidate => candidate.ID == userID);

        return (account, user);
    }

    private async Task<IDictionary<object, object>> SendRequest(string cookie, int accountID, string nickname, string confirmedNickname, string password = Password)
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            ["nickname"] = nickname,
            ["confirmNickname"] = confirmedNickname,
            ["account_id"] = accountID.ToString(),
            ["password"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant(),
            ["cookie"] = cookie
        };

        using HttpResponseMessage response = await httpClient.PostAsync("client_require_nickchange.php", new FormUrlEncodedContent(formData));
        response.EnsureSuccessStatusCode();

        string responseBody = await response.Content.ReadAsStringAsync();

        return PhpSerialization.Deserialize(responseBody) as IDictionary<object, object>
            ?? throw new InvalidOperationException("Response Was Not A PHP-Serialised Dictionary");
    }

    private static StoreItem GetNicknameChangeProduct()
        => JSONConfiguration.StoreItemsConfiguration.GetByID(NicknameChangeProductID)
            ?? throw new InvalidOperationException($"Nickname Change Store Product {NicknameChangeProductID} Was Not Found");

    private sealed record SeededSession(string Cookie, int AccountID, int UserID, string AccountName);
}
