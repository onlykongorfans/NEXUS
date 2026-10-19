namespace ASPIRE.Tests.KONGOR.MasterServer.Tests.Social;

public sealed class ChatChannelLoginTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

    [Test]
    public async Task Authenticate_With_SRP_Lists_The_Default_Chat_Channels_First_And_Discards_Saved_Reserved_Chat_Channels()
    {
        SRPAuthenticationService srpAuthenticationService = new (webApplicationFactory);

        (Account account, string password) = await srpAuthenticationService.CreateAccountWithSRPCredentials("channels@kongor.com", "Channels", "SecurePassword123!");

        using (IServiceScope scope = webApplicationFactory.Services.CreateScope())
        {
            MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            Account databaseAccount = await databaseContext.Accounts.SingleAsync(accountRecord => accountRecord.ID == account.ID);

            databaseAccount.Clan = new Clan { Name = "Current Clan", Tag = "CRNT" };
            databaseAccount.ClanTier = ClanTier.Leader;

            // The Saved Clan Channel Belongs To A Clan Which The Account Has Since Left, And The Numbered General Channel Is Only Reachable Through Load Balancing
            databaseAccount.AutoConnectChatChannels = ["Saved Channel", "Clan Former Clan", "KONGOR 2"];

            await databaseContext.SaveChangesAsync();
        }

        SRPAuthenticationData result = await srpAuthenticationService.PerformFullAuthentication(account, password);

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.ChatChannels).IsEquivalentTo(["KONGOR", "Clan Current Clan", "Saved Channel"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Authenticate_With_SRP_Lists_The_Hosts_Chat_Channel_For_An_Account_Whose_User_Owns_A_Host_Account()
    {
        SRPAuthenticationService srpAuthenticationService = new (webApplicationFactory);

        (Account account, string password) = await srpAuthenticationService.CreateAccountWithSRPCredentials("hosting.user@kongor.com", "HostingUser", "SecurePassword123!");

        using (IServiceScope scope = webApplicationFactory.Services.CreateScope())
        {
            MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            User user = await databaseContext.Users.SingleAsync(userRecord => userRecord.Accounts.Any(accountRecord => accountRecord.ID == account.ID));

            await databaseContext.Accounts.AddAsync(new Account { Name = "HostingUserHost", User = user, IsMain = false, Type = AccountType.ServerHost });

            await databaseContext.SaveChangesAsync();
        }

        SRPAuthenticationData result = await srpAuthenticationService.PerformFullAuthentication(account, password);

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.ChatChannels).IsEquivalentTo(["KONGOR", "HOSTS"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }
}
