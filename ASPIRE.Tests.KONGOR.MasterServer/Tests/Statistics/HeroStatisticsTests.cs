namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Statistics;

/// <summary>
///     Integration tests for the legacy client's all-hero-statistics request.
/// </summary>
public sealed class HeroStatisticsTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const string ClientRequesterRoute = "client_requester.php?f=get_account_all_hero_stats";

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task A_Missing_Nickname_Uses_The_Account_From_The_Validated_Session_Cookie()
    {
        (Account account, string cookie) = await SeedAuthenticatedAccount("hero.session@kongor.com", "HeroSession");

        await SeedRankedHeroStatistics(account.ID, "Hero_SessionAccount");

        HttpResponseMessage response = await PostRequest(new Dictionary<string, string> { ["cookie"] = cookie });
        IDictionary<object, object> rankedHero = await GetOnlyRankedHero(response);

        using (Assert.Multiple())
        {
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
            await Assert.That(rankedHero["cli_name"]?.ToString()).IsEqualTo("Hero_SessionAccount");
        }
    }

    [Test]
    public async Task A_Supplied_Nickname_Continues_To_Select_That_Account()
    {
        (Account requester, string cookie) = await SeedAuthenticatedAccount("hero.requester@kongor.com", "HeroRequester");
        (Account target, string _) = await SeedAuthenticatedAccount("hero.target@kongor.com", "HeroTarget");

        await SeedRankedHeroStatistics(requester.ID, "Hero_Requester");
        await SeedRankedHeroStatistics(target.ID, "Hero_ExplicitTarget");

        HttpResponseMessage response = await PostRequest(new Dictionary<string, string>
        {
            ["cookie"] = cookie,
            ["nickname"] = target.Name
        });

        IDictionary<object, object> rankedHero = await GetOnlyRankedHero(response);

        using (Assert.Multiple())
        {
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
            await Assert.That(rankedHero["cli_name"]?.ToString()).IsEqualTo("Hero_ExplicitTarget");
        }
    }

    private async Task<(Account Account, string Cookie)> SeedAuthenticatedAccount(string emailAddress, string accountName)
    {
        SRPAuthenticationService service = new (webApplicationFactory);
        (Account account, string _) = await service.CreateAccountWithSRPCredentials(emailAddress, accountName, "DoesNotMatter123!");

        string cookie = Guid.NewGuid().ToString("N");
        await webApplicationFactory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, accountName);

        return (account, cookie);
    }

    private async Task SeedRankedHeroStatistics(int accountID, string heroIdentifier)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        AccountStatistics statistics = await databaseContext.AccountStatistics
            .SingleAsync(candidate => candidate.AccountID == accountID && candidate.Type == AccountStatisticsType.Matchmaking);

        statistics.HeroStatistics.Heroes.Add(new HeroStats
        {
            HeroIdentifier = heroIdentifier,
            GamesPlayed = 1,
            Wins = 1
        });

        await databaseContext.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> PostRequest(Dictionary<string, string> formFields)
    {
        HttpClient client = webApplicationFactory.CreateClient();
        return await client.PostAsync(ClientRequesterRoute, new FormUrlEncodedContent(formFields));
    }

    private static async Task<IDictionary<object, object>> GetOnlyRankedHero(HttpResponseMessage response)
    {
        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);
        IDictionary<object, object> allHeroStatistics = (IDictionary<object, object>) body["all_hero_stats"];
        IEnumerable<object> rankedHeroes = allHeroStatistics["ranked"] switch
        {
            IDictionary<object, object> dictionary => dictionary.Values,
            IEnumerable<object> values              => values,
            object value                            => [value]
        };

        return (IDictionary<object, object>) rankedHeroes.Single();
    }
}
