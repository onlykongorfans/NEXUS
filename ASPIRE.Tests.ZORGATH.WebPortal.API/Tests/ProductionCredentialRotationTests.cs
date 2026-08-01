using ASPIRE.Common.Constants;

using MERRICK.DatabaseContext.Handlers;

using Microsoft.AspNetCore.Identity;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Verifies that Production startup replaces every publicly-known built-in password and the unreachable administrator email address without changing the configured credentials again on later starts.
/// </summary>
public sealed class ProductionCredentialRotationTests(ZORGATHIntegrationWebApplicationFactory webApplicationFactory)
{
    [Test]
    public async Task Production_Credential_Rotation_Updates_All_Built_In_Users_And_Is_Idempotent()
    {
        await webApplicationFactory.WithEnvironment("Production").WithSQLServerContainer().InitialiseAsync();

        const string administratorEmailAddress = "kongor@kongor.fans";
        const string operatorEmailAddress = "operator@kongor.fans";
        const string moderatorEmailAddress = "moderator@kongor.fans";
        const string administratorPassword = "Administrator-Rotated-2026!";
        const string operatorPassword = "Operator-Rotated-2026!";
        const string moderatorPassword = "Moderator-Rotated-2026!";

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        ILogger<ProductionCredentialRotationTests> logger = scope.ServiceProvider.GetRequiredService<ILogger<ProductionCredentialRotationTests>>();

        await SeedDataHandlers.SeedUsers(databaseContext, CancellationToken.None, logger);
        await SeedDataHandlers.SeedClans(databaseContext, CancellationToken.None, logger);
        await SeedDataHandlers.SeedAccounts(databaseContext, CancellationToken.None, logger, seedBuiltInGuestAccounts: false);
        await SeedDataHandlers.SeedOperator(databaseContext, CancellationToken.None, logger);

        await SeedDataHandlers.SecureBuiltInProductionCredentials(
            databaseContext,
            CancellationToken.None,
            logger,
            administratorEmailAddress,
            administratorPassword,
            operatorEmailAddress,
            operatorPassword,
            moderatorEmailAddress,
            moderatorPassword);

        User administrator = await LoadUserForAccount(databaseContext, "KONGOR");
        User operatorUser = await LoadUserForAccount(databaseContext, OOTB.Accounts.OPERATOR.Name);
        User moderator = await LoadUserForAccount(databaseContext, "MODERATOR");

        await AssertCredentials(administrator, administratorPassword);
        await AssertCredentials(operatorUser, operatorPassword);
        await AssertCredentials(moderator, moderatorPassword);

        using (Assert.Multiple())
        {
            await Assert.That(administrator.EmailAddress).IsEqualTo(administratorEmailAddress);
            await Assert.That(operatorUser.EmailAddress).IsEqualTo(operatorEmailAddress);
            await Assert.That(moderator.EmailAddress).IsEqualTo(moderatorEmailAddress);
            await Assert.That(await databaseContext.Accounts.AnyAsync(account => account.Type == AccountType.Guest)).IsFalse();
            await Assert.That(PasswordMatches(administrator, OOTB.Accounts.GUEST.Password)).IsFalse();
            await Assert.That(PasswordMatches(operatorUser, OOTB.Accounts.OPERATOR.Password)).IsFalse();
            await Assert.That(PasswordMatches(moderator, OOTB.Accounts.GUEST.Password)).IsFalse();
        }

        string[] originalCredentialState =
        [
            administrator.SRPPasswordSalt, administrator.SRPPasswordHash, administrator.PBKDF2PasswordHash,
            operatorUser.SRPPasswordSalt, operatorUser.SRPPasswordHash, operatorUser.PBKDF2PasswordHash,
            moderator.SRPPasswordSalt, moderator.SRPPasswordHash, moderator.PBKDF2PasswordHash
        ];

        await SeedDataHandlers.SecureBuiltInProductionCredentials(
            databaseContext,
            CancellationToken.None,
            logger,
            administratorEmailAddress,
            administratorPassword,
            operatorEmailAddress,
            operatorPassword,
            moderatorEmailAddress,
            moderatorPassword);

        string[] repeatedCredentialState =
        [
            administrator.SRPPasswordSalt, administrator.SRPPasswordHash, administrator.PBKDF2PasswordHash,
            operatorUser.SRPPasswordSalt, operatorUser.SRPPasswordHash, operatorUser.PBKDF2PasswordHash,
            moderator.SRPPasswordSalt, moderator.SRPPasswordHash, moderator.PBKDF2PasswordHash
        ];

        // A Subsequent Startup Must Find The Operator By Its Stable Account Name After Its Email Address Has Been Rotated
        await SeedDataHandlers.SeedOperator(databaseContext, CancellationToken.None, logger);

        using (Assert.Multiple())
        {
            await Assert.That(repeatedCredentialState).IsEquivalentTo(originalCredentialState);
            await Assert.That(await databaseContext.Accounts.CountAsync(account => account.Name.Equals(OOTB.Accounts.OPERATOR.Name))).IsEqualTo(1);
        }

        ILogger<AccountPasswordController> passwordLogger = scope.ServiceProvider.GetRequiredService<ILogger<AccountPasswordController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        AccountPasswordController passwordController = new (databaseContext, passwordLogger, emailService, hostEnvironment);

        IActionResult resetRequestResponse = await passwordController.RequestAccountPasswordReset(new RequestAccountPasswordResetDTO(operatorEmailAddress));

        await Assert.That(resetRequestResponse).IsTypeOf<OkObjectResult>();
        await Assert.That(await databaseContext.Tokens.AnyAsync(token => token.Purpose.Equals(TokenPurpose.AccountPasswordReset) && token.EmailAddress.Equals(operatorEmailAddress))).IsFalse();

        Token supersededResetToken = new ()
        {
            Purpose = TokenPurpose.AccountPasswordReset,
            EmailAddress = operatorEmailAddress,
            Value = Guid.CreateVersion7(),
            Data = operatorEmailAddress,
            Validity = TimeSpan.FromHours(1)
        };

        await databaseContext.Tokens.AddAsync(supersededResetToken);
        await databaseContext.SaveChangesAsync();

        IActionResult resetConfirmationResponse = await passwordController.ConfirmAccountPasswordReset(
            new ConfirmAccountPasswordResetDTO(supersededResetToken.Value.ToString(), "Replacement-Password-2026!", "Replacement-Password-2026!"));

        await Assert.That(resetConfirmationResponse).IsTypeOf<ObjectResult>();
        ObjectResult forbiddenResetResult = (ObjectResult) resetConfirmationResponse;

        using (Assert.Multiple())
        {
            await Assert.That(forbiddenResetResult.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
            await Assert.That(await databaseContext.Tokens.AnyAsync(token => token.ID.Equals(supersededResetToken.ID))).IsFalse();
            await AssertCredentials(operatorUser, operatorPassword);
        }

        ClaimsPrincipal principal = new (new ClaimsIdentity([ new Claim(Claims.Email, operatorEmailAddress) ], "Test"));
        passwordController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

        IActionResult updateRequestResponse = await passwordController.RequestAccountPasswordUpdate(
            new RequestAccountPasswordUpdateDTO(operatorPassword, "Replacement-Password-2026!", "Replacement-Password-2026!"));

        await Assert.That(updateRequestResponse).IsTypeOf<ObjectResult>();
        ObjectResult forbiddenUpdateResult = (ObjectResult) updateRequestResponse;

        using (Assert.Multiple())
        {
            await Assert.That(forbiddenUpdateResult.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
            await Assert.That(await databaseContext.Tokens.AnyAsync(token => token.Purpose.Equals(TokenPurpose.AccountPasswordUpdate) && token.EmailAddress.Equals(operatorEmailAddress))).IsFalse();
            await AssertCredentials(operatorUser, operatorPassword);
        }
    }

    private static async Task<User> LoadUserForAccount(MerrickContext databaseContext, string accountName)
        => (await databaseContext.Accounts.Include(account => account.User)
            .SingleAsync(account => account.Name.Equals(accountName))).User;

    private static async Task AssertCredentials(User user, string password)
    {
        using (Assert.Multiple())
        {
            await Assert.That(user.SRPPasswordSalt.Length).IsEqualTo(64);
            await Assert.That(user.SRPPasswordHash).IsEqualTo(SRPPasswordHandlers.ComputeSRPPasswordHash(password, user.SRPPasswordSalt));
            await Assert.That(new PasswordHasher<User>().VerifyHashedPassword(user, user.PBKDF2PasswordHash, password)).IsEqualTo(PasswordVerificationResult.Success);
        }
    }

    private static bool PasswordMatches(User user, string password)
        => user.SRPPasswordHash.Equals(SRPPasswordHandlers.ComputeSRPPasswordHash(password, user.SRPPasswordSalt), StringComparison.Ordinal)
            && new PasswordHasher<User>().VerifyHashedPassword(user, user.PBKDF2PasswordHash, password) is PasswordVerificationResult.Success;
}
