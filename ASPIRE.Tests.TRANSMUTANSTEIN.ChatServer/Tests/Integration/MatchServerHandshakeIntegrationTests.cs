using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Integration;

/// <summary>
///     Drives the match server handshake against the real host over a real socket, covering the accept path, reconnect supersession, managed retirement handover, and graceful termination that must flush the "quit" remote command before closing.
/// </summary>
[NotInParallel(nameof(ChatServerHost))]
public sealed class MatchServerHandshakeIntegrationTests(ServiceContainerContext containerContext)
{
    [Test]
    [Arguments("NEXUS AU 1")]
    [Arguments("NEXUS AU 2")]
    [Arguments("NEXUS2 AU 1")]
    public async Task Valid_Server_Handshake_Preserves_The_Advertised_Name_And_Sends_Configuration(string serverName)
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie);

        IDatabase distributedCacheStore = host.Services.GetRequiredService<IDatabase>();

        MatchServer server = await distributedCacheStore.GetMatchServerByID(serverID)
            ?? throw new NullReferenceException("Seeded Match Server Is NULL");

        server.Name = serverName;

        await distributedCacheStore.SetMatchServer(hostAccountName, server);

        using TcpClient client = await host.ConnectMatchServerAsync();

        NetworkStream stream = client.GetStream();

        await ChatTestProtocol.WriteFrame(stream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(10));

        ushort command = await ChatTestProtocol.ReadCommand(stream, timeout.Token);

        await Assert.That(command).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        List<string> remoteCommands = [];
        bool optionsReceived = false;

        while (true)
        {
            ChatBuffer frame = new (await ChatTestProtocol.ReadFramePayload(stream, timeout.Token));
            ushort frameCommand = BitConverter.ToUInt16(frame.ReadCommandBytes(), 0);

            if (frameCommand == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_OPTIONS)
                optionsReceived = true;

            if (frameCommand != ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND)
                continue;

            frame.ReadString();

            string remoteCommand = frame.ReadString();

            remoteCommands.Add(remoteCommand);

            if (remoteCommand == "echo Project KONGOR Remote Configuration Was Injected Successfully")
                break;
        }

        MatchServer preservedServer = await distributedCacheStore.GetMatchServerByID(serverID)
            ?? throw new NullReferenceException("Registered Match Server Is NULL");

        using (Assert.Multiple())
        {
            await Assert.That(optionsReceived).IsTrue();
            await Assert.That(remoteCommands.Any(remoteCommand => remoteCommand.StartsWith("svr_gameFPS ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(remoteCommands.Any(remoteCommand => remoteCommand.StartsWith("svr_submitMatchStatItems ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(remoteCommands.Any(remoteCommand => remoteCommand.StartsWith("svr_name", StringComparison.OrdinalIgnoreCase))).IsFalse();
            await Assert.That(preservedServer.Name).IsEqualTo(serverName);
            await Assert.That(preservedServer.Cookie).IsEqualTo(cookie);
        }
    }

    [Test]
    public async Task A_Status_Update_Replaces_A_Cached_Random_Name_With_The_Advertised_Name()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();
        string serverName = "NEXUS AU 1";

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie);

        using TcpClient client = await host.ConnectMatchServerAsync();

        NetworkStream stream = client.GetStream();

        await ChatTestProtocol.WriteFrame(stream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(10));

        await Assert.That(await ChatTestProtocol.ReadCommand(stream, timeout.Token)).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        await ChatTestProtocol.WriteFrame(stream, BuildServerStatus(serverID, serverName));

        ChatBuffer ping = new ();
        ping.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);

        await ChatTestProtocol.WriteFrame(stream, ping);

        ChatBuffer? pong = await ChatTestProtocol.ReadUntilCommand(stream, ChatProtocol.Bidirectional.NET_CHAT_PONG, TimeSpan.FromSeconds(10));

        IDatabase distributedCacheStore = host.Services.GetRequiredService<IDatabase>();

        MatchServer updatedServer = await distributedCacheStore.GetMatchServerByID(serverID)
            ?? throw new NullReferenceException("Registered Match Server Is NULL");

        using (Assert.Multiple())
        {
            await Assert.That(pong).IsNotNull();
            await Assert.That(updatedServer.Name).IsEqualTo(serverName);
            await Assert.That(Context.MatchServerChatSessions[serverID].Metadata.Name).IsEqualTo(serverName);
            await Assert.That(updatedServer.Status).IsEqualTo(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);
            await Assert.That(updatedServer.Cookie).IsEqualTo(cookie);
        }
    }

    [Test]
    [Arguments("NEXUS AU", 1, "NEXUS AU 1")]
    [Arguments("NEXUS AU", 5, "NEXUS AU 5")]
    [Arguments("NEXUS2 AU", 1, "NEXUS2 AU 1")]
    public async Task A_Base_Name_Is_Given_An_Instance_Suffix_Through_A_Quoted_Remote_Command(string prefix, int instance, string expectedName)
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie, instance: instance);

        using TcpClient client = await host.ConnectMatchServerAsync();

        NetworkStream stream = client.GetStream();

        await ChatTestProtocol.WriteFrame(stream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));
        await ChatTestProtocol.WriteFrame(stream, BuildServerStatus(serverID, prefix));

        await Assert.That(await ChatTestProtocol.ReadUntilRemoteCommand(stream, $"svr_name \"{expectedName}\"", TimeSpan.FromSeconds(10))).IsTrue();

        foreach (ChatProtocol.ServerStatus serverState in new[]
        {
            ChatProtocol.ServerStatus.SERVER_STATUS_SLEEPING,
            ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
            ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
            ChatProtocol.ServerStatus.SERVER_STATUS_IDLE
        })
        {
            await ChatTestProtocol.WriteFrame(stream, BuildServerStatus(serverID, expectedName, serverState));
        }

        ChatBuffer ping = new ();
        ping.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);

        await ChatTestProtocol.WriteFrame(stream, ping);

        using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(10));

        List<string> subsequentCommands = [];

        while (true)
        {
            ChatBuffer frame = new (await ChatTestProtocol.ReadFramePayload(stream, timeout.Token));
            ushort command = BitConverter.ToUInt16(frame.ReadCommandBytes(), 0);

            if (command == ChatProtocol.Bidirectional.NET_CHAT_PONG)
                break;

            if (command == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND)
            {
                frame.ReadString();
                subsequentCommands.Add(frame.ReadString());
            }
        }

        IDatabase distributedCacheStore = host.Services.GetRequiredService<IDatabase>();

        using (Assert.Multiple())
        {
            await Assert.That(subsequentCommands.Any(command => command.StartsWith("svr_name", StringComparison.Ordinal))).IsFalse();
            await Assert.That((await distributedCacheStore.GetMatchServerByID(serverID))?.Name).IsEqualTo(expectedName);
        }
    }

    [Test]
    [Arguments("NEXUS AU;quit")]
    [Arguments("NEXUS AU\\quit")]
    [Arguments("NEXUS AU\"quit")]
    public async Task An_Unsafe_Advertised_Name_Is_Not_Executed_As_A_Remote_Command(string serverName)
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie);

        using TcpClient client = await host.ConnectMatchServerAsync();

        NetworkStream stream = client.GetStream();

        await ChatTestProtocol.WriteFrame(stream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));
        await ChatTestProtocol.WriteFrame(stream, BuildServerStatus(serverID, serverName));

        ChatBuffer ping = new ();
        ping.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);

        await ChatTestProtocol.WriteFrame(stream, ping);

        using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(10));

        List<string> commands = [];

        while (true)
        {
            ChatBuffer frame = new (await ChatTestProtocol.ReadFramePayload(stream, timeout.Token));
            ushort command = BitConverter.ToUInt16(frame.ReadCommandBytes(), 0);

            if (command == ChatProtocol.Bidirectional.NET_CHAT_PONG)
                break;

            if (command == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND)
            {
                frame.ReadString();
                commands.Add(frame.ReadString());
            }
        }

        await Assert.That(commands.Any(command => command.StartsWith("svr_name", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Reconnect_Supersedes_The_Existing_Session_And_Preserves_The_Cache_Entry()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie);

        IDatabase cache = host.Services.GetRequiredService<IDatabase>();

        MatchServer seededServer = await cache.GetMatchServerByID(serverID)
            ?? throw new NullReferenceException("Seeded Match Server Is NULL");

        seededServer.Name = "NEXUS2 AU 1";

        await cache.SetMatchServer(hostAccountName, seededServer);

        using TcpClient first = await host.ConnectMatchServerAsync();

        NetworkStream firstStream = first.GetStream();

        await ChatTestProtocol.WriteFrame(firstStream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using (CancellationTokenSource firstTimeout = new (TimeSpan.FromSeconds(10)))
            await Assert.That(await ChatTestProtocol.ReadCommand(firstStream, firstTimeout.Token)).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        using TcpClient second = await host.ConnectMatchServerAsync();

        NetworkStream secondStream = second.GetStream();

        await ChatTestProtocol.WriteFrame(secondStream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using (CancellationTokenSource secondTimeout = new (TimeSpan.FromSeconds(10)))
            await Assert.That(await ChatTestProtocol.ReadCommand(secondStream, secondTimeout.Token)).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        bool firstWasSuperseded = await ChatTestProtocol.WaitForClose(firstStream, TimeSpan.FromSeconds(10));

        bool cacheEntryPreserved;

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

            cacheEntryPreserved = await distributedCacheStore.GetMatchServerBySessionCookie(cookie) is not null;
        }

        using (Assert.Multiple())
        {
            await Assert.That(firstWasSuperseded).IsTrue();
            await Assert.That(Context.MatchServerChatSessions.ContainsKey(serverID)).IsTrue();
            await Assert.That(cacheEntryPreserved).IsTrue();
            await Assert.That((await cache.GetMatchServerByID(serverID))?.Name).IsEqualTo("NEXUS2 AU 1");
        }
    }

    [Test]
    public async Task Server_Disconnect_Retires_The_Session_And_Preserves_The_Cache_Entry_For_Reconnect()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie, ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);

        using TcpClient first = await host.ConnectMatchServerAsync();

        NetworkStream firstStream = first.GetStream();

        await ChatTestProtocol.WriteFrame(firstStream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using (CancellationTokenSource firstTimeout = new (TimeSpan.FromSeconds(10)))
            await Assert.That(await ChatTestProtocol.ReadCommand(firstStream, firstTimeout.Token)).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

        MatchServerChatSession retiredSession = Context.MatchServerChatSessions[serverID];

        ChatBuffer disconnect = new ();

        disconnect.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_DISCONNECT);

        await ChatTestProtocol.WriteFrame(firstStream, disconnect);

        bool retiredSessionRemoved = await ChatTestProtocol.WaitUntil(
            () => Context.MatchServerChatSessions.ContainsKey(serverID) is false,
            TimeSpan.FromSeconds(10));

        MatchServer? preservedMatchServer = null;

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

            for (int attempt = 0; attempt < 200; attempt++)
            {
                preservedMatchServer = await distributedCacheStore.GetMatchServerByID(serverID);

                if (preservedMatchServer?.IsRetired is true)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }

        if (preservedMatchServer is null)
            throw new NullReferenceException("Preserved Match Server Is NULL");

        ServerForCreateListResponse createServerListDuringGracePeriod = new ([preservedMatchServer], region: null, cookie: "test-cookie");

        using (Assert.Multiple())
        {
            await Assert.That(retiredSessionRemoved).IsTrue();
            await Assert.That(Context.MatchServerChatSessions.Values.Contains(retiredSession)).IsFalse();
            await Assert.That(preservedMatchServer.Cookie).IsEqualTo(cookie);
            await Assert.That(preservedMatchServer.IsRetired).IsTrue();
            await Assert.That(preservedMatchServer.Status).IsEqualTo(ChatProtocol.ServerStatus.SERVER_STATUS_CRASHED);
            await Assert.That(createServerListDuringGracePeriod.Servers.ContainsKey(serverID)).IsFalse();
        }

        using TcpClient replacement = await host.ConnectMatchServerAsync();

        NetworkStream replacementStream = replacement.GetStream();

        await ChatTestProtocol.WriteFrame(replacementStream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        using CancellationTokenSource replacementTimeout = new (TimeSpan.FromSeconds(10));

        ushort replacementCommand = await ChatTestProtocol.ReadCommand(replacementStream, replacementTimeout.Token);

        await Assert.That(replacementCommand).IsEqualTo((ushort) ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);
    }

    [Test]
    public async Task Graceful_Terminate_Delivers_The_Quit_Remote_Command()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);

        (int hostAccountID, string hostAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.ServerHost);

        int serverID = Random.Shared.Next(100_000, 999_999);
        string cookie = Guid.CreateVersion7().ToString();

        await ChatTestData.SeedMatchServer(host.Services, serverID, hostAccountID, hostAccountName, cookie);

        using TcpClient client = await host.ConnectMatchServerAsync();

        NetworkStream stream = client.GetStream();

        await ChatTestProtocol.WriteFrame(stream, ChatTestProtocol.BuildServerHandshake(serverID, cookie));

        // Wait For The Handshake To Register The Session In The Pool, Then Terminate It Gracefully
        await ChatTestProtocol.WaitUntil(() => Context.MatchServerChatSessions.ContainsKey(serverID), TimeSpan.FromSeconds(10));

        MatchServerChatSession session = Context.MatchServerChatSessions[serverID];

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

            await session.Terminate(distributedCacheStore);
        }

        // The Graceful Teardown Must Flush The "quit" Remote Command To The Match Server Before The Socket Is Closed
        bool quitDelivered = await ChatTestProtocol.ReadUntilRemoteCommand(stream, "quit", TimeSpan.FromSeconds(10));

        await Assert.That(quitDelivered).IsTrue();
    }
    private static ChatBuffer BuildServerStatus(int serverID, string serverName, ChatProtocol.ServerStatus serverState = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE)
    {
        ChatBuffer status = new ();

        status.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS);
        status.WriteInt32(serverID);
        status.WriteString("127.0.0.1");
        status.WriteInt16(11235);
        status.WriteString("AU");
        status.WriteString(serverName);
        status.WriteInt32(1);
        status.WriteInt32(0);
        status.WriteInt8((byte) serverState);
        status.WriteInt8(0);
        status.WriteInt16(0);
        status.WriteString("caldavar");
        status.WriteString("Public Match");
        status.WriteString("ap");
        status.WriteInt8(5);
        status.WriteInt16(0);
        status.WriteInt16(0);
        status.WriteInt32(0);
        status.WriteInt32(0);

        // Two Team Records And Ten Player Records
        for (int recordIndex = 0; recordIndex < 12; recordIndex++)
            status.WriteString(string.Empty);

        status.WriteInt32(0);
        status.WriteInt32(0);
        status.WriteInt64(1024);
        status.WriteInt64(2048);
        status.WriteInt64(1024);
        status.WriteInt64(2048);
        status.WriteString("4.14.1.2");
        status.WriteInt32(0);
        status.WriteFloat32(0);
        status.WriteString("Test Matchserver");

        return status;
    }
}
