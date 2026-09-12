namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Matchmaking;

/// <summary>
///     Covers delayed match cleanup after a player has created a replacement matchmaking group.
/// </summary>
public sealed class MatchmakingCleanupTests
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
    {
        MatchmakingService.Groups.Clear();
        MatchmakingService.ActiveMatches.Clear();

        return Task.CompletedTask;
    }

    [After(HookType.Test)]
    public Task After_Each_Test()
    {
        MatchmakingService.Groups.Clear();
        MatchmakingService.ActiveMatches.Clear();

        return Task.CompletedTask;
    }

    [Test]
    [NotInParallel]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Delayed_Cleanup_Preserves_A_Replacement_Queued_Group(int replacementMemberCount)
    {
        MatchmakingGroup oldGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: -1);
        MatchmakingMatch oldMatch = RegisterMatch(oldGroup);

        // Solo Groups Leave The Registry When Their Match Starts, But The Active Match Retains The Old Group Until Server Reset
        MatchmakingGroup replacement = MatchmakingTestBuilder.BuildGroup([.. Enumerable.Repeat(1500.0, replacementMemberCount)]);
        replacement.Leader.Account.ID = oldGroup.Leader.Account.ID;

        DateTimeOffset? queueStartTime = replacement.QueueStartTime;

        MatchmakingService.Groups.TryAdd(replacement.Leader.Account.ID, replacement);

        MatchmakingService.CleanUpMatchesForServer(oldMatch.AssignedServerID.GetValueOrDefault());

        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(MatchmakingService.GetMatchmakingGroupByMemberID(replacement.Leader.Account.ID), replacement)).IsTrue();
            await Assert.That(replacement.QueueStartTime).IsEqualTo(queueStartTime);
            await Assert.That(replacement.MatchedUp).IsFalse();
            await Assert.That(MatchmakingQueueDiagnostics.Capture().QueuedPlayerCount).IsEqualTo(replacementMemberCount);
            await Assert.That(MatchmakingService.ActiveMatches.ContainsKey(oldMatch.GUID)).IsFalse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task Cleanup_Removes_The_Old_Solo_Group_When_It_Is_Still_Registered()
    {
        MatchmakingGroup oldGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: -1);
        MatchmakingMatch oldMatch = RegisterMatch(oldGroup);

        MatchmakingService.Groups.TryAdd(oldGroup.Leader.Account.ID, oldGroup);

        MatchmakingService.CleanUpMatchesForServer(oldMatch.AssignedServerID.GetValueOrDefault());

        using (Assert.Multiple())
        {
            await Assert.That(MatchmakingService.GetMatchmakingGroupByMemberID(oldGroup.Leader.Account.ID)).IsNull();
            await Assert.That(MatchmakingService.ActiveMatches.ContainsKey(oldMatch.GUID)).IsFalse();
            await Assert.That(oldGroup.MatchedUp).IsFalse();
            await Assert.That(oldGroup.Leader.IsInGame).IsFalse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task Repeated_Cleanup_Does_Not_Remove_A_Group_Created_After_Cleanup()
    {
        MatchmakingGroup oldGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: -1);
        MatchmakingMatch oldMatch = RegisterMatch(oldGroup);

        MatchmakingService.CleanUpMatchesForServer(oldMatch.AssignedServerID.GetValueOrDefault());

        MatchmakingGroup replacement = MatchmakingTestBuilder.BuildSoloGroup(1500.0);
        replacement.Leader.Account.ID = oldGroup.Leader.Account.ID;

        MatchmakingService.Groups.TryAdd(replacement.Leader.Account.ID, replacement);

        MatchmakingService.CleanUpMatchesForServer(oldMatch.AssignedServerID.GetValueOrDefault());

        await Assert.That(ReferenceEquals(MatchmakingService.GetMatchmakingGroupByMemberID(replacement.Leader.Account.ID), replacement)).IsTrue();
    }

    private static MatchmakingMatch RegisterMatch(MatchmakingGroup group)
    {
        MatchmakingMatch match = new ()
        {
            LegionTeam = MatchmakingTeam.FromGroups([group], teamSize: 1),
            AssignedServerID = 12345
        };

        group.MatchedUp = true;
        group.Leader.IsInGame = true;

        MatchmakingService.ActiveMatches.TryAdd(match.GUID, match);

        return match;
    }
}
