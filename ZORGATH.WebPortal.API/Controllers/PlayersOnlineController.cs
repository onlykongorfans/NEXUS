namespace ZORGATH.WebPortal.API.Controllers;

[ApiController]
[Route("[controller]")]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PlayersOnlineController(IDatabase distributedCache, ILogger<PlayersOnlineController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PlayersOnlineHeadline>> GetHeadline()
    {
        OnlinePlayerCountSnapshot? snapshot = await ReadSnapshot<OnlinePlayerCountSnapshot>(OnlinePlayerCountSnapshot.CacheKey);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not { PlayerCount: >= 0 } || snapshot.UpdatedAt > now || snapshot.UpdatedAt <= now - OnlinePlayerCountSnapshot.MaximumAge)
            return NoContent();

        return new PlayersOnlineHeadline(snapshot.PlayerCount, snapshot.UpdatedAt + OnlinePlayerCountSnapshot.MaximumAge);
    }

    [HttpGet("Details")]
    public async Task<ActionResult<PlayersOnlineDetails>> GetDetails()
    {
        OnlinePlayerDetailsSnapshot? snapshot = await ReadSnapshot<OnlinePlayerDetailsSnapshot>(OnlinePlayerDetailsSnapshot.CacheKey, OnlinePlayerDetailsSnapshot.JSONOptions);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not { Version: OnlinePlayerDetailsSnapshot.CurrentVersion, GamesInProgress: >= 0, ServerLocations: not null, QueueRegions: not null }
            || snapshot.UpdatedAt > now.ToUnixTimeSeconds() || snapshot.UpdatedAt <= (now - OnlinePlayerCountSnapshot.MaximumAge).ToUnixTimeSeconds()
            || snapshot.ServerLocations.Any(location => location is not { GamesInProgress: >= 0, ServersAvailable: >= 0 } || string.IsNullOrWhiteSpace(location.Location))
            || snapshot.QueueRegions.Any(region => region is not { GameTypes: not null } || string.IsNullOrWhiteSpace(region.Region) ||
                region.GameTypes.Any(count => count is not { QueuedPlayers: >= 0 })))
            return NoContent();

        return new PlayersOnlineDetails(
            snapshot.GamesInProgress,
            snapshot.ServerLocations.Select(location => new PlayersOnlineLocation(location.Location, location.GamesInProgress, location.ServersAvailable)).ToArray(),
            GetQueue(snapshot, ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL),
            GetQueue(snapshot, ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS),
            DateTimeOffset.FromUnixTimeSeconds(snapshot.UpdatedAt) + OnlinePlayerCountSnapshot.MaximumAge);
    }

    private static PlayersOnlineQueue[] GetQueue(OnlinePlayerDetailsSnapshot snapshot, ChatProtocol.TMMGameType gameType)
        => [.. snapshot.QueueRegions.SelectMany(region => region.GameTypes
            .Where(count => count.GameType == (int) gameType)
            .Select(count => new PlayersOnlineQueue(region.Region, count.QueuedPlayers)))];

    private async Task<TSnapshot?> ReadSnapshot<TSnapshot>(string key, JsonSerializerOptions? serializerOptions = null) where TSnapshot : class
    {
        try
        {
            RedisValue cachedSnapshot = await distributedCache.StringGetAsync(key);

            return cachedSnapshot.HasValue ? JsonSerializer.Deserialize<TSnapshot>(cachedSnapshot.ToString(), serializerOptions) : null;
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Unable To Read Players Online Snapshot {CacheKey}", key);
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Invalid Players Online Snapshot {CacheKey}", key);
            return null;
        }
    }
}
