namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Accounts;

/// <summary>
///     Integration tests for the legacy <c>/client_require_identity.php</c> sub-account creation form.
/// </summary>
public sealed class SubAccountCreationTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const int SubAccountProductID = 162;

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task A_Purchased_Sub_Account_Is_Created_And_The_Product_Is_Consumed()
    {
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.create@kongor.com", "SubCreate", ownsSubAccountProduct: true);

        IDictionary<object, object> response = await SendRequest(cookie, accountID, "SubCreated", "SubCreated", password: "DeliberatelyIncorrectPassword");

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        User user = await databaseContext.Users
            .Include(candidate => candidate.Accounts)
            .SingleAsync(candidate => candidate.ID == userID);

        Account subAccount = user.Accounts.Single(account => account.Name.Equals("SubCreated"));
        StoreItem product = GetSubAccountProduct();

        int statisticsCount = await databaseContext.AccountStatistics.CountAsync(statistics => statistics.AccountID == subAccount.ID);
        bool masteryExists = await databaseContext.Masteries.AnyAsync(mastery => mastery.AccountID == subAccount.ID);
        bool masteryRewardsExist = await databaseContext.MasteryRewards.AnyAsync(rewards => rewards.AccountID == subAccount.ID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(1);
            await Assert.That(response["errors"]?.ToString()).IsEmpty();
            await Assert.That(subAccount.IsMain).IsFalse();
            await Assert.That(subAccount.User.ID).IsEqualTo(userID);
            await Assert.That(user.OwnedStoreItems).DoesNotContain(product.PrefixedCode);
            await Assert.That(statisticsCount).IsEqualTo(Enum.GetValues<AccountStatisticsType>().Length);
            await Assert.That(masteryExists).IsTrue();
            await Assert.That(masteryRewardsExist).IsTrue();
        }
    }

    [Test]
    public async Task Each_Successful_Creation_Requires_A_New_Product_Purchase()
    {
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.consume@kongor.com", "SubConsume", ownsSubAccountProduct: true);

        IDictionary<object, object> firstResponse = await SendRequest(cookie, accountID, "FirstSub", "FirstSub");
        IDictionary<object, object> responseWithoutRepurchase = await SendRequest(cookie, accountID, "SecondSub", "SecondSub");

        await GrantSubAccountProduct(userID);

        IDictionary<object, object> responseAfterRepurchase = await SendRequest(cookie, accountID, "SecondSub", "SecondSub");

        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(firstResponse["success"])).IsEqualTo(1);
            await Assert.That(Convert.ToInt32(responseWithoutRepurchase["success"])).IsEqualTo(0);
            await Assert.That(responseWithoutRepurchase["errors"]?.ToString()).IsEqualTo("nickname:25");
            await Assert.That(Convert.ToInt32(responseAfterRepurchase["success"])).IsEqualTo(1);
            await Assert.That(user.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubConsume", "FirstSub", "SecondSub"]);
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetSubAccountProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task Concurrent_Creation_Requests_Cannot_Spend_One_Product_Twice()
    {
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.concurrent@kongor.com", "SubConcurrent", ownsSubAccountProduct: true);

        Task<IDictionary<object, object>> firstRequest = SendRequest(cookie, accountID, "RaceSubOne", "RaceSubOne");
        Task<IDictionary<object, object>> secondRequest = SendRequest(cookie, accountID, "RaceSubTwo", "RaceSubTwo");

        IDictionary<object, object>[] responses = await Task.WhenAll(firstRequest, secondRequest);
        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(responses.Count(response => Convert.ToInt32(response["success"]) == 1)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response["errors"]?.ToString() == "nickname:25")).IsEqualTo(1);
            await Assert.That(user.Accounts).Count().IsEqualTo(2);
            await Assert.That(user.Accounts.Count(account => account.IsMain.Equals(false))).IsEqualTo(1);
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetSubAccountProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Duplicate_Nickname_Is_Rejected_Without_Consuming_The_Product()
    {
        (string _, int _, int _) = await SeedSession("subaccount.existing@kongor.com", "ExistingSub", ownsSubAccountProduct: false);
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.duplicate@kongor.com", "SubDuplicate", ownsSubAccountProduct: true);

        IDictionary<object, object> response = await SendRequest(cookie, accountID, "ExistingSub", "ExistingSub");

        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:18");
            await Assert.That(user.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubDuplicate"]);
            await Assert.That(user.OwnedStoreItems).Contains(GetSubAccountProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task Mismatched_Nickname_Confirmation_Is_Rejected_Without_Consuming_The_Product()
    {
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.confirm@kongor.com", "SubConfirm", ownsSubAccountProduct: true);

        IDictionary<object, object> response = await SendRequest(cookie, accountID, "WantedSub", "OtherSub");

        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:23");
            await Assert.That(user.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubConfirm"]);
            await Assert.That(user.OwnedStoreItems).Contains(GetSubAccountProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task Creation_Without_A_Purchased_Product_Is_Rejected()
    {
        (string cookie, int accountID, int userID) = await SeedSession("subaccount.unowned@kongor.com", "SubUnowned", ownsSubAccountProduct: false);

        IDictionary<object, object> response = await SendRequest(cookie, accountID, "UnownedSub", "UnownedSub");

        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:25");
            await Assert.That(user.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubUnowned"]);
        }
    }

    [Test]
    public async Task An_Invalid_Cookie_Cannot_Create_A_Sub_Account()
    {
        (string _, int accountID, int userID) = await SeedSession("subaccount.cookie@kongor.com", "SubCookie", ownsSubAccountProduct: true);

        IDictionary<object, object> response = await SendRequest("forged-cookie", accountID, "ForgedSub", "ForgedSub");

        User user = await LoadUser(userID);

        using (Assert.Multiple())
        {
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:27");
            await Assert.That(user.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubCookie"]);
            await Assert.That(user.OwnedStoreItems).Contains(GetSubAccountProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Request_Cannot_Create_A_Sub_Account_For_Another_Account_ID()
    {
        (string requesterCookie, int requesterAccountID, int requesterUserID) = await SeedSession("subaccount.requester@kongor.com", "SubRequester", ownsSubAccountProduct: true);
        (string _, int targetAccountID, int targetUserID) = await SeedSession("subaccount.target@kongor.com", "SubTarget", ownsSubAccountProduct: true);

        IDictionary<object, object> response = await SendRequest(requesterCookie, targetAccountID, "StolenSub", "StolenSub");

        User requester = await LoadUser(requesterUserID);
        User target = await LoadUser(targetUserID);

        using (Assert.Multiple())
        {
            await Assert.That(requesterAccountID).IsNotEqualTo(targetAccountID);
            await Assert.That(Convert.ToInt32(response["success"])).IsEqualTo(0);
            await Assert.That(response["errors"]?.ToString()).IsEqualTo("nickname:27");
            await Assert.That(requester.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubRequester"]);
            await Assert.That(target.Accounts.Select(account => account.Name)).IsEquivalentTo(["SubTarget"]);
            await Assert.That(requester.OwnedStoreItems).Contains(GetSubAccountProduct().PrefixedCode);
            await Assert.That(target.OwnedStoreItems).Contains(GetSubAccountProduct().PrefixedCode);
        }
    }

    private async Task<(string Cookie, int AccountID, int UserID)> SeedSession(string emailAddress, string accountName, bool ownsSubAccountProduct)
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);

        (Account account, string _) = await authenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, "DoesNotMatter123!");

        if (ownsSubAccountProduct)
            await GrantSubAccountProduct(account.User.ID);

        string cookie = Guid.NewGuid().ToString("N");

        IDatabase distributedCache = webApplicationFactory.Services.GetRequiredService<IDatabase>();

        await distributedCache.SetAccountNameForSessionCookie(cookie, accountName);

        return (cookie, account.ID, account.User.ID);
    }

    private async Task GrantSubAccountProduct(int userID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        User user = await databaseContext.Users.SingleAsync(candidate => candidate.ID == userID);
        string productCode = GetSubAccountProduct().PrefixedCode;

        if (user.OwnedStoreItems.Contains(productCode).Equals(false))
            user.OwnedStoreItems.Add(productCode);

        await databaseContext.SaveChangesAsync();
    }

    private async Task<User> LoadUser(int userID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        return await databaseContext.Users
            .Include(candidate => candidate.Accounts)
            .SingleAsync(candidate => candidate.ID == userID);
    }

    private async Task<IDictionary<object, object>> SendRequest(string cookie, int accountID, string nickname, string confirmedNickname, string password = "IgnoredPassword")
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            ["nickname"] = nickname,
            ["confirmNickname"] = confirmedNickname,
            ["account_id"] = accountID.ToString(),
            ["password"] = password,
            ["cookie"] = cookie
        };

        HttpResponseMessage response = await httpClient.PostAsync("client_require_identity.php", new FormUrlEncodedContent(formData));

        response.EnsureSuccessStatusCode();

        string responseBody = await response.Content.ReadAsStringAsync();

        return PhpSerialization.Deserialize(responseBody) as IDictionary<object, object>
            ?? throw new InvalidOperationException("Response Was Not A PHP-Serialised Dictionary");
    }

    private static StoreItem GetSubAccountProduct()
        => JSONConfiguration.StoreItemsConfiguration.GetByID(SubAccountProductID)
            ?? throw new InvalidOperationException($"Sub-Account Store Product {SubAccountProductID} Was Not Found");
}
