namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

/// <summary>
///     Verifies that the built-in guest user, whose password is public, remains useful to Development self-hosters but cannot authenticate to a Production master server.
/// </summary>
public sealed class ProductionGuestAuthenticationTests(KONGORIntegrationWebApplicationFactory webApplicationFactory)
{
    [Test]
    public async Task Built_In_Quick_Play_Guest_Cannot_Authenticate_In_Production()
    {
        await webApplicationFactory.WithEnvironment("Production").WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

        SRPAuthenticationService authenticationService = new (webApplicationFactory);

        (Account account, string password) = await authenticationService.CreateAccountWithSRPCredentials(
            OOTB.Accounts.GUEST.EmailAddress,
            "GUEST-01",
            OOTB.Accounts.GUEST.Password);

        await SetAccountType(account.ID, AccountType.Guest);

        SRPAuthenticationData result = await authenticationService.PerformFullAuthentication(account, password);

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.ErrorMessage).IsNotEmpty();
        }
    }

    [Test]
    public async Task Moderator_Can_Authenticate_In_Production_After_Its_Password_Is_Rotated()
    {
        await webApplicationFactory.WithEnvironment("Production").WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

        SRPAuthenticationService authenticationService = new (webApplicationFactory);

        (Account account, string password) = await authenticationService.CreateAccountWithSRPCredentials(
            OOTB.Accounts.GUEST.EmailAddress,
            "MODERATOR",
            "Rotated-Moderator-Password-2026!");

        await SetAccountType(account.ID, AccountType.MatchModerator);

        SRPAuthenticationData result = await authenticationService.PerformFullAuthentication(account, password);

        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Built_In_Guest_User_Can_Authenticate_In_Development()
    {
        await webApplicationFactory.WithEnvironment("Development").WithSQLServerContainer().WithDistributedCacheContainer().InitialiseAsync();

        SRPAuthenticationService authenticationService = new (webApplicationFactory);

        (Account account, string password) = await authenticationService.CreateAccountWithSRPCredentials(
            OOTB.Accounts.GUEST.EmailAddress,
            "GUEST-01",
            OOTB.Accounts.GUEST.Password);

        await SetAccountType(account.ID, AccountType.Guest);

        SRPAuthenticationData result = await authenticationService.PerformFullAuthentication(account, password);

        await Assert.That(result.Success).IsTrue();
    }

    private async Task SetAccountType(int accountID, AccountType accountType)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        Account account = await databaseContext.Accounts.SingleAsync(candidate => candidate.ID == accountID);

        account.Type = accountType;

        await databaseContext.SaveChangesAsync();
    }
}
