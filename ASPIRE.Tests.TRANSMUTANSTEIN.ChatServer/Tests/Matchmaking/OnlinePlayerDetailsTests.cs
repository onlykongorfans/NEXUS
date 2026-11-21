namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Matchmaking;

[NotInParallel]
public sealed class OnlinePlayerDetailsTests
{
    [Test]
    public async Task Queue_Details_Deduplicate_Selections_And_Keep_Wildcard_And_Game_Types_Separate()
    {
        MatchmakingGroup normal = MatchmakingTestBuilder.BuildGroup([1500, 1500], information:
            MatchmakingTestBuilder.Information(ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL, gameRegions: ["USW", "USE", "AU"]));
        MatchmakingGroup casual = MatchmakingTestBuilder.BuildSoloGroup(1500, information:
            MatchmakingTestBuilder.Information(ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL, gameRegions: ["NEWERTH", "AU"]));
        MatchmakingGroup committed = MatchmakingTestBuilder.BuildSoloGroup(1500, information:
            MatchmakingTestBuilder.Information(ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, gameRegions: ["AU"]));
        committed.MatchedUp = true;

        foreach (MatchmakingGroup group in new[] { normal, casual, committed })
            MatchmakingService.Groups[group.Leader.Account.ID] = group;

        try
        {
            OnlinePlayerDetailsSnapshot details = OnlinePlayerDetails.Capture([], DateTimeOffset.UtcNow);
            OnlineQueueRegionCount unitedStates = details.QueueRegions.Single(region => region.Region == "US");
            OnlineQueueRegionCount australia = details.QueueRegions.Single(region => region.Region == "AU");
            OnlineQueueRegionCount wildcard = details.QueueRegions.Single(region => region.Region == "NEWERTH");

            await Assert.That(details.QueuedPlayers).IsEqualTo(3);
            await Assert.That(unitedStates.QueuedPlayers).IsEqualTo(2);
            await Assert.That(australia.QueuedPlayers).IsEqualTo(2);
            await Assert.That(australia.GameTypes.Single(gameType => gameType.GameType == 6).QueuedPlayers).IsEqualTo(2);
            await Assert.That(australia.GameTypes.Single(gameType => gameType.GameType == 3).QueuedPlayers).IsEqualTo(0);
            await Assert.That(wildcard.QueuedPlayers).IsEqualTo(1);
            await Assert.That(wildcard.GameTypes.Single(gameType => gameType.GameType == 7).QueuedPlayers).IsEqualTo(1);
        }
        finally
        {
            foreach (MatchmakingGroup group in new[] { normal, casual, committed })
                MatchmakingService.Groups.TryRemove(group.Leader.Account.ID, out _);
        }
    }

    [Test]
    public async Task Server_Details_Count_Distinct_Running_Matches_And_Only_Allocatable_Instances()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const ChatProtocol.ServerStatus idle = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        const ChatProtocol.ServerStatus active = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE;
        MatchServer[] servers =
        [
            Server(1, "AU", idle), Server(2, "AU", idle), Server(3, "AU", idle),
            Server(4, "USW", active), Server(5, "USW", active), Server(6, "USW", active),
            Server(7, "AU", ChatProtocol.ServerStatus.SERVER_STATUS_SLEEPING), Server(8, "AU", idle),
            Server(9, "AU", idle)
        ];
        servers[7].IsRetired = true;
        MatchmakingMatch assigned = new () { LegionTeam = new(), AssignedServerID = 2 };
        MatchmakingService.ActiveMatches[assigned.GUID] = assigned;

        try
        {
            foreach (MatchServer server in servers.Where(server => server.ID != 9))
            {
                MatchServerChatSession session = (MatchServerChatSession) RuntimeHelpers.GetUninitializedObject(typeof(MatchServerChatSession));
                session.Metadata = new MatchServerChatSessionMetadata
                {
                    ServerID = server.ID,
                    SessionCookie = string.Empty,
                    ChatProtocolVersion = 0,
                    Status = server.Status,
                    GamePhase = server.ID is 4 or 5 ? 5 : 1,
                    MatchID = server.ID is 4 or 5 ? 123 : 124,
                    LastStatusUpdate = server.ID == 3 ? now.AddSeconds(-151) : now
                };
                Context.MatchServerChatSessions[server.ID] = session;
            }

            OnlinePlayerDetailsSnapshot details = OnlinePlayerDetails.Capture(servers, now);
            OnlineServerLocationCount australia = details.ServerLocations.Single(location => location.Location == "AU");
            OnlineServerLocationCount usWest = details.ServerLocations.Single(location => location.Location == "USW");

            await Assert.That(australia.ServersAvailable).IsEqualTo(1);
            await Assert.That(usWest.GamesInProgress).IsEqualTo(1);
            await Assert.That(usWest.ServersAvailable).IsEqualTo(0);
            await Assert.That(details.GamesInProgress).IsEqualTo(1);
        }
        finally
        {
            MatchmakingService.ActiveMatches.TryRemove(assigned.GUID, out _);
            foreach (MatchServer server in servers)
                Context.MatchServerChatSessions.TryRemove(server.ID, out _);
        }
    }

    private static MatchServer Server(int id, string location, ChatProtocol.ServerStatus status) => new ()
    {
        ID = id, HostAccountID = id, HostAccountName = "Host", Name = "Test", MatchServerManagerID = null,
        Instance = 1, IPAddress = "127.0.0.1", Port = 11235, Location = location, Description = string.Empty, Status = status
    };
}
