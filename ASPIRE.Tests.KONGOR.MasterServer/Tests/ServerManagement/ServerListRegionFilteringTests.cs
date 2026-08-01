namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.ServerManagement;

/// <summary>
///     Locks down the region filtering of the create-game server list, which compares at the aggregate level (see <see cref="GameRegions"/>).
///     A server whose location normalises to the wildcard (including unknown locations) appears in every list, and a blank or wildcard request returns all servers.
/// </summary>
public sealed class ServerListRegionFilteringTests
{
    [Test]
    public async Task A_Blank_Region_Returns_All_Servers()
    {
        ServerForCreateListResponse response = BuildResponse(region: null);

        await Assert.That(response.Servers.Count).IsEqualTo(5);
    }

    [Test]
    public async Task The_Newerth_Wildcard_Returns_All_Servers()
    {
        ServerForCreateListResponse response = BuildResponse(GameRegions.Wildcard);

        await Assert.That(response.Servers.Count).IsEqualTo(5);
    }

    [Test]
    [Arguments("US")]
    [Arguments("USE")]
    [Arguments("USW")]
    public async Task A_US_Region_Request_Returns_The_US_Servers_And_The_Wildcard_Servers(string region)
    {
        ServerForCreateListResponse response = BuildResponse(region);

        using (Assert.Multiple())
        {
            await Assert.That(response.Servers.ContainsKey(USEServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(USWServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(WildcardServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(UnknownLocationServerID)).IsTrue();
            await Assert.That(response.Servers.Count).IsEqualTo(4);
        }
    }

    [Test]
    public async Task An_EU_Region_Request_Returns_The_EU_Server_And_The_Wildcard_Servers()
    {
        ServerForCreateListResponse response = BuildResponse("EU");

        using (Assert.Multiple())
        {
            await Assert.That(response.Servers.ContainsKey(EUServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(WildcardServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(UnknownLocationServerID)).IsTrue();
            await Assert.That(response.Servers.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task An_AU_Region_Request_Does_Not_Return_A_South_East_Asian_Server()
    {
        const int AustraliaServerID = 10;
        const int SingaporeServerID = 11;

        List<MatchServer> servers =
        [
            BuildServer(AustraliaServerID, "AU"),
            BuildServer(SingaporeServerID, "SG")
        ];

        ServerForCreateListResponse response = new (servers, "AU", cookie: "test-cookie");

        using (Assert.Multiple())
        {
            await Assert.That(response.Servers.ContainsKey(AustraliaServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(SingaporeServerID)).IsFalse();
            await Assert.That(response.Servers.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task An_Unmapped_Region_Request_Returns_Only_The_Wildcard_Servers()
    {
        ServerForCreateListResponse response = BuildResponse("TOUR");

        using (Assert.Multiple())
        {
            await Assert.That(response.Servers.ContainsKey(WildcardServerID)).IsTrue();
            await Assert.That(response.Servers.ContainsKey(UnknownLocationServerID)).IsTrue();
            await Assert.That(response.Servers.Count).IsEqualTo(2);
        }
    }

    private const int USEServerID = 1;
    private const int USWServerID = 2;
    private const int EUServerID = 3;
    private const int WildcardServerID = 4;
    private const int UnknownLocationServerID = 5;

    private static ServerForCreateListResponse BuildResponse(string? region)
    {
        List<MatchServer> servers =
        [
            BuildServer(USEServerID, "USE"),
            BuildServer(USWServerID, "USW"),
            BuildServer(EUServerID, "EU"),
            BuildServer(WildcardServerID, GameRegions.Wildcard),
            BuildServer(UnknownLocationServerID, "MyBasement")
        ];

        return new ServerForCreateListResponse(servers, region, cookie: "test-cookie");
    }

    private static MatchServer BuildServer(int id, string location)
    {
        return new MatchServer
        {
            HostAccountID = 1,
            HostAccountName = "TestHost",
            ID = id,
            Name = $"Test Server {id}",
            MatchServerManagerID = null,
            Instance = 0,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = location,
            Description = "Test",
            Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE
        };
    }
}
