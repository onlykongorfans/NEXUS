namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
///     Publishes the authenticated game-client population for the master server without exposing account identities.
/// </summary>
public sealed class OnlinePlayerCountPublisher(IDatabase distributedCache, ILogger<OnlinePlayerCountPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            try { await Publish(); }
            catch (RedisException exception) { logger.LogWarning(exception, "Failed To Publish Online Player Count"); }

            try { await Task.Delay(PublicationInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task Publish()
    {
        int count = Context.ClientChatSessions.Values
            .Where(session => session.Account is not null && session.Metadata is not null)
            .Select(session => session.Account.ID)
            .Distinct()
            .Count();

        OnlinePlayerCountSnapshot snapshot = new (count, DateTimeOffset.UtcNow);

        await distributedCache.StringSetAsync(OnlinePlayerCountSnapshot.CacheKey, JsonSerializer.Serialize(snapshot), OnlinePlayerCountSnapshot.MaximumAge);

        try
        {
            List<MatchServer> servers = await distributedCache.GetMatchServers();
            OnlinePlayerDetailsSnapshot details = OnlinePlayerDetails.Capture(servers, DateTimeOffset.UtcNow);
            string json = JsonSerializer.Serialize(details, OnlinePlayerDetailsSnapshot.JSONOptions);
            await distributedCache.StringSetAsync(OnlinePlayerDetailsSnapshot.CacheKey, json, OnlinePlayerCountSnapshot.MaximumAge);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed To Publish Online Player Details");
        }
    }
}
