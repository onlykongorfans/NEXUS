namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class PlayersOnlineTests(ZORGATHIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Initialise() => webApplicationFactory.WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task Anonymous_Visitors_Get_Physical_Server_Counts_And_Only_The_Supported_Queue_Pools()
    {
        using HttpClient client = CreateClient();
        IDatabase cache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OnlinePlayerDetailsSnapshot snapshot = new(OnlinePlayerDetailsSnapshot.CurrentVersion, now.ToUnixTimeSeconds(), 3, 27,
            [new("AU", 1, 4), new("USW", 2, 0), new("UNKNOWN", 0, 0)],
            [
                new("AU", 19,
                [
                    new("caldavar", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL, 3),
                    new("caldavar", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL, 8),
                    new("midwars", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, 2),
                    new("riftwars", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS, 6)
                ]),
                new("US", 5, [new("caldavar", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL, 5),
                    new("midwars", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, 0)]),
                new("NEWERTH", 3, [new("midwars", (int) ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, 3)])
            ]);

        await cache.StringSetAsync(OnlinePlayerCountSnapshot.CacheKey, JsonSerializer.Serialize(new OnlinePlayerCountSnapshot(737, now)));
        await cache.StringSetAsync(OnlinePlayerDetailsSnapshot.CacheKey, JsonSerializer.Serialize(snapshot, OnlinePlayerDetailsSnapshot.JSONOptions));

        PlayersOnlineHeadline headline = (await client.GetFromJsonAsync<PlayersOnlineHeadline>("/PlayersOnline"))
            ?? throw new InvalidOperationException("The Headline Response Was Empty");
        PlayersOnlineDetails details = (await client.GetFromJsonAsync<PlayersOnlineDetails>("/PlayersOnline/Details"))
            ?? throw new InvalidOperationException("The Details Response Was Empty");

        using (Assert.Multiple())
        {
            await Assert.That(headline.PlayerCount).IsEqualTo(737);
            await Assert.That(details.GamesInProgress).IsEqualTo(3);
            await Assert.That(details.ServerLocations.Any(location => location == new PlayersOnlineLocation("USW", 2, 0))).IsTrue();
            await Assert.That(details.ServerLocations.Any(location => location.Location == "UNKNOWN")).IsTrue();
            await Assert.That(details.RankedPick.SequenceEqual([new("AU", 3), new("US", 5)])).IsTrue();
            await Assert.That(details.MidWars.SequenceEqual([new("AU", 2), new("US", 0), new("NEWERTH", 3)])).IsTrue();
        }
    }

    [Test]
    public async Task Expired_And_Missing_Details_Do_Not_Prevent_A_Fresh_Zero_Headline()
    {
        using HttpClient client = CreateClient();
        IDatabase cache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OnlinePlayerDetailsSnapshot expired = new(OnlinePlayerDetailsSnapshot.CurrentVersion,
            (now - OnlinePlayerCountSnapshot.MaximumAge - TimeSpan.FromSeconds(1)).ToUnixTimeSeconds(), 0, 0, [], []);

        await cache.StringSetAsync(OnlinePlayerCountSnapshot.CacheKey, JsonSerializer.Serialize(new OnlinePlayerCountSnapshot(0, now)));

        using HttpResponseMessage missingDetails = await client.GetAsync("/PlayersOnline/Details");
        await cache.StringSetAsync(OnlinePlayerDetailsSnapshot.CacheKey, JsonSerializer.Serialize(expired, OnlinePlayerDetailsSnapshot.JSONOptions));
        using HttpResponseMessage expiredDetails = await client.GetAsync("/PlayersOnline/Details");

        PlayersOnlineHeadline headline = (await client.GetFromJsonAsync<PlayersOnlineHeadline>("/PlayersOnline"))
            ?? throw new InvalidOperationException("The Headline Response Was Empty");

        using (Assert.Multiple())
        {
            await Assert.That(missingDetails.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(expiredDetails.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(headline.PlayerCount).IsEqualTo(0);
        }
    }

    private HttpClient CreateClient() => webApplicationFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}
