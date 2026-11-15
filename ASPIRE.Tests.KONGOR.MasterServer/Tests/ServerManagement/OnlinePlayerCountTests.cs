namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.ServerManagement;

public sealed class OnlinePlayerCountTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Initialise() => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    [Arguments(true, 42)]
    [Arguments(false, 42)]
    [Arguments(true, 0)]
    [Arguments(false, 0)]
    public async Task A_Fresh_Count_Including_Zero_Is_Returned_Through_Both_Routes(bool queryStringRoute, int playerCount)
    {
        IDatabase cache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        OnlinePlayerCountSnapshot snapshot = new (playerCount, DateTimeOffset.UtcNow);
        await cache.StringSetAsync(OnlinePlayerCountSnapshot.CacheKey, JsonSerializer.Serialize(snapshot), TimeSpan.FromMinutes(1));

        using HttpResponseMessage response = await Request(queryStringRoute);
        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToInt32(body["available"])).IsEqualTo(1);
        await Assert.That(Convert.ToInt32(body["player_count"])).IsEqualTo(playerCount);
        await Assert.That(Convert.ToInt64(body["updated_at"])).IsEqualTo(snapshot.UpdatedAt.ToUnixTimeSeconds());
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(body.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("stale")]
    [Arguments("future")]
    [Arguments("negative")]
    [Arguments("malformed")]
    [Arguments("null")]
    public async Task An_Unavailable_Or_Invalid_Snapshot_Is_Not_Reported_As_Zero_Players(string state)
    {
        IDatabase cache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        string? cached = state switch
        {
            "missing"   => null,
            "stale"     => JsonSerializer.Serialize(new OnlinePlayerCountSnapshot(42, DateTimeOffset.UtcNow.AddMinutes(-1))),
            "future"    => JsonSerializer.Serialize(new OnlinePlayerCountSnapshot(42, DateTimeOffset.UtcNow.AddMinutes(1))),
            "negative"  => JsonSerializer.Serialize(new OnlinePlayerCountSnapshot(-1, DateTimeOffset.UtcNow)),
            "malformed" => "{invalid",
            _           => "null"
        };

        if (cached is not null)
            await cache.StringSetAsync(OnlinePlayerCountSnapshot.CacheKey, cached, TimeSpan.FromMinutes(5));

        using HttpResponseMessage response = await Request();
        IDictionary<object, object> body = await PlinkoTestsHelper.DeserialisePhpResponse(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToInt32(body["available"])).IsEqualTo(0);
        await Assert.That(Convert.ToInt32(body["player_count"])).IsEqualTo(-1);
        await Assert.That(Convert.ToInt64(body["updated_at"])).IsEqualTo(0L);
    }

    [Test]
    [Arguments("")]
    [Arguments("invalid")]
    public async Task A_Missing_Or_Invalid_Session_Cookie_Is_Rejected(string cookie)
    {
        using HttpResponseMessage response = await webApplicationFactory.CreateClient().PostAsync("client_requester.php?f=get_online_player_count",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["cookie"] = cookie }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Population_Polling_Does_Not_Consume_The_Normal_Game_API_Request_Allowance()
    {
        IDatabase cache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        string cookie = Guid.NewGuid().ToString();
        await cache.SetAccountNameForSessionCookie(cookie, "PopulationTest");
        using HttpClient client = webApplicationFactory.CreateClient();

        for (int index = 0; index < 101; index++)
        {
            using HttpResponseMessage population = await client.PostAsync("client_requester.php?f=get_online_player_count",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["cookie"] = cookie }));
            await Assert.That(population.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using HttpResponseMessage normal = await client.PostAsync("client_requester.php?f=client_events_info",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["cookie"] = cookie }));
        await Assert.That(normal.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Request(bool queryStringRoute = true)
    {
        string cookie = Guid.NewGuid().ToString();
        await webApplicationFactory.Services.GetRequiredService<IDatabase>().SetAccountNameForSessionCookie(cookie, "PopulationTest");
        Dictionary<string, string> form = new () { ["cookie"] = cookie };

        if (queryStringRoute is false)
            form["f"] = "get_online_player_count";

        return await webApplicationFactory.CreateClient().PostAsync(queryStringRoute ? "client_requester.php?f=get_online_player_count" : "client_requester.php", new FormUrlEncodedContent(form));
    }
}
