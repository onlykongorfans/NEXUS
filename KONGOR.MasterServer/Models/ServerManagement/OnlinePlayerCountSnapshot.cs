namespace KONGOR.MasterServer.Models.ServerManagement;

/// <summary>
///     Describes the number of authenticated game clients connected to the chat server at a particular time.
/// </summary>
public sealed record OnlinePlayerCountSnapshot(int PlayerCount, DateTimeOffset UpdatedAt)
{
    public const string CacheKey = "CHAT:ONLINE-PLAYER-COUNT";

    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(45);
}
