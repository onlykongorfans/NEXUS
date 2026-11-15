namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.ServerManagement;

public sealed class ReplayTransferTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const int MatchID = 123456;
    private const string HostName = "ReplayHost";
    private const string HostPassword = "Replay-Test-Password";
    private static readonly byte[] ReplayBytes = Encoding.UTF8.GetBytes("Recorded native replay fixture");

    [Before(HookType.Test)]
    public Task Initialise() => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task Native_Authorisation_Returns_A_Scoped_Form_Grant_Without_Storage_Credentials()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new ();
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        Dictionary<string, string> form = await AuthorisationForm(storage);
        ServerRequesterController controller = CreateRequester(scope.ServiceProvider, storage, form);

        OkObjectResult response = (OkObjectResult) await controller.ServerRequester();
        string body = (string) (response.Value ?? throw new InvalidOperationException());
        IDictionary<object, object> data = PhpSerialization.Deserialize(body) as IDictionary<object, object> ?? throw new InvalidOperationException("Missing Replay Response");
        IDictionary<object, object> fields = (IDictionary<object, object>) data["headers"];
        string token = (string) fields["upload_token"];
        ReplayUploadGrant? grant = await storage.GetGrant(token);

        await Assert.That(data["TargetURL"].ToString()).IsEqualTo("master.test/replays/upload");
        await Assert.That(grant?.MatchID).IsEqualTo(MatchID);
        await Assert.That(grant?.HostAccountName).IsEqualTo(HostName);
        await Assert.That(body.Contains("storage-secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(await storage.GetAuthorisation(MatchID, HostName)).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Invalid_Authorisation_Or_Other_Host_Match_Is_Rejected(bool wrongPassword)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider, wrongPassword ? HostName : "AnotherHost");
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, new TestStorageHandler());
        Dictionary<string, string> form = await AuthorisationForm(storage);
        if (wrongPassword) form["pass"] = "incorrect";
        ServerRequesterController controller = CreateRequester(scope.ServiceProvider, storage, form);
        await Assert.That(await controller.ServerRequester()).IsTypeOf<UnauthorizedResult>();
    }

    [Test]
    [Arguments("pending")]
    [Arguments("address")]
    [Arguments("signature")]
    [Arguments("size")]
    public async Task Native_Request_Requires_A_Pending_Match_And_Matching_Source_Signature_And_Size(string invalidField)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, new TestStorageHandler());
        Dictionary<string, string> form = await AuthorisationForm(storage);
        ServerRequesterController controller = CreateRequester(scope.ServiceProvider, storage, form);

        if (invalidField is "pending")
            await scope.ServiceProvider.GetRequiredService<IDatabase>().KeyDeleteAsync(ReplayStorageService.AuthorisationKey(HostName.GetDeterministicInt32Hash(), MatchID));
        else if (invalidField is "address")
            controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        else if (invalidField is "signature")
            form["hash_key"] = new string('0', 40);
        else
            form["file_size"] = (ReplayBytes.Length + 1).ToString();

        controller.Request.Form = new FormCollection(form.ToDictionary(entry => entry.Key, entry => new StringValues(entry.Value)));
        await Assert.That(await controller.ServerRequester()).IsTypeOf<UnauthorizedResult>();
    }

    [Test]
    public async Task Native_Multipart_Upload_Is_Relayed_As_Authenticated_PUT_Then_Acknowledged_With_204()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new ();
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        string token = await CreateGrant(storage);
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, token, ReplayBytes);

        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<NoContentResult>();
        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(handler.URL).IsEqualTo($"https://storage.test/replays/M{MatchID}.honreplay");
        await Assert.That(handler.HasAuthentication).IsTrue();
        await Assert.That(handler.Bytes).IsEquivalentTo(ReplayBytes);
        await Assert.That(await storage.GetGrant(token)).IsNull();

        MerrickContext database = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        await Assert.That((await database.MatchStatistics.SingleAsync()).FileSize).IsEqualTo(ReplayBytes.Length);
        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<UnauthorizedResult>();
    }

    [Test]
    public async Task Missing_Grant_Is_Rejected_Without_Contacting_Storage()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        TestStorageHandler handler = new ();
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, new string('A', 64), ReplayBytes);
        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<UnauthorizedResult>();
        await Assert.That(handler.Method).IsNull();
    }

    [Test]
    public async Task An_Existing_Stored_Replay_Is_Not_Overwritten()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new () { ExistingSize = ReplayBytes.Length };
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, await CreateGrant(storage), ReplayBytes);
        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<ConflictObjectResult>();
        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Head);
    }

    [Test]
    public async Task Upload_Grant_Cannot_Write_A_Client_Patch_Path()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new ();
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, await CreateGrant(storage), ReplayBytes);
        IFormCollection original = controller.Request.Form;
        controller.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["upload_token"] = original["upload_token"], ["key"] = "../wac/manifest.json"
        }, new FormFileCollection { original.Files.Single() });
        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<BadRequestObjectResult>();
        await Assert.That(handler.Method).IsNull();
    }

    [Test]
    public async Task Corrupt_Replay_Is_Rejected_Before_Contacting_Storage()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new ();
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        byte[] corruptBytes = ReplayBytes.ToArray();
        corruptBytes[0] ^= 1;
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, await CreateGrant(storage), corruptBytes);
        await Assert.That(await controller.Upload(CancellationToken.None)).IsTypeOf<BadRequestObjectResult>();
        await Assert.That(handler.Method).IsNull();
    }

    [Test]
    public async Task Storage_Failure_Does_Not_Consume_The_Grant_Or_Mark_The_Replay_Available()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        TestStorageHandler handler = new () { StatusCode = HttpStatusCode.ServiceUnavailable };
        ReplayStorageService storage = CreateStorage(scope.ServiceProvider, handler);
        string token = await CreateGrant(storage);
        ReplayUploadController controller = CreateReceiver(scope.ServiceProvider, storage, token, ReplayBytes);
        StatusCodeResult response = (StatusCodeResult) await controller.Upload(CancellationToken.None);
        await Assert.That(response.StatusCode).IsEqualTo(502);
        await Assert.That(await storage.GetGrant(token)).IsNotNull();
        await Assert.That((await scope.ServiceProvider.GetRequiredService<MerrickContext>().MatchStatistics.SingleAsync()).FileSize).IsEqualTo(ReplayBytes.Length);
    }

    [Test]
    public async Task Authenticated_Native_Size_Update_Is_Persisted_Without_Claiming_A_CDN_Upload()
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        await Seed(scope.ServiceProvider);
        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();
        MatchServer server = new ()
        {
            HostAccountID = 1, HostAccountName = HostName, ID = 1, Name = "Test Server", Instance = 1,
            MatchServerManagerID = null, IPAddress = "127.0.0.1", Port = 11235, Location = "AU", Description = "", Cookie = "server-session"
        };
        await cache.SetMatchServer(HostName, server);
        ServerRequesterController controller = CreateRequester(scope.ServiceProvider, CreateStorage(scope.ServiceProvider, new TestStorageHandler()), new ()
        {
            ["f"] = "set_replay_size", ["session"] = "server-session", ["match_id"] = MatchID.ToString(), ["file_size"] = ReplayBytes.Length.ToString()
        });
        await Assert.That(await controller.ServerRequester()).IsTypeOf<OkObjectResult>();
        await Assert.That((await scope.ServiceProvider.GetRequiredService<MerrickContext>().MatchStatistics.SingleAsync()).FileSize).IsEqualTo(ReplayBytes.Length);
        await Assert.That(await cache.KeyExistsAsync(ReplayStorageService.AvailabilityKey(MatchID))).IsFalse();
    }

    private async Task Seed(IServiceProvider services, string owner = HostName)
    {
        SRPAuthenticationService accounts = new (webApplicationFactory);
        (Account account, string password) = await accounts.CreateAccountWithSRPCredentials("replay@test.invalid", HostName, HostPassword);
        MerrickContext database = services.GetRequiredService<MerrickContext>();
        Account host = await database.Accounts.SingleAsync(candidate => candidate.ID == account.ID);
        host.Type = AccountType.ServerHost;
        MatchStatistics match = MatchDataHelper.BuildMatchStatistics();
        match.MatchID = MatchID;
        match.ServerID = 1;
        match.HostAccountName = owner;
        match.FileSize = ReplayBytes.Length;
        database.MatchStatistics.Add(match);
        await database.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, string>> AuthorisationForm(ReplayStorageService storage)
    {
        await storage.CreateAuthorisation(MatchID, HostName, "127.0.0.1");
        return new ()
        {
            ["f"] = "sm_upload_request", ["man_masterLogin"] = HostName + ":", ["pass"] = "native-test-password",
            ["hash_key"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{MatchID}honreplay{new string('A', 64)}"))),
            ["match_id"] = MatchID.ToString(), ["file_extension"] = "honreplay", ["file_size"] = ReplayBytes.Length.ToString(),
            ["md5_checksum"] = Convert.ToHexString(MD5.HashData(ReplayBytes))
        };
    }

    private static ReplayStorageService CreateStorage(IServiceProvider services, TestStorageHandler handler) => new (
        new HttpClient(handler), new ReplayStorageSettings
        {
            UploadBaseURL = "https://storage.test/replays/", DownloadBaseURL = "http://files.test/replays/",
            ReceiverURL = "http://master.test/replays/upload", UserName = "storage-user", Password = "storage-secret",
            NativePasswordSHA256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("native-test-password"))), NativeHashSuffix = new string('A', 64)
        }, services.GetRequiredService<IDatabase>(), NullLogger<ReplayStorageService>.Instance);

    private static Task<string> CreateGrant(ReplayStorageService storage) => storage.CreateGrant(new ReplayUploadGrant(MatchID, HostName, ReplayBytes.Length, Convert.ToHexString(MD5.HashData(ReplayBytes))));

    private static ServerRequesterController CreateRequester(IServiceProvider services, ReplayStorageService storage, Dictionary<string, string> form) => new (
        services.GetRequiredService<MerrickContext>(), services.GetRequiredService<IDatabase>(), NullLogger<ServerRequesterController>.Instance,
        services.GetRequiredService<IOptions<OperationalConfiguration>>(), services.GetRequiredService<IWebHostEnvironment>(), storage)
    {
        ControllerContext = new ControllerContext { HttpContext = Context(form) }
    };

    private static ReplayUploadController CreateReceiver(IServiceProvider services, ReplayStorageService storage, string token, byte[] bytes) => new (
        storage, services.GetRequiredService<IDatabase>(), services.GetRequiredService<MerrickContext>(), NullLogger<ReplayUploadController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = Context(new () { ["upload_token"] = token, ["key"] = $"M{MatchID}.honreplay" },
                new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "untrusted-filename.honreplay") })
        }
    };

    private static DefaultHttpContext Context(Dictionary<string, string> form, FormFileCollection? files = null)
    {
        DefaultHttpContext context = new ();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.ContentType = files is null ? "application/x-www-form-urlencoded" : "multipart/form-data";
        context.Request.Form = new FormCollection(form.ToDictionary(entry => entry.Key, entry => new StringValues(entry.Value)), files);
        return context;
    }

    private sealed class TestStorageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.Created;
        public int? ExistingSize { get; init; }
        public HttpMethod? Method { get; private set; }
        public string? URL { get; private set; }
        public bool HasAuthentication { get; private set; }
        public byte[] Bytes { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            URL = request.RequestUri?.ToString();
            HasAuthentication = request.Headers.Authorization?.Scheme == "Basic";
            Bytes = request.Content is not null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : [];
            HttpResponseMessage response = new (StatusCode);
            if (request.Method == HttpMethod.Head && ExistingSize.HasValue)
                response.Content = new ByteArrayContent(new byte[ExistingSize.Value]);
            return response;
        }
    }
}
