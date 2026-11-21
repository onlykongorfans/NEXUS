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

        if (Request.Form["include_details"] is { Count: 1 } includeDetails && includeDetails[0] == "1")
        {
            string? details = await GetOnlinePlayerDetails();
            response["details_available"] = details is null ? 0 : 1;
            response["details_json"] = details ?? string.Empty;
        }

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<string?> GetOnlinePlayerDetails()
    {
        try
        {
            RedisValue cached = await DistributedCache.StringGetAsync(OnlinePlayerDetailsSnapshot.CacheKey);

            if (cached.HasValue is false)
                return null;

            string json = cached.ToString();
            OnlinePlayerDetailsSnapshot? details = JsonSerializer.Deserialize<OnlinePlayerDetailsSnapshot>(json, OnlinePlayerDetailsSnapshot.JSONOptions);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (details is not { Version: OnlinePlayerDetailsSnapshot.CurrentVersion, GamesInProgress: >= 0, QueuedPlayers: >= 0, ServerLocations: not null, QueueRegions: not null }
                || details.UpdatedAt > now || details.UpdatedAt <= now - (long) OnlinePlayerCountSnapshot.MaximumAge.TotalSeconds)
                return null;

            return json;
        }
        catch (RedisException exception) { Logger.LogWarning(exception, "Unable To Read Online Player Details"); }
        catch (JsonException exception) { Logger.LogWarning(exception, "Invalid Online Player Details Snapshot"); }

        return null;
    }
}
