namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Social;

/// <summary>
///     Integration tests for persisting the game client's auto-connect chat channel list.
/// </summary>
public sealed class AutoConnectChatChannelTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task Adding_A_Channel_Persists_It_And_Returns_Client_Success()
    {
        (string cookie, int accountID) = await SeedSession("channel.add@kongor.com", "ChannelAdd");

        IDictionary<object, object> responseData = await SendRequest("add_room", cookie, accountID, "aushon");
        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responseData["add_room"] as string).IsEqualTo("OK");
            await Assert.That(account.AutoConnectChatChannels).IsEquivalentTo(["aushon"]);
        }
    }

    [Test]
    public async Task Adding_An_Existing_Channel_Is_Idempotent_And_Preserves_Its_Original_Casing()
    {
        (string cookie, int accountID) = await SeedSession("channel.duplicate@kongor.com", "ChannelDup");

        await SendRequest("add_room", cookie, accountID, "aushon");
        IDictionary<object, object> responseData = await SendRequest("add_room", cookie, accountID, "AUSHON");

        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responseData["add_room"] as string).IsEqualTo("OK");
            await Assert.That(account.AutoConnectChatChannels).IsEquivalentTo(["aushon"]);
        }
    }

    [Test]
    public async Task Removing_A_Channel_Is_Case_Insensitive_And_Returns_Client_Success()
    {
        (string cookie, int accountID) = await SeedSession("channel.remove@kongor.com", "ChannelRemove");

        await SendRequest("add_room", cookie, accountID, "aushon");
        IDictionary<object, object> responseData = await SendRequest("remove_room", cookie, accountID, "AUSHON");

        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responseData["remove_room"] as string).IsEqualTo("OK");
            await Assert.That(account.AutoConnectChatChannels).IsEmpty();
        }
    }

    [Test]
    public async Task Removing_An_Absent_Channel_Is_Idempotent()
    {
        (string cookie, int accountID) = await SeedSession("channel.absent@kongor.com", "ChannelAbsent");

        IDictionary<object, object> responseData = await SendRequest("remove_room", cookie, accountID, "aushon");
        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responseData["remove_room"] as string).IsEqualTo("OK");
            await Assert.That(account.AutoConnectChatChannels).IsEmpty();
        }
    }

    [Test]
    public async Task Clearing_Channels_Removes_Every_Saved_Channel_And_Returns_Client_Success()
    {
        (string cookie, int accountID) = await SeedSession("channel.clear@kongor.com", "ChannelClear");

        await SendRequest("add_room", cookie, accountID, "aushon");
        await SendRequest("add_room", cookie, accountID, "another channel");

        IDictionary<object, object> responseData = await SendRequest("clear_rooms", cookie, accountID);
        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(responseData["clear_rooms"] as string).IsEqualTo("OK");
            await Assert.That(account.AutoConnectChatChannels).IsEmpty();
        }
    }

    [Test]
    public async Task A_Request_Cannot_Modify_Another_Account_Using_Its_Account_ID()
    {
        (string requesterCookie, int requesterAccountID) = await SeedSession("channel.requester@kongor.com", "ChannelReq");
        (string _, int targetAccountID) = await SeedSession("channel.target@kongor.com", "ChannelTarget");

        HttpResponseMessage response = await SendRawRequest("add_room", requesterCookie, targetAccountID, "aushon");

        Account requesterAccount = await LoadAccount(requesterAccountID);
        Account targetAccount = await LoadAccount(targetAccountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(requesterAccount.AutoConnectChatChannels).IsEmpty();
            await Assert.That(targetAccount.AutoConnectChatChannels).IsEmpty();
        }
    }

    [Test]
    public async Task Adding_A_Channel_Longer_Than_The_Protocol_Limit_Is_Rejected()
    {
        (string cookie, int accountID) = await SeedSession("channel.length@kongor.com", "ChannelLength");
        string channelName = new ('a', ChatProtocol.CHAT_CHANNEL_MAX_LENGTH + 1);

        HttpResponseMessage response = await SendRawRequest("add_room", cookie, accountID, channelName);
        Account account = await LoadAccount(accountID);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(account.AutoConnectChatChannels).IsEmpty();
        }
    }

    private async Task<(string Cookie, int AccountID)> SeedSession(string emailAddress, string accountName)
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);

        (Account account, string _) = await authenticationService.CreateAccountWithSRPCredentials(emailAddress, accountName, "DoesNotMatter123!");

        string cookie = Guid.NewGuid().ToString("N");

        IDatabase distributedCache = webApplicationFactory.Services.GetRequiredService<IDatabase>();

        await distributedCache.SetAccountNameForSessionCookie(cookie, accountName);

        return (cookie, account.ID);
    }

    private async Task<IDictionary<object, object>> SendRequest(string function, string cookie, int accountID, string? chatChannelName = null)
    {
        HttpResponseMessage response = await SendRawRequest(function, cookie, accountID, chatChannelName);

        response.EnsureSuccessStatusCode();

        string responseBody = await response.Content.ReadAsStringAsync();

        return PhpSerialization.Deserialize(responseBody) as IDictionary<object, object>
            ?? throw new InvalidOperationException("Response Was Not A PHP-Serialised Dictionary");
    }

    private async Task<HttpResponseMessage> SendRawRequest(string function, string cookie, int accountID, string? chatChannelName = null)
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            { "account_id", accountID.ToString() },
            { "cookie", cookie }
        };

        if (chatChannelName is not null)
            formData["chatroom_name"] = chatChannelName;

        return await httpClient.PostAsync($"client_requester.php?f={function}", new FormUrlEncodedContent(formData));
    }

    private async Task<Account> LoadAccount(int accountID)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        return await databaseContext.Accounts.SingleAsync(account => account.ID == accountID);
    }
}
