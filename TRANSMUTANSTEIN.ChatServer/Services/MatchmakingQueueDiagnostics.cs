namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
///     Captures read-only snapshots of the process-local matchmaking queue for operational diagnostics.
/// </summary>
public static class MatchmakingQueueDiagnostics
{
    /// <summary>
    ///     Captures the groups which the broker can currently select.
    ///     Groups which are only being formed, or which have already been committed to a match, are intentionally excluded.
    /// </summary>
    public static MatchmakingQueueSnapshot Capture()
    {
        DateTimeOffset capturedAtUTC = DateTimeOffset.UtcNow;

        // A Group Can Briefly Be Present Under Both Its Old And New Leader IDs During A Concurrent Leadership Transfer, So De-Duplicate By Its Stable GUID
        MatchmakingGroup[] candidateGroups = [.. MatchmakingService.Groups.Values.DistinctBy(group => group.GUID)];

        List<MatchmakingQueuedGroupSnapshot> queuedGroups = [];

        foreach (MatchmakingGroup group in candidateGroups)
        {
            DateTimeOffset? queuedAtUTC = group.QueueStartTime;

            // Match The Broker's Eligibility Predicate Rather Than Terminal.UsersInQueuePerRegion(), Which Also Counts Groups Already Committed To A Match
            if (queuedAtUTC is null || group.MatchedUp)
                continue;

            if (group.CaptureMemberSnapshot() is not { Length: > 0 } members)
                continue;

            Array.Sort(members, (left, right) => left.Slot.CompareTo(right.Slot));

            // Re-Check The Mutable Queue State After Copying Members So A Concurrent Leave Or Match Commitment Is Not Reported As An Active Queue Entry
            if (group.QueueStartTime != queuedAtUTC || group.MatchedUp)
                continue;

            string[] requestedRegions = [.. group.Information.GameRegions];
            string[] aggregateRegions = [.. requestedRegions.Select(GameRegions.GetAggregate).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

            MatchmakingQueuedPlayerSnapshot[] players = [.. members.Select(member => new MatchmakingQueuedPlayerSnapshot
            (
                member.Account.ID,
                member.Account.Name,
                member.Slot,
                member.IsLeader,
                member.TMR
            ))];

            queuedGroups.Add(new MatchmakingQueuedGroupSnapshot
            (
                group.GUID,
                queuedAtUTC.Value,
                Math.Max(0L, Convert.ToInt64(Math.Floor((capturedAtUTC - queuedAtUTC.Value).TotalSeconds))),
                ResolveQueueType(group),
                group.Information.GroupType.ToString(),
                group.Information.GameType.ToString(),
                group.Information.MapName,
                [.. group.Information.GameModes],
                requestedRegions,
                aggregateRegions,
                group.Information.Ranked,
                group.Information.MatchFidelity,
                players
            ));
        }

        queuedGroups.Sort((left, right) =>
        {
            int queueTimeComparison = left.QueuedAtUTC.CompareTo(right.QueuedAtUTC);

            return queueTimeComparison is not 0 ? queueTimeComparison : left.GroupGUID.CompareTo(right.GroupGUID);
        });

        SortedDictionary<string, int> playersPerRegion = new (StringComparer.OrdinalIgnoreCase);

        foreach (MatchmakingQueuedGroupSnapshot group in queuedGroups)
            foreach (string region in group.AggregateRegions)
                playersPerRegion[region] = playersPerRegion.GetValueOrDefault(region) + group.Players.Count;

        return new MatchmakingQueueSnapshot
        (
            capturedAtUTC,
            queuedGroups.Count,
            queuedGroups.Sum(group => group.Players.Count),
            playersPerRegion,
            queuedGroups
        );
    }

    private static string ResolveQueueType(MatchmakingGroup group)
    {
        try
        {
            return MatchmakingService.GetQueueTypePartition(group.Information.GroupType, group.Information.GameType).ToString();
        }

        catch (ArgumentOutOfRangeException)
        {
            // Preserve Visibility Into A Malformed Queue Entry Instead Of Failing The Entire Diagnostic Request
            return "Unsupported";
        }
    }
}
