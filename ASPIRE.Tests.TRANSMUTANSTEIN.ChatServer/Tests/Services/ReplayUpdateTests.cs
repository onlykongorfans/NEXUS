namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Services;

[NotInParallel]
public sealed class ReplayUpdateTests(TRANSMUTANSTEINIntegrationWebApplicationFactory webApplicationFactory)
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Completion_Reaches_All_Waiting_Clients_Only_After_Storage_Confirms_The_File(bool stored)
    {
        await webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        MerrickContext database = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();
        const int matchID = 998877;
        const int managerID = 555;

        MatchStatistics match = new ()
        {
            MatchID = matchID, ServerID = 1, HostAccountName = "ReplayOwner", Map = "caldavar", MapVersion = "1", TimePlayed = 1,
            FileSize = 0, FileName = "local.honreplay", ConnectionState = 0, Version = "4.10.1.0", AveragePSR = 1500,
            AveragePSRTeamOne = 1500, AveragePSRTeamTwo = 1500, GameMode = "normal", ScoreTeam1 = 0, ScoreTeam2 = 0,
            TeamScoreGoal = 0, PlayerScoreGoal = 0, NumberOfRounds = 0, ReleaseStage = "live", BannedHeroes = null,
            AwardMostAnnihilations = null, AwardMostQuadKills = null, AwardLargestKillStreak = null, AwardMostSmackdowns = null,
            AwardMostKills = null, AwardMostAssists = null, AwardLeastDeaths = null, AwardMostBuildingDamage = null,
            AwardMostWardsKilled = null, AwardMostHeroDamageDealt = null, AwardHighestCreepScore = null
        };
        database.MatchStatistics.Add(match);
        await database.SaveChangesAsync();

        await using RunningClientSession first = RunningClientSession.Start(Account("ReplayFirst", 700001));
        await using RunningClientSession second = RunningClientSession.Start(Account("ReplaySecond", 700002));
        Context.ClientChatSessions[first.Session.Account.Name] = first.Session;
        Context.ClientChatSessions[second.Session.Account.Name] = second.Session;

        WebApplication host = WebApplication.CreateSlimBuilder().Build();
        await using (host)
        {
            MatchServerManagerChatSession manager = new (new DefaultConnectionContext(), host.Services)
            {
                Account = Account("ReplayOwner", 700003),
                Metadata = new MatchServerManagerChatSessionMetadata { ServerManagerID = managerID, SessionCookie = "test", ChatProtocolVersion = (int) ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION }
            };
            string watchersKey = ReplayStorageService.WatchersKey(managerID, matchID);

            foreach (int accountID in new[] { first.Session.Account.ID, second.Session.Account.ID })
            {
                await cache.StringSetAsync(ReplayStorageService.PendingRequestKey(managerID, matchID, accountID), "pending");
                await cache.SetAddAsync(watchersKey, accountID);
            }

            ReplayStorageSettings settings = new ()
            {
                UploadBaseURL = "https://store.test/replays/", DownloadBaseURL = "http://files.test/replays/"
            };
            ReplayStorageService storage = new (new HttpClient(new StorageHandler(stored)), settings, cache, NullLogger<ReplayStorageService>.Instance);
            ReplayUploadUpdate processor = new (database, cache, storage);

            ChatBuffer update = new ();
            update.WriteCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_UPLOAD_UPDATE);
            update.WriteInt32(matchID);
            update.WriteInt32(first.Session.Account.ID);
            update.WriteInt8(7);
            update.WriteString("http://untrusted-manager.test/ignore-this-link");
            await processor.Process(manager, update);

            (int firstMatch, byte firstStatus, string firstURL) = await first.ReadReplayStatus();
            (int secondMatch, byte secondStatus, string secondURL) = await second.ReadReplayStatus();
            await Assert.That(firstMatch).IsEqualTo(matchID);
            await Assert.That(secondMatch).IsEqualTo(matchID);
            await Assert.That(firstStatus).IsEqualTo(stored ? (byte) 7 : (byte) 0);
            await Assert.That(secondStatus).IsEqualTo(firstStatus);
            await Assert.That(firstURL).IsEqualTo(stored ? $"http://files.test/replays/M{matchID}.honreplay" : string.Empty);
            await Assert.That(secondURL).IsEqualTo(firstURL);
            await Assert.That(await cache.KeyExistsAsync(watchersKey)).IsFalse();
        }
        Context.ClientChatSessions.TryRemove(first.Session.Account.Name, out _);
        Context.ClientChatSessions.TryRemove(second.Session.Account.Name, out _);
    }

    private static Account Account(string name, int accountID) => new ()
    {
        ID = accountID, Name = name, IsMain = true, Type = AccountType.Normal,
        User = new User { EmailAddress = name + "@test.invalid", Role = new Role { Name = "Player" }, SRPPasswordSalt = "", SRPPasswordHash = "" }
    };

    private sealed class StorageHandler(bool stored) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new (stored ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = stored ? 2048 : 0;
            return Task.FromResult(response);
        }
    }
}
