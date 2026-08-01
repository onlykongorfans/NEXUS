namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests for secure, token-based forgotten-password resets.
/// </summary>
public sealed class AccountPasswordResetTests(ZORGATHIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().InitialiseAsync();

    [Test]
    public async Task Password_Reset_Link_Lets_User_Choose_New_Password_Without_Emailing_A_Password()
    {
        const string emailAddress = "password-reset@example.technology";
        const string accountName = "ResetPlayer";
        const string originalPassword = "OriginalPassword123!!";
        const string replacementPassword = "ReplacementPassword456!!";

        JWTAuthenticationService authenticationService = new (webApplicationFactory);
        await authenticationService.CreateAuthenticatedUser(emailAddress, accountName, originalPassword);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        ILogger<AccountPasswordController> passwordLogger = scope.ServiceProvider.GetRequiredService<ILogger<AccountPasswordController>>();
        ILogger<UserController> userLogger = scope.ServiceProvider.GetRequiredService<ILogger<UserController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        IOptions<OperationalConfiguration> configuration = scope.ServiceProvider.GetRequiredService<IOptions<OperationalConfiguration>>();

        AccountPasswordController passwordController = new (databaseContext, passwordLogger, emailService, hostEnvironment);

        IActionResult requestResponse = await passwordController.RequestAccountPasswordReset(new RequestAccountPasswordResetDTO(emailAddress));
        await Assert.That(requestResponse).IsTypeOf<OkObjectResult>();

        RecordedEmail resetEmail = webApplicationFactory.GetInMemoryEmailService().GetRecordedFor(emailAddress)
            .Single(email => email.Kind.Equals(EmailKind.AccountPasswordResetLink));

        using (Assert.Multiple())
        {
            await Assert.That(resetEmail.Parameters.ContainsKey("Token")).IsTrue();
            await Assert.That(resetEmail.Parameters.ContainsKey("GeneratedPassword")).IsFalse();
        }

        IActionResult confirmResponse = await passwordController.ConfirmAccountPasswordReset(
            new ConfirmAccountPasswordResetDTO(resetEmail.Parameters["Token"], replacementPassword, replacementPassword));

        await Assert.That(confirmResponse).IsTypeOf<OkObjectResult>();

        UserController userController = new (databaseContext, userLogger, emailService, configuration, hostEnvironment, scope.ServiceProvider.GetRequiredService<AuthenticationAttemptLimiter>());

        await Assert.That(await userController.LogInUser(new LogInUserDTO(accountName, originalPassword))).IsTypeOf<UnauthorizedObjectResult>();
        await Assert.That(await userController.LogInUser(new LogInUserDTO(accountName, replacementPassword))).IsTypeOf<OkObjectResult>();
    }

    [Test]
    public async Task Expired_Password_Reset_Token_Is_Rejected_And_Removed()
    {
        const string emailAddress = "expired-password-reset@example.technology";
        const string accountName = "ExpiredReset";
        const string originalPassword = "OriginalPassword123!!";
        const string replacementPassword = "ReplacementPassword456!!";

        JWTAuthenticationService authenticationService = new (webApplicationFactory);
        await authenticationService.CreateAuthenticatedUser(emailAddress, accountName, originalPassword);

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        ILogger<AccountPasswordController> logger = scope.ServiceProvider.GetRequiredService<ILogger<AccountPasswordController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        AccountPasswordController controller = new (databaseContext, logger, emailService, hostEnvironment);
        await controller.RequestAccountPasswordReset(new RequestAccountPasswordResetDTO(emailAddress));

        Token token = await databaseContext.Tokens.SingleAsync(candidate =>
            candidate.EmailAddress.Equals(emailAddress)
            && candidate.Purpose.Equals(TokenPurpose.AccountPasswordReset)
            && candidate.TimestampConsumed == null);

        token.TimestampCreated = DateTimeOffset.UtcNow.Subtract(token.Validity).AddMinutes(-1);
        await databaseContext.SaveChangesAsync();

        IActionResult response = await controller.ConfirmAccountPasswordReset(
            new ConfirmAccountPasswordResetDTO(token.Value.ToString(), replacementPassword, replacementPassword));

        await Assert.That(response).IsTypeOf<BadRequestObjectResult>();
        await Assert.That(await databaseContext.Tokens.AnyAsync(candidate => candidate.ID.Equals(token.ID))).IsFalse();
    }
}
