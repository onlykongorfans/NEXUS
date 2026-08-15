namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Matchmaking;

/// <summary>
///     Verifies that operational queue snapshots use the same eligibility semantics as the matchmaking broker and expose the player-level details needed to diagnose queue discrepancies.
/// </summary>
public sealed class MatchmakingQueueDiagnosticsTests
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
    {
        MatchmakingService.Groups.Clear();

        return Task.CompletedTask;
    }

    [After(HookType.Test)]
    public Task After_Each_Test()
    {
        MatchmakingService.Groups.Clear();

        return Task.CompletedTask;
    }

    [Test]
    [NotInParallel]
    public async Task Capture_Lists_Players_And_Regions_For_Broker_Eligible_Groups()
    {
        MatchmakingGroupInformation information = MatchmakingTestBuilder.Information
        (
            gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL,
            gameRegions: ["AU", "SG"]
        );

        MatchmakingGroup group = MatchmakingTestBuilder.BuildGroup([1490.0, 1510.0], queuedMinutesAgo: 2.0, information);

        MatchmakingService.Groups.TryAdd(group.Leader.Account.ID, group);

        MatchmakingQueueSnapshot snapshot = MatchmakingQueueDiagnostics.Capture();
        MatchmakingQueuedGroupSnapshot queuedGroup = snapshot.Groups.Single();

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.QueuedGroupCount).IsEqualTo(1);
            await Assert.That(snapshot.QueuedPlayerCount).IsEqualTo(2);
            await Assert.That(snapshot.PlayersPerRegion["AU"]).IsEqualTo(2);
            await Assert.That(snapshot.PlayersPerRegion["SEA"]).IsEqualTo(2);
            await Assert.That(queuedGroup.GroupGUID).IsEqualTo(group.GUID);
            await Assert.That(queuedGroup.QueueType).IsEqualTo(nameof(QueueType.Caldavar));
            await Assert.That(queuedGroup.Players.Count).IsEqualTo(2);
            await Assert.That(queuedGroup.Players[0].AccountName).IsEqualTo(group.Members[0].Account.Name);
            await Assert.That(queuedGroup.Players[0].IsLeader).IsTrue();
            await Assert.That(queuedGroup.Players[1].AccountName).IsEqualTo(group.Members[1].Account.Name);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Capture_Excludes_Forming_And_Match_Committed_Groups()
    {
        MatchmakingGroupInformation information = MatchmakingTestBuilder.Information(gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL);

        MatchmakingGroup activeQueuedGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: 1.0, information);
        MatchmakingGroup formingGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: -1.0, information);
        MatchmakingGroup committedGroup = MatchmakingTestBuilder.BuildSoloGroup(1500.0, queuedMinutesAgo: 1.0, information);

        committedGroup.MatchedUp = true;

        MatchmakingService.Groups.TryAdd(activeQueuedGroup.Leader.Account.ID, activeQueuedGroup);
        MatchmakingService.Groups.TryAdd(formingGroup.Leader.Account.ID, formingGroup);
        MatchmakingService.Groups.TryAdd(committedGroup.Leader.Account.ID, committedGroup);

        MatchmakingQueueSnapshot snapshot = MatchmakingQueueDiagnostics.Capture();

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.QueuedGroupCount).IsEqualTo(1);
            await Assert.That(snapshot.QueuedPlayerCount).IsEqualTo(1);
            await Assert.That(snapshot.Groups.Single().GroupGUID).IsEqualTo(activeQueuedGroup.GUID);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Capture_DeDuplicates_A_Group_During_A_Leader_Rekey()
    {
        MatchmakingGroupInformation information = MatchmakingTestBuilder.Information(gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL);
        MatchmakingGroup group = MatchmakingTestBuilder.BuildGroup([1500.0, 1500.0], queuedMinutesAgo: 1.0, information);

        MatchmakingService.Groups.TryAdd(group.Leader.Account.ID, group);
        MatchmakingService.Groups.TryAdd(int.MaxValue, group);

        MatchmakingQueueSnapshot snapshot = MatchmakingQueueDiagnostics.Capture();

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.QueuedGroupCount).IsEqualTo(1);
            await Assert.That(snapshot.QueuedPlayerCount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Captured_Member_Snapshot_Is_Independent_Of_The_Mutable_Group_List()
    {
        MatchmakingGroup group = MatchmakingTestBuilder.BuildGroup([1500.0, 1500.0]);

        MatchmakingGroupMember[] members = group.CaptureMemberSnapshot();

        group.Members.RemoveAt(group.Members.Count - 1);

        using (Assert.Multiple())
        {
            await Assert.That(members.Length).IsEqualTo(2);
            await Assert.That(group.Members.Count).IsEqualTo(1);
            await Assert.That(members.Any(member => member is null)).IsFalse();
        }
    }
}
