namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Matchmaking;

[NotInParallel(nameof(ChatServerHost))]
public sealed class GroupMembershipTests(ServiceContainerContext containerContext)
{
    [Test]
    public async Task Removing_A_Member_Keeps_An_Existing_Enumeration_Valid_And_Preserves_The_Leader()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        List<TcpClient> clients = [];
        List<MatchmakingGroupMember> members = [];

        try
        {
            for (byte slot = 1; slot <= 3; slot++)
            {
                (int accountID, string accountName) = await ChatTestData.SeedAccount(host.Services, AccountType.Normal);
                string cookie = Guid.CreateVersion7().ToString();

                await ChatTestData.SeedClientSessionCookie(host.Services, cookie, accountName);

                clients.Add(await host.ConnectAndAuthenticateClientAsync(accountID, accountName, cookie, "127.0.0.1"));

                members.Add(new MatchmakingGroupMember(Context.ClientChatSessions[accountName])
                {
                    Slot = slot,
                    IsLeader = slot is 1,
                    IsReady = true,
                    IsInGame = false,
                    IsEligibleForMatchmaking = true,
                    LoadingPercent = 100,
                    GameModeAccess = string.Empty,
                    TMR = MatchmakingTestBuilder.BaselineTMR
                });
            }

            MatchmakingGroup group = MatchmakingTestBuilder.BuildGroup([1500, 1500, 1500]);

            group.Members = members;

            using List<MatchmakingGroupMember>.Enumerator enumeration = group.Members.GetEnumerator();

            enumeration.MoveNext();

            group.RemoveMember(members[1].Account.ID);

            int remainingOriginalMembers = 0;

            while (enumeration.MoveNext())
                remainingOriginalMembers++;

            using (Assert.Multiple())
            {
                await Assert.That(remainingOriginalMembers).IsEqualTo(2);
                await Assert.That(group.Members.Count).IsEqualTo(2);
                await Assert.That(group.Members.Any(member => member.Account.ID == members[1].Account.ID)).IsFalse();
                await Assert.That(group.Leader.Account.ID).IsEqualTo(members[0].Account.ID);
                await Assert.That(group.Members.Select(member => member.Slot)).IsEquivalentTo([(byte) 1, (byte) 2]);
            }
        }

        finally
        {
            foreach (TcpClient client in clients)
                client.Dispose();
        }
    }
}
