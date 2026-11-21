namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class RegisteredAccountCountTests(ZORGATHIntegrationWebApplicationFactory factory)
{
    [Test]
    public async Task Anonymous_Visitors_Get_The_Count_Of_Main_Accounts_Only()
    {
        await factory.WithSQLServerContainer().InitialiseAsync();

        using IServiceScope scope = factory.Services.CreateScope();
        MerrickContext context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        int existingMainAccounts = await context.Accounts.CountAsync(account => account.IsMain);
        Role role = await context.Roles.SingleAsync(role => role.Name == UserRoles.User);
        User user = new ()
        {
            EmailAddress = "registered-count@example.invalid",
            Role = role,
            SRPPasswordHash = "unused",
            SRPPasswordSalt = "unused",
            PBKDF2PasswordHash = "unused"
        };

        context.Accounts.AddRange(
            new Account { Name = "RegisteredMain", IsMain = true, User = user },
            new Account { Name = "RegisteredSub", IsMain = false, User = user });
        await context.SaveChangesAsync();

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        int count = await client.GetFromJsonAsync<int>("/Accounts/RegisteredCount");

        await Assert.That(count).IsEqualTo(existingMainAccounts + 1);
    }
}
