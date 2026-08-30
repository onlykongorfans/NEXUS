namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Accounts;

/// <summary>
///     Integration tests for switching between identities through the legacy <c>switch_auth</c> client request.
/// </summary>
public sealed class SubAccountSwitchTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    private const string Password = "SecurePassword123!";

    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task An_Authenticated_Session_Can_Switch_From_Main_To_Sub_And_Back()
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account mainAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("switch.both@kongor.com", "SwitchMain", Password);
        Account subAccount = await AddSubAccount(mainAccount.ID, "SwitchSub");
        SRPAuthenticationData authentication = await authenticationService.PerformFullAuthentication(mainAccount, Password);

        string mainCookie = authentication.Cookie ?? throw new InvalidOperationException("Main Account Cookie Is NULL");

        using HttpResponseMessage switchToSubResponse = await SendSwitchRequest(mainCookie, subAccount.ID, subAccount.Name);
        IDictionary<object, object> switchToSubData = await DeserialiseResponse(switchToSubResponse);
        string subCookie = switchToSubData["cookie"] as string ?? throw new InvalidOperationException("Sub-Account Cookie Is NULL");

        IDatabase distributedCache = webApplicationFactory.Services.GetRequiredService<IDatabase>();
        (bool mainCookieIsValid, string? mainCookieAccountName) = await distributedCache.ValidateAccountSessionCookie(mainCookie);
        (bool subCookieIsValid, string? subCookieAccountName) = await distributedCache.ValidateAccountSessionCookie(subCookie);

        using (Assert.Multiple())
        {
            await Assert.That(switchToSubResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(switchToSubData["nickname"]?.ToString()).IsEqualTo(subAccount.Name);
            await Assert.That(Convert.ToInt32(switchToSubData["account_id"])).IsEqualTo(subAccount.ID);
            await Assert.That(Convert.ToInt32(switchToSubData["super_id"])).IsEqualTo(mainAccount.ID);
            await Assert.That(Convert.ToBoolean(switchToSubData["is_current_subaccount"])).IsTrue();
            await Assert.That(switchToSubData["auth_hash"]?.ToString()).IsNotEmpty();
            await Assert.That(switchToSubData["chat_url"]?.ToString()).IsEqualTo("public-chat.test");
            await Assert.That(mainCookieIsValid).IsFalse();
            await Assert.That(mainCookieAccountName).IsNull();
            await Assert.That(subCookieIsValid).IsTrue();
            await Assert.That(subCookieAccountName).IsEqualTo(subAccount.Name);
        }

        using HttpResponseMessage switchToMainResponse = await SendSwitchRequest(subCookie, mainAccount.ID, mainAccount.Name);
        IDictionary<object, object> switchToMainData = await DeserialiseResponse(switchToMainResponse);
        string replacementMainCookie = switchToMainData["cookie"] as string ?? throw new InvalidOperationException("Replacement Main Account Cookie Is NULL");

        (bool previousSubCookieIsValid, string? previousSubCookieAccountName) = await distributedCache.ValidateAccountSessionCookie(subCookie);
        (bool replacementMainCookieIsValid, string? replacementMainCookieAccountName) = await distributedCache.ValidateAccountSessionCookie(replacementMainCookie);

        using (Assert.Multiple())
        {
            await Assert.That(switchToMainResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(switchToMainData["nickname"]?.ToString()).IsEqualTo(mainAccount.Name);
            await Assert.That(Convert.ToInt32(switchToMainData["account_id"])).IsEqualTo(mainAccount.ID);
            await Assert.That(Convert.ToInt32(switchToMainData["super_id"])).IsEqualTo(mainAccount.ID);
            await Assert.That(Convert.ToBoolean(switchToMainData["is_current_subaccount"])).IsFalse();
            await Assert.That(previousSubCookieIsValid).IsFalse();
            await Assert.That(previousSubCookieAccountName).IsNull();
            await Assert.That(replacementMainCookieIsValid).IsTrue();
            await Assert.That(replacementMainCookieAccountName).IsEqualTo(mainAccount.Name);
        }
    }

    [Test]
    public async Task A_Mismatched_Target_Name_And_ID_Is_Rejected_Without_Invalidating_The_Current_Session()
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account mainAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("switch.mismatch@kongor.com", "MismatchMain", Password);
        Account subAccount = await AddSubAccount(mainAccount.ID, "MismatchSub");
        SRPAuthenticationData authentication = await authenticationService.PerformFullAuthentication(mainAccount, Password);
        string cookie = authentication.Cookie ?? throw new InvalidOperationException("Account Cookie Is NULL");

        using HttpResponseMessage response = await SendSwitchRequest(cookie, mainAccount.ID, subAccount.Name);
        IDictionary<object, object> responseData = await DeserialiseResponse(response);
        (bool cookieIsValid, string? cookieAccountName) = await webApplicationFactory.Services.GetRequiredService<IDatabase>()
            .ValidateAccountSessionCookie(cookie);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(responseData["auth"]?.ToString()).IsEqualTo("Account Not Found");
            await Assert.That(cookieIsValid).IsTrue();
            await Assert.That(cookieAccountName).IsEqualTo(mainAccount.Name);
        }
    }

    [Test]
    public async Task A_Session_Cannot_Switch_To_An_Account_Belonging_To_Another_User()
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account requesterAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("switch.requester@kongor.com", "SwitchReq", Password);
        (Account unrelatedAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("switch.unrelated@kongor.com", "SwitchOther", Password);
        SRPAuthenticationData authentication = await authenticationService.PerformFullAuthentication(requesterAccount, Password);
        string cookie = authentication.Cookie ?? throw new InvalidOperationException("Requester Cookie Is NULL");

        using HttpResponseMessage response = await SendSwitchRequest(cookie, unrelatedAccount.ID, unrelatedAccount.Name);
        IDictionary<object, object> responseData = await DeserialiseResponse(response);
        (bool cookieIsValid, string? cookieAccountName) = await webApplicationFactory.Services.GetRequiredService<IDatabase>()
            .ValidateAccountSessionCookie(cookie);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(responseData["auth"]?.ToString()).IsEqualTo("Account Not Found");
            await Assert.That(cookieIsValid).IsTrue();
            await Assert.That(cookieAccountName).IsEqualTo(requesterAccount.Name);
        }
    }

    [Test]
    public async Task A_Forged_Cookie_Cannot_Switch_Accounts()
    {
        SRPAuthenticationService authenticationService = new (webApplicationFactory);
        (Account mainAccount, string _) = await authenticationService.CreateAccountWithSRPCredentials("switch.forged@kongor.com", "ForgedMain", Password);
        Account subAccount = await AddSubAccount(mainAccount.ID, "ForgedSub");

        using HttpResponseMessage response = await SendSwitchRequest("forged-cookie", subAccount.ID, subAccount.Name);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private async Task<Account> AddSubAccount(int mainAccountID, string subAccountName)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        Account mainAccount = await databaseContext.Accounts
            .Include(account => account.User).ThenInclude(user => user.Accounts)
            .SingleAsync(account => account.ID == mainAccountID);

        Account subAccount = new ()
        {
            Name = subAccountName,
            User = mainAccount.User,
            IsMain = false
        };

        mainAccount.User.Accounts.Add(subAccount);

        await databaseContext.SaveChangesAsync();

        return subAccount;
    }

    private async Task<HttpResponseMessage> SendSwitchRequest(string cookie, int targetAccountID, string targetAccountName)
    {
        HttpClient httpClient = webApplicationFactory.CreateClient();

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "S2 Games/Heroes Of Newerth/4.10.1.0/wac/x86_64");

        Dictionary<string, string> formData = new ()
        {
            ["login"] = targetAccountName,
            ["account_id"] = targetAccountID.ToString(),
            ["cookie"] = cookie
        };

        return await httpClient.PostAsync("client_requester.php?f=switch_auth", new FormUrlEncodedContent(formData));
    }

    private static async Task<IDictionary<object, object>> DeserialiseResponse(HttpResponseMessage response)
    {
        string responseBody = await response.Content.ReadAsStringAsync();

        return PhpSerialization.Deserialize(responseBody) as IDictionary<object, object>
            ?? throw new InvalidOperationException("Response Was Not A PHP-Serialised Dictionary");
    }
}
