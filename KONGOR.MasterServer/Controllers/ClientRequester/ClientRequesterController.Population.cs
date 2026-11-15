namespace KONGOR.MasterServer.Controllers.ClientRequester;

public partial class ClientRequesterController
{
    private async Task<IActionResult> GetOnlinePlayerCount()
    {
        Response.Headers.CacheControl = "no-store";

        OnlinePlayerCountSnapshot? snapshot = null;

        try
        {
            RedisValue cached = await DistributedCache.StringGetAsync(OnlinePlayerCountSnapshot.CacheKey);

            if (cached.HasValue)
                snapshot = JsonSerializer.Deserialize<OnlinePlayerCountSnapshot>(cached.ToString());
        }
        catch (RedisException exception) { Logger.LogWarning(exception, "Unable To Read Online Player Count"); }
        catch (JsonException exception) { Logger.LogWarning(exception, "Invalid Online Player Count Snapshot"); }

        TimeSpan? age = snapshot is not null ? DateTimeOffset.UtcNow - snapshot.UpdatedAt : null;
        bool available = snapshot is { PlayerCount: >= 0 } && age >= TimeSpan.Zero && age <= OnlinePlayerCountSnapshot.MaximumAge;

        Dictionary<string, object> response = new ()
        {
            ["available"] = available ? 1 : 0,
            ["player_count"] = available && snapshot is not null ? snapshot.PlayerCount : -1,
            ["updated_at"] = available && snapshot is not null ? snapshot.UpdatedAt.ToUnixTimeSeconds() : 0L
        };

        return Ok(PhpSerialization.Serialize(response));
    }
}
