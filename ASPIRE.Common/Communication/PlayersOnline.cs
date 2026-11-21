namespace ASPIRE.Common.Communication;

public sealed record PlayersOnlineHeadline(int PlayerCount, DateTimeOffset ExpiresAt);

public sealed record PlayersOnlineDetails(
    int GamesInProgress,
    IReadOnlyList<PlayersOnlineLocation> ServerLocations,
    IReadOnlyList<PlayersOnlineQueue> RankedPick,
    IReadOnlyList<PlayersOnlineQueue> MidWars,
    DateTimeOffset ExpiresAt);

public sealed record PlayersOnlineLocation(string Location, int GamesInProgress, int ServersAvailable);

public sealed record PlayersOnlineQueue(string Region, int QueuedPlayers);
