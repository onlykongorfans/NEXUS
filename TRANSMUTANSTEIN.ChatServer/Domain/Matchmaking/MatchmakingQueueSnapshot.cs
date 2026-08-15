namespace TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

/// <summary>
///     A point-in-time view of the groups which are eligible for selection by the matchmaking broker.
/// </summary>
public sealed record MatchmakingQueueSnapshot
(
    DateTimeOffset CapturedAtUTC,
    int QueuedGroupCount,
    int QueuedPlayerCount,
    IReadOnlyDictionary<string, int> PlayersPerRegion,
    IReadOnlyList<MatchmakingQueuedGroupSnapshot> Groups
);

/// <summary>
///     A queued matchmaking group and the queue criteria shared by its members.
/// </summary>
public sealed record MatchmakingQueuedGroupSnapshot
(
    Guid GroupGUID,
    DateTimeOffset QueuedAtUTC,
    long WaitSeconds,
    string QueueType,
    string GroupType,
    string GameType,
    string MapName,
    IReadOnlyList<string> GameModes,
    IReadOnlyList<string> RequestedRegions,
    IReadOnlyList<string> AggregateRegions,
    bool Ranked,
    byte MatchFidelity,
    IReadOnlyList<MatchmakingQueuedPlayerSnapshot> Players
);

/// <summary>
///     A player belonging to a broker-eligible queued group.
/// </summary>
public sealed record MatchmakingQueuedPlayerSnapshot
(
    int AccountID,
    string AccountName,
    byte Slot,
    bool IsLeader,
    double TMR
);
