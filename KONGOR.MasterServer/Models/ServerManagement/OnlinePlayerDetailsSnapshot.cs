namespace KONGOR.MasterServer.Models.ServerManagement;

public sealed record OnlinePlayerDetailsSnapshot
(
    int Version,
    long UpdatedAt,
    int GamesInProgress,
    int QueuedPlayers,
    OnlineServerLocationCount[] ServerLocations,
    OnlineQueueRegionCount[] QueueRegions
)
{
    public const int CurrentVersion = 1;
    public const string CacheKey = "CHAT:ONLINE-PLAYER-DETAILS";

    public static readonly JsonSerializerOptions JSONOptions = new () { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

public sealed record OnlineServerLocationCount(string Location, int GamesInProgress, int ServersAvailable);

public sealed record OnlineQueueRegionCount(string Region, int QueuedPlayers, OnlineQueueGameTypeCount[] GameTypes);

public sealed record OnlineQueueGameTypeCount(string Map, int GameType, int QueuedPlayers);
