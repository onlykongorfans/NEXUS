namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.ServerManagement;

/// <summary>
///     Tests for the production-only block that prevents the built-in <see cref="OOTB.Accounts.OPERATOR"/> host account, which ships with a publicly-known password, from hosting match servers.
///     The block is applied at both server requester authentication endpoints (the match server manager's "replay_auth" and the match server's "new_session"), and only when the master server runs in the production environment, so that self-hosters running outside production are unaffected.
/// </summary>
public sealed class HostingAuthenticationTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const string DedicatedHostAccountName = "DEDICATED-HOST";

    private const string HostPassword = "HOST-PASSWORD";

    [Test]
    public async Task Operator_Manager_Authentication_Is_Rejected_In_Production()
    {
        await InitialiseIn("Production");

        HttpResponseMessage response = await AuthenticateManager(OOTB.Accounts.OPERATOR.Name);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Operator_Server_Authentication_Is_Rejected_In_Production()
    {
        await InitialiseIn("Production");

        HttpResponseMessage response = await AuthenticateServer($"{OOTB.Accounts.OPERATOR.Name}:1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Operator_Authentication_Is_Allowed_Outside_Production()
    {
        await InitialiseIn("Development");

        using (Assert.Multiple())
        {
            await Assert.That((await AuthenticateManager(OOTB.Accounts.OPERATOR.Name)).StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That((await AuthenticateServer($"{OOTB.Accounts.OPERATOR.Name}:1")).StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Dedicated_Host_Account_Authentication_Is_Allowed_In_Production()
    {
        await InitialiseIn("Production");

        using (Assert.Multiple())
        {
            await Assert.That((await AuthenticateManager(DedicatedHostAccountName)).StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That((await AuthenticateServer($"{DedicatedHostAccountName}:1")).StatusCode).IsNotEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Host_Authentication_Advertises_Internal_Chat_Endpoints()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);
        HttpResponseMessage serverResponse = await AuthenticateServer($"{DedicatedHostAccountName}:1");

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(serverResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        IDictionary<object, object> managerData = await DeserialiseAuthenticationResponse(managerResponse);
        IDictionary<object, object> serverData = await DeserialiseAuthenticationResponse(serverResponse);

        using (Assert.Multiple())
        {
            await Assert.That(managerData["chat_address"] as string).IsEqualTo("internal-chat.test");
            await Assert.That(Convert.ToInt32(managerData["chat_port"])).IsEqualTo(11033);
            await Assert.That(serverData["chat_address"] as string).IsEqualTo("internal-chat.test");
            await Assert.That(Convert.ToInt32(serverData["chat_port"])).IsEqualTo(11032);
        }
    }

    [Test]
    public async Task Empty_Instance_Registrations_Reuse_Identity_And_Cookie_By_IP_Address_And_Port()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

        HttpResponseMessage firstResponse = await AuthenticateServer($"{DedicatedHostAccountName}:");

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int firstServerID, string firstSessionCookie) = await DeserialiseServerAuthenticationResponse(firstResponse);

        MatchServer firstMatchServer = await distributedCacheStore.GetMatchServerByID(firstServerID)
            ?? throw new NullReferenceException("First Match Server Is NULL");

        HttpResponseMessage secondResponse = await AuthenticateServer($"{DedicatedHostAccountName}:");

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int secondServerID, string secondSessionCookie) = await DeserialiseServerAuthenticationResponse(secondResponse);

        MatchServer secondMatchServer = await distributedCacheStore.GetMatchServerByID(secondServerID)
            ?? throw new NullReferenceException("Second Match Server Is NULL");

        HttpResponseMessage differentPortResponse = await AuthenticateServer($"{DedicatedHostAccountName}:", 11236);

        await Assert.That(differentPortResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int differentPortServerID, string differentPortSessionCookie) = await DeserialiseServerAuthenticationResponse(differentPortResponse);

        MatchServer differentPortMatchServer = await distributedCacheStore.GetMatchServerByID(differentPortServerID)
            ?? throw new NullReferenceException("Different-Port Match Server Is NULL");

        using (Assert.Multiple())
        {
            await Assert.That(firstMatchServer.Instance).IsGreaterThan(0);
            await Assert.That(secondMatchServer.Instance).IsEqualTo(firstMatchServer.Instance);
            await Assert.That(secondServerID).IsEqualTo(firstServerID);
            await Assert.That(secondSessionCookie).IsEqualTo(firstSessionCookie);
            await Assert.That(firstMatchServer.Cookie).IsEqualTo(firstSessionCookie);
            await Assert.That(secondMatchServer.Cookie).IsEqualTo(secondSessionCookie);

            await Assert.That(differentPortMatchServer.Instance).IsGreaterThan(0);
            await Assert.That(differentPortMatchServer.Instance).IsNotEqualTo(firstMatchServer.Instance);
            await Assert.That(differentPortServerID).IsNotEqualTo(firstServerID);
            await Assert.That(differentPortSessionCookie).IsNotEqualTo(firstSessionCookie);
            await Assert.That(differentPortMatchServer.Cookie).IsEqualTo(differentPortSessionCookie);
        }
    }

    [Test]
    public async Task Explicit_Instance_Registrations_Reuse_Identity_And_Cookie()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage firstResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);
        HttpResponseMessage secondResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int firstServerID, string firstSessionCookie) = await DeserialiseServerAuthenticationResponse(firstResponse);
        (int secondServerID, string secondSessionCookie) = await DeserialiseServerAuthenticationResponse(secondResponse);

        using (Assert.Multiple())
        {
            await Assert.That(firstServerID).IsEqualTo(secondServerID);
            await Assert.That(firstSessionCookie).IsEqualTo(secondSessionCookie);
        }
    }

    [Test]
    public async Task Concurrent_Explicit_Instance_Registrations_Reuse_One_Identity_And_Cookie()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        TaskCompletionSource registrationGate = new (TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage>[] registrationTasks = [.. Enumerable.Range(0, 8).Select(async _ =>
        {
            await registrationGate.Task;

            return await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);
        })];

        registrationGate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(registrationTasks);

        await Assert.That(responses.All(response => response.StatusCode is HttpStatusCode.OK)).IsTrue();

        List<(int ServerID, string SessionCookie)> registrations = [];

        foreach (HttpResponseMessage response in responses)
            registrations.Add(await DeserialiseServerAuthenticationResponse(response));

        using (Assert.Multiple())
        {
            await Assert.That(registrations.Select(registration => registration.ServerID).Distinct().Count()).IsEqualTo(1);
            await Assert.That(registrations.Select(registration => registration.SessionCookie).Distinct().Count()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Explicit_Instance_Registration_Cannot_Overwrite_A_Different_Address()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage firstResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int firstServerID, string firstSessionCookie) = await DeserialiseServerAuthenticationResponse(firstResponse);

        HttpResponseMessage conflictingResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11237);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

        MatchServer preservedMatchServer = await distributedCacheStore.GetMatchServerByAccountNameAndInstance(DedicatedHostAccountName, 2)
            ?? throw new NullReferenceException("Preserved Match Server Is NULL");

        using (Assert.Multiple())
        {
            await Assert.That(conflictingResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(preservedMatchServer.ID).IsEqualTo(firstServerID);
            await Assert.That(preservedMatchServer.Cookie).IsEqualTo(firstSessionCookie);
            await Assert.That(preservedMatchServer.Port).IsEqualTo(11236);
        }
    }

    [Test]
    public async Task Different_Explicit_Instances_Cannot_Share_An_IP_Address_And_Port()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage firstResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int firstServerID, string firstSessionCookie) = await DeserialiseServerAuthenticationResponse(firstResponse);

        HttpResponseMessage conflictingResponse = await AuthenticateServer($"{DedicatedHostAccountName}:3", 11236);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

        MatchServer preservedMatchServer = await distributedCacheStore.GetMatchServerByAccountNameAndInstance(DedicatedHostAccountName, 2)
            ?? throw new NullReferenceException("Preserved Match Server Is NULL");

        MatchServer? conflictingMatchServer = await distributedCacheStore.GetMatchServerByAccountNameAndInstance(DedicatedHostAccountName, 3);

        using (Assert.Multiple())
        {
            await Assert.That(conflictingResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(preservedMatchServer.ID).IsEqualTo(firstServerID);
            await Assert.That(preservedMatchServer.Cookie).IsEqualTo(firstSessionCookie);
            await Assert.That(conflictingMatchServer).IsNull();
        }
    }

    [Test]
    public async Task Explicit_Instance_Reauthentication_Preserves_Existing_Server_Metadata()
    {
        await InitialiseIn("Development");

        HttpResponseMessage managerResponse = await AuthenticateManager(DedicatedHostAccountName);

        await Assert.That(managerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage firstResponse = await AuthenticateServer($"{DedicatedHostAccountName}:2", 11236);

        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int firstServerID, string firstSessionCookie) = await DeserialiseServerAuthenticationResponse(firstResponse);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        IDatabase distributedCacheStore = scope.ServiceProvider.GetRequiredService<IDatabase>();

        MatchServer existingMatchServer = await distributedCacheStore.GetMatchServerByID(firstServerID)
            ?? throw new NullReferenceException("Existing Match Server Is NULL");

        string existingDisplayName = Random.Shared.Next().ToString("X8");
        string existingLocation = "AUS";
        DateTimeOffset existingRegistrationTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        ChatProtocol.ServerStatus existingStatus = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;

        existingMatchServer.Name = existingDisplayName;
        existingMatchServer.Location = existingLocation;
        existingMatchServer.TimestampRegistered = existingRegistrationTimestamp;
        existingMatchServer.Status = existingStatus;

        await distributedCacheStore.SetMatchServer(DedicatedHostAccountName, existingMatchServer);

        HttpResponseMessage secondResponse = await AuthenticateServer(
            $"{DedicatedHostAccountName}:2",
            11236,
            serverName: "NEXUS AU",
            serverLocation: "EU");

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        (int secondServerID, string secondSessionCookie) = await DeserialiseServerAuthenticationResponse(secondResponse);

        MatchServer preservedMatchServer = await distributedCacheStore.GetMatchServerByID(secondServerID)
            ?? throw new NullReferenceException("Preserved Match Server Is NULL");

        using (Assert.Multiple())
        {
            await Assert.That(secondServerID).IsEqualTo(firstServerID);
            await Assert.That(secondSessionCookie).IsEqualTo(firstSessionCookie);
            await Assert.That(preservedMatchServer.Name).IsEqualTo(existingDisplayName);
            await Assert.That(preservedMatchServer.Location).IsEqualTo(existingLocation);
            await Assert.That(preservedMatchServer.TimestampRegistered).IsEqualTo(existingRegistrationTimestamp);
            await Assert.That(preservedMatchServer.Status).IsEqualTo(existingStatus);
        }
    }

    private async Task InitialiseIn(string environment)
    {
        await webApplicationFactory.WithEnvironment(environment).WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

        await EnsureHostAccountExists(OOTB.Accounts.OPERATOR.Name, OOTB.Accounts.OPERATOR.EmailAddress);
        await EnsureHostAccountExists(DedicatedHostAccountName, "dedicated-host@kongor.test");
    }

    private async Task<HttpResponseMessage> AuthenticateManager(string login)
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            { "login", login },
            { "pass", ComputeServerPasswordHash(HostPassword) }
        };

        return await httpClient.PostAsync("server_requester.php?f=replay_auth", new FormUrlEncodedContent(formData));
    }

    private async Task<HttpResponseMessage> AuthenticateServer(string login, int serverPort = 11235, string serverName = "TEST-SERVER", string serverLocation = "EU", string serverIPAddress = "127.0.0.1")
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            { "login", login },
            { "pass", ComputeServerPasswordHash(HostPassword) },
            { "port", serverPort.ToString() },
            { "name", serverName },
            { "desc", "Test Server" },
            { "location", serverLocation },
            { "ip", serverIPAddress }
        };

        return await httpClient.PostAsync("server_requester.php?f=new_session", new FormUrlEncodedContent(formData));
    }

    private static async Task<(int ServerID, string SessionCookie)> DeserialiseServerAuthenticationResponse(HttpResponseMessage response)
    {
        IDictionary<object, object> responseData = await DeserialiseAuthenticationResponse(response);

        int serverID = Convert.ToInt32(responseData["server_id"]);
        string sessionCookie = responseData["session"] as string ?? throw new NullReferenceException("Server Authentication Session Cookie Is NULL");

        return (serverID, sessionCookie);
    }

    private static async Task<IDictionary<object, object>> DeserialiseAuthenticationResponse(HttpResponseMessage response)
    {
        string responseBody = await response.Content.ReadAsStringAsync();

        return PhpSerialization.Deserialize(responseBody) as IDictionary<object, object>
            ?? throw new NullReferenceException("Authentication Response Data Is NULL");
    }

    /// <summary>
    ///     Computes the password value a match server or match server manager sends, which is the lower-case hexadecimal MD5 of the password.
    ///     The master server wraps this with the account salt to compare against the stored hash.
    /// </summary>
    private static string ComputeServerPasswordHash(string password)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLower();

    private async Task EnsureHostAccountExists(string accountName, string emailAddress)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext merrickContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        if (await merrickContext.Accounts.AnyAsync(account => account.Name.Equals(accountName)))
            return;

        Role role = await merrickContext.Roles.SingleAsync(candidate => candidate.Name.Equals(UserRoles.User));

        string salt = SRPPasswordHasher.GenerateSRPPasswordSalt();

        User user = new ()
        {
            EmailAddress = emailAddress,
            Role = role,
            SRPPasswordSalt = salt,
            SRPPasswordHash = SRPPasswordHasher.ComputeSRPPasswordHash(HostPassword, salt)
        };

        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, HostPassword);

        Account account = new ()
        {
            Name = accountName,
            User = user,
            Type = AccountType.ServerHost,
            IsMain = true
        };

        user.Accounts.Add(account);

        await merrickContext.Users.AddAsync(user);

        await merrickContext.SaveChangesAsync();
    }
}
