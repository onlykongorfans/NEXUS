namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Services;

[NotInParallel]
public sealed class OnlinePlayerCountPublisherTests(ServiceContainerContext containerContext)
{
    [Test]
    public async Task The_Published_Count_Tracks_Authenticated_Clients_And_Has_A_Bounded_Lifetime()
    {
        await using ChatServerHost host = await ChatServerHost.StartAsync(containerContext);
        IDatabase cache = host.Services.GetRequiredService<IDatabase>();
        OnlinePlayerCountPublisher publisher = host.Services.GetServices<IHostedService>().OfType<OnlinePlayerCountPublisher>().Single();
        await publisher.Publish();
        await Assert.That((await Read(cache)).PlayerCount).IsEqualTo(0);

        (int firstAccountID, string firstAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.Normal);
        (int secondAccountID, string secondAccountName) = await ChatTestData.SeedAccount(host.Services, AccountType.Normal);
        string firstCookie = Guid.NewGuid().ToString();
        string secondCookie = Guid.NewGuid().ToString();
        await ChatTestData.SeedClientSessionCookie(host.Services, firstCookie, firstAccountName);
        await ChatTestData.SeedClientSessionCookie(host.Services, secondCookie, secondAccountName);

        using TcpClient first = await host.ConnectAndAuthenticateClientAsync(firstAccountID, firstAccountName, firstCookie, "127.0.0.1");
        using TcpClient second = await host.ConnectAndAuthenticateClientAsync(secondAccountID, secondAccountName, secondCookie, "127.0.0.1");
        using TcpClient unauthenticated = await host.ConnectClientAsync();
        using TcpClient matchServer = await host.ConnectMatchServerAsync();
        using TcpClient manager = new ();
        await manager.ConnectAsync(IPAddress.Loopback, host.MatchServerManagerPort);

        Context.ClientChatSessions[firstAccountName].Metadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        await publisher.Publish();
        await Assert.That((await Read(cache)).PlayerCount).IsEqualTo(2);

        first.Dispose();
        await Assert.That(await ChatTestProtocol.WaitUntil(() => Context.ClientChatSessions.ContainsKey(firstAccountName) is false, TimeSpan.FromSeconds(10))).IsTrue();
        await publisher.Publish();
        await Assert.That((await Read(cache)).PlayerCount).IsEqualTo(1);

        TimeSpan? remaining = await cache.KeyTimeToLiveAsync(OnlinePlayerCountSnapshot.CacheKey);
        await Assert.That(remaining.HasValue && remaining.Value > TimeSpan.Zero && remaining.Value <= OnlinePlayerCountSnapshot.MaximumAge).IsTrue();
    }

    private static async Task<OnlinePlayerCountSnapshot> Read(IDatabase cache)
    {
        RedisValue value = await cache.StringGetAsync(OnlinePlayerCountSnapshot.CacheKey);
        return JsonSerializer.Deserialize<OnlinePlayerCountSnapshot>(value.ToString()) ?? throw new InvalidOperationException("Missing Population Snapshot");
    }
}
