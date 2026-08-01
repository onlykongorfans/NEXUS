using ASPIRE.Common.Constants;

using Microsoft.AspNetCore.Identity;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Verifies that the public credentials for the built-in guest user cannot be exchanged for a Production portal JWT, including through its MODERATOR account.
/// </summary>
public sealed class ProductionGuestAuthenticationTests(ZORGATHIntegrationWebApplicationFactory webApplicationFactory)
{
    [Test]
    public async Task Built_In_Quick_Play_Guest_Cannot_Log_In_To_Production_Portal()
    {
        IActionResult response = await CreateBuiltInGuestUserAndLogIn("GUEST-01");

        await Assert.That(response).IsTypeOf<UnauthorizedObjectResult>();
    }

    [Test]
    public async Task Moderator_Can_Log_In_To_Production_Portal_After_Its_Password_Is_Rotated()
    {
        IActionResult response = await CreateBuiltInGuestUserAndLogIn("MODERATOR");

        await Assert.That(response).IsTypeOf<OkObjectResult>();
    }

    private async Task<IActionResult> CreateBuiltInGuestUserAndLogIn(string accountName)
    {
        await webApplicationFactory.WithEnvironment("Production").WithSQLServerContainer().InitialiseAsync();

        const string password = "Rotated-Moderator-Password-2026!";

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        Role role = await databaseContext.Roles.SingleAsync(candidate => candidate.Name.Equals(UserRoles.User));

        User user = new ()
        {
            EmailAddress = OOTB.Accounts.GUEST.EmailAddress,
            Role = role,
            SRPPasswordSalt = new string('a', 64),
            SRPPasswordHash = new string('b', 64)
        };

        user.PBKDF2PasswordHash = new PasswordHasher<User>().HashPassword(user, password);

        Account account = new ()
        {
            Name = accountName,
            User = user,
            Type = accountName.Equals("MODERATOR") ? AccountType.MatchModerator : AccountType.Guest,
            IsMain = accountName.Equals("MODERATOR")
        };

        user.Accounts.Add(account);

        await databaseContext.Users.AddAsync(user);
        await databaseContext.SaveChangesAsync();

        ILogger<UserController> logger = scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IOptions<OperationalConfiguration> configuration = scope.ServiceProvider.GetRequiredService<IOptions<OperationalConfiguration>>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        UserController controller = new (databaseContext, logger, emailService, configuration, hostEnvironment, scope.ServiceProvider.GetRequiredService<AuthenticationAttemptLimiter>());

        return await controller.LogInUser(new LogInUserDTO(accountName, password));
    }
}
