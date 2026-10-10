namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Statistics;

/// <summary>
///     Integration tests for the recent matches lookup of the client-requester endpoint, which backs the recent games search of the match replays screen and the latest match lookup of the match statistics screen.
/// </summary>
public sealed class MatchHistoryTests_Integration(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const string ClientRequesterRoute = "client_requester.php";

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Grab_Last_Matches_From_Nick_Without_A_Cookie_Returns_The_Most_Recent_Matches_First(bool queryStringRoute)
    {
        SRPAuthenticationService service = new (webApplicationFactory);

        (Account account, string _) = await service.CreateAccountWithSRPCredentials("recent.matches@kongor.com", "RecentMatches", "DoesNotMatter123!");

        // Match IDs Are Not Chronological, So The Older Match Is Given The Larger Match ID
        await SeedMatch(account, matchID: 2000, timestampRecorded: DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        await SeedMatch(account, matchID: 1000, timestampRecorded: DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        HttpClient client = webApplicationFactory.CreateClient();

        // The Client Submits This Request As A Form Without A Session Cookie
        string route = queryStringRoute ? $"{ClientRequesterRoute}?f=grab_last_matches_from_nick" : ClientRequesterRoute;
        Dictionary<string, string> form = new ()
        {
            ["nickname"] = "RecentMatches",
            ["hosttime"] = "123456"
        };

        if (queryStringRoute.Equals(false))
            form["f"] = "grab_last_matches_from_nick";

        using HttpResponseMessage response = await client.PostAsync(route, new FormUrlEncodedContent(form));

        await Assert.That(response.IsSuccessStatusCode).IsTrue();

        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);

        List<int> matchIDs = EnumeratePHPArrayValues(body["last_stats"]).Select(Convert.ToInt32).ToList();

        using (Assert.Multiple())
        {
            await Assert.That(matchIDs).IsEquivalentTo([1000, 2000], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(Convert.ToString(body["hosttime"])).IsEqualTo("123456");
        }
    }

    [Test]
    public async Task Recent_Matches_Are_Limited_To_50_With_Deterministic_Timestamp_Ties()
    {
        SRPAuthenticationService service = new (webApplicationFactory);
        (Account account, string _) = await service.CreateAccountWithSRPCredentials("recent.limit@kongor.com", "RecentLimit", "DoesNotMatter123!");
        DateTimeOffset timestampRecorded = DateTimeOffset.UtcNow;

        for (int matchID = 1; matchID <= 52; matchID++)
            await SeedMatch(account, matchID, timestampRecorded);

        using HttpResponseMessage response = await webApplicationFactory.CreateClient().PostAsync(ClientRequesterRoute, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "grab_last_matches_from_nick", ["nickname"] = account.Name
        }));

        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);
        List<int> matchIDs = EnumeratePHPArrayValues(body["last_stats"]).Select(Convert.ToInt32).ToList();
        await Assert.That(matchIDs).IsEquivalentTo(Enumerable.Range(3, 50).Reverse(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>
    ///     A deserialised PHP array is a list when its keys are consecutive integers and a dictionary otherwise, so both shapes are enumerated uniformly here.
    /// </summary>
    private static IReadOnlyList<object> EnumeratePHPArrayValues(object phpArray) => phpArray switch
    {
        IDictionary<object, object> dictionary => [.. dictionary.Values],
        IEnumerable<object> values              => [.. values],
        _                                       => throw new InvalidOperationException($@"Unexpected PHP Array Shape ""{phpArray.GetType()}""")
    };

    private async Task SeedMatch(Account account, int matchID, DateTimeOffset timestampRecorded)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        MatchStatistics matchStatistics = MatchDataHelper.BuildMatchStatistics(matchID);

        matchStatistics.TimestampRecorded = timestampRecorded;

        MatchParticipantStatistics participant = MatchDataHelper.BuildParticipant(account.ID, account.Name, groupNumber: 1, win: 1, matchID: matchID, publicMatch: 1, rankedMatch: 0);

        await databaseContext.MatchStatistics.AddAsync(matchStatistics);
        await databaseContext.MatchParticipantStatistics.AddAsync(participant);

        await databaseContext.SaveChangesAsync();
    }
}
