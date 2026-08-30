namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Statistics;

/// <summary>
///     Integration tests for redeeming the legacy client's Stat Reset product.
/// </summary>
public sealed class StatResetTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const int StatResetProductID = 163;
    private const string Password = "StatResetPassword123!";

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task Selected_Statistics_Are_Reset_And_The_Product_Is_Consumed()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.success@kongor.com", "ResetSuccess", ownsProduct: true);

        await SeedStatistics(accountID);

        JsonElement response = await SendRequest(cookie, accountID, new Dictionary<string, string>
        {
            ["public"] = "1",
            ["psr"] = "1",
            ["midwars"] = "1",
            ["mmmidwars"] = "0",
            ["connormalstats"] = "1"
        });

        (User user, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        AccountStatistics publicStatistics = statistics[AccountStatisticsType.Public];
        AccountStatistics normalStatistics = statistics[AccountStatisticsType.Matchmaking];
        AccountStatistics casualStatistics = statistics[AccountStatisticsType.MatchmakingCasual];
        AccountStatistics midWarsStatistics = statistics[AccountStatisticsType.MidWars];

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsTrue();
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetStatResetProduct().PrefixedCode);

            await Assert.That(publicStatistics.MatchesPlayed).IsEqualTo(0);
            await Assert.That(publicStatistics.SkillRating).IsEqualTo(1500.0);
            await Assert.That(publicStatistics.HeroStatistics.Heroes).IsEmpty();
            await Assert.That(publicStatistics.AwardStatistics.MVPAwards).IsEqualTo(0);

            await Assert.That(normalStatistics.MatchesPlayed).IsEqualTo(0);
            await Assert.That(normalStatistics.SkillRating).IsEqualTo(1500.0);
            await Assert.That(normalStatistics.PlacementMatchesData).IsEmpty();

            await Assert.That(midWarsStatistics.MatchesPlayed).IsEqualTo(0);
            await Assert.That(midWarsStatistics.SkillRating).IsEqualTo(1650.0);

            await Assert.That(casualStatistics.MatchesPlayed).IsEqualTo(25);
            await Assert.That(casualStatistics.SkillRating).IsEqualTo(1650.0);
            await Assert.That(casualStatistics.PlacementMatchesData).IsEqualTo("111111");
        }
    }

    [Test]
    public async Task Public_Statistics_Can_Be_Reset_Without_Resetting_Psr()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.public@kongor.com", "ResetPublic", ownsProduct: true);

        await SeedStatistics(accountID);

        JsonElement response = await SendRequest(cookie, accountID, new Dictionary<string, string> { ["public"] = "1" });
        (User _, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        AccountStatistics publicStatistics = statistics[AccountStatisticsType.Public];

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsTrue();
            await Assert.That(publicStatistics.MatchesPlayed).IsEqualTo(0);
            await Assert.That(publicStatistics.SkillRating).IsEqualTo(1650.0);
        }
    }

    [Test]
    public async Task Rating_Can_Be_Reset_Without_Resetting_Other_Statistics()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.rating@kongor.com", "ResetRating", ownsProduct: true);

        await SeedStatistics(accountID);

        JsonElement response = await SendRequest(cookie, accountID, new Dictionary<string, string>
        {
            ["smr"] = "1",
            ["casualmmr"] = "1",
            ["mmmidwars"] = "1"
        });

        (User _, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsTrue();
            await Assert.That(statistics[AccountStatisticsType.Matchmaking].MatchesPlayed).IsEqualTo(25);
            await Assert.That(statistics[AccountStatisticsType.Matchmaking].SkillRating).IsEqualTo(1500.0);
            await Assert.That(statistics[AccountStatisticsType.Matchmaking].PlacementMatchesData).IsEmpty();
            await Assert.That(statistics[AccountStatisticsType.MatchmakingCasual].MatchesPlayed).IsEqualTo(25);
            await Assert.That(statistics[AccountStatisticsType.MatchmakingCasual].SkillRating).IsEqualTo(1500.0);
            await Assert.That(statistics[AccountStatisticsType.MatchmakingCasual].PlacementMatchesData).IsEmpty();
            await Assert.That(statistics[AccountStatisticsType.MidWars].MatchesPlayed).IsEqualTo(25);
            await Assert.That(statistics[AccountStatisticsType.MidWars].SkillRating).IsEqualTo(1500.0);
        }
    }

    [Test]
    public async Task A_Wrong_Password_Is_Rejected_Without_Changing_Statistics_Or_Consuming_The_Product()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.password@kongor.com", "ResetPassword", ownsProduct: true);

        await SeedStatistics(accountID);

        JsonElement response = await SendRequest(cookie, accountID, new Dictionary<string, string> { ["public"] = "1" }, "WrongPassword123!");
        (User user, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(response.GetProperty("errors").GetString()).IsEqualTo("wrong password");
            await Assert.That(statistics[AccountStatisticsType.Public].MatchesPlayed).IsEqualTo(25);
            await Assert.That(user.OwnedStoreItems).Contains(GetStatResetProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Reset_Without_A_Purchased_Product_Is_Rejected_Without_Changing_Statistics()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.unowned@kongor.com", "ResetUnowned", ownsProduct: false);

        await SeedStatistics(accountID);

        JsonElement response = await SendRequest(cookie, accountID, new Dictionary<string, string> { ["public"] = "1" });
        (User _, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(response.GetProperty("errors").GetString()).IsEqualTo("not enough stats reset");
            await Assert.That(statistics[AccountStatisticsType.Public].MatchesPlayed).IsEqualTo(25);
        }
    }

    [Test]
    public async Task Concurrent_Reset_Requests_Cannot_Spend_One_Product_Twice()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.concurrent@kongor.com", "ResetRace", ownsProduct: true);

        await SeedStatistics(accountID);

        Dictionary<string, string> selection = new () { ["public"] = "1" };
        Task<JsonElement> firstRequest = SendRequest(cookie, accountID, selection);
        Task<JsonElement> secondRequest = SendRequest(cookie, accountID, selection);

        JsonElement[] responses = await Task.WhenAll(firstRequest, secondRequest);
        (User user, Dictionary<AccountStatisticsType, AccountStatistics> statistics) = await LoadState(userID, accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responses.Count(response => response.GetProperty("success").GetBoolean())).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.TryGetProperty("errors", out JsonElement error) && error.GetString() == "not enough stats reset")).IsEqualTo(1);
            await Assert.That(statistics[AccountStatisticsType.Public].MatchesPlayed).IsEqualTo(0);
            await Assert.That(user.OwnedStoreItems).DoesNotContain(GetStatResetProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Reset_Without_Any_Selected_Options_Is_Rejected_Without_Consuming_The_Product()
    {
        (string cookie, int accountID, int userID) = await SeedSession("statreset.options@kongor.com", "ResetOptions", ownsProduct: true);

        JsonElement response = await SendRequest(cookie, accountID, []);
        (User user, Dictionary<AccountStatisticsType, AccountStatistics> _) = await LoadState(userID, accountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(response.GetProperty("errors").GetString()).IsEqualTo("no options provided");
            await Assert.That(user.OwnedStoreItems).Contains(GetStatResetProduct().PrefixedCode);
        }
    }

    [Test]
    public async Task A_Request_Cannot_Reset_Another_Account_ID()
    {
        (string requesterCookie, int requesterAccountID, int requesterUserID) = await SeedSession("statreset.requester@kongor.com", "ResetRequester", ownsProduct: true);
        (string _, int targetAccountID, int targetUserID) = await SeedSession("statreset.target@kongor.com", "ResetTarget", ownsProduct: true);

        await SeedStatistics(requesterAccountID);
        await SeedStatistics(targetAccountID);

        JsonElement response = await SendRequest(requesterCookie, targetAccountID, new Dictionary<string, string> { ["public"] = "1" });

        (User requester, Dictionary<AccountStatisticsType, AccountStatistics> requesterStatistics) = await LoadState(requesterUserID, requesterAccountID);
        (User target, Dictionary<AccountStatisticsType, AccountStatistics> targetStatistics) = await LoadState(targetUserID, targetAccountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(response.GetProperty("errors").GetString()).IsEqualTo("internal error");
            await Assert.That(requesterStatistics[AccountStatisticsType.Public].MatchesPlayed).IsEqualTo(25);
            await Assert.That(targetStatistics[AccountStatisticsType.Public].MatchesPlayed).IsEqualTo(25);
            await Assert.That(requester.OwnedStoreItems).Contains(GetStatResetProduct().PrefixedCode);
            await Assert.That(target.OwnedStoreItems).Contains(GetStatResetProduct().PrefixedCode);
        }
    }

    private async Task<(string Cookie, int AccountID, int UserID)> SeedSession(string emailAddress, string accountName, bool ownsProduct)
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account account, string _) = await authenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, Password);

        if (ownsProduct)
        {
            using IServiceScope scope = webApplicationFactory.Services.CreateScope();
            MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            User user = await databaseContext.Users.SingleAsync(candidate => candidate.ID == account.User.ID);
            user.OwnedStoreItems.Add(GetStatResetProduct().PrefixedCode);
            await databaseContext.SaveChangesAsync();
        }

        string cookie = Guid.NewGuid().ToString("N");
        await webApplicationFactory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, accountName);

        return (cookie, account.ID, account.User.ID);
    }

    private async Task SeedStatistics(int accountID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        List<AccountStatistics> statistics = await databaseContext.AccountStatistics.Where(candidate => candidate.AccountID == accountID).ToListAsync();

        foreach (AccountStatistics row in statistics)
        {
            row.MatchesPlayed = 25;
            row.MatchesWon = 15;
            row.MatchesLost = 10;
            row.MatchesDisconnected = 2;
            row.MatchesConceded = 3;
            row.MatchesKicked = 1;
            row.SkillRating = 1650.0;
            row.HeroKills = 100;
            row.HeroAssists = 200;
            row.HeroDeaths = 50;
            row.WardsPlaced = 80;
            row.Smackdowns = 12;
            row.HeroStatistics.Heroes.Add(new HeroStats { HeroIdentifier = "Hero_Test", GamesPlayed = 25, HeroKills = 100 });
            row.AwardStatistics.MVPAwards = 4;

            if (row.Type is AccountStatisticsType.Matchmaking or AccountStatisticsType.MatchmakingCasual)
                row.PlacementMatchesData = "111111";
        }

        await databaseContext.SaveChangesAsync();
    }

    private async Task<(User User, Dictionary<AccountStatisticsType, AccountStatistics> Statistics)> LoadState(int userID, int accountID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        User user = await databaseContext.Users.SingleAsync(candidate => candidate.ID == userID);
        Dictionary<AccountStatisticsType, AccountStatistics> statistics = await databaseContext.AccountStatistics
            .Where(candidate => candidate.AccountID == accountID)
            .ToDictionaryAsync(candidate => candidate.Type);

        return (user, statistics);
    }

    private async Task<JsonElement> SendRequest(string cookie, int accountID, Dictionary<string, string> selections, string password = Password)
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new (selections)
        {
            ["account_id"] = accountID.ToString(),
            ["password"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant(),
            ["cookie"] = cookie
        };

        using HttpResponseMessage response = await httpClient.PostAsync("client_requester.php?f=reset_stats", new FormUrlEncodedContent(formData));
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    private static StoreItem GetStatResetProduct()
        => JSONConfiguration.StoreItemsConfiguration.GetByID(StatResetProductID)
            ?? throw new InvalidOperationException($"Stat Reset Store Product {StatResetProductID} Was Not Found");
}
