namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Tests for email address registration.
/// </summary>
public sealed class EmailAddressRegistrationTests(ZORGATHIntegrationWebApplicationFactory webApplicationFactory)
{
    [Before(HookType.Test)]
    public Task Before_Each_Test()
        => webApplicationFactory.WithSQLServerContainer().InitialiseAsync();

    [Test]
    [Arguments("test@kongor.com")]
    [Arguments("user@kongor.net")]
    public async Task Register_Email_Address_With_Valid_Email_Address_Returns_OK_And_Creates_Token_And_Delivers_Email(string emailAddress)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        await Assert.That(response).IsTypeOf<OkObjectResult>();

        Token? token = await databaseContext.Tokens.SingleOrDefaultAsync(candidate =>
            candidate.EmailAddress.Equals(emailAddress) && candidate.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        await Assert.That(token).IsNotNull();

        using (Assert.Multiple())
        {
            await Assert.That(token.EmailAddress).IsEqualTo(emailAddress);
            await Assert.That(token.Purpose).IsEqualTo(TokenPurpose.EmailAddressVerification);
            await Assert.That(token.TimestampConsumed).IsNull();
        }

        // Verify The Controller Actually Dispatched A Registration-Link Email, With The Token Available To The Recipient So They Can Complete Verification
        IReadOnlyList<RecordedEmail> delivered = webApplicationFactory.GetInMemoryEmailService().GetRecordedFor(emailAddress);

        await Assert.That(delivered.Count).IsEqualTo(1);

        RecordedEmail recorded = delivered.Single();

        using (Assert.Multiple())
        {
            await Assert.That(recorded.Kind).IsEqualTo(EmailKind.EmailAddressRegistrationLink);
            await Assert.That(recorded.Recipient).IsEqualTo(emailAddress);
            await Assert.That(recorded.Parameters["Token"]).IsEqualTo(token.Value.ToString());
        }
    }

    [Test]
    [Arguments("test@kongor.com", "different@kongor.com")]
    [Arguments("user@kongor.net", "typo@kongor.net")]
    public async Task Register_Email_Address_With_Mismatched_Confirmation_Returns_Bad_Request(string emailAddress, string confirmEmailAddress)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, confirmEmailAddress));

        await Assert.That(response).IsTypeOf<BadRequestObjectResult>();
    }

    [Test]
    [Arguments("duplicate@kongor.com")]
    [Arguments("existing@kongor.net")]
    public async Task Register_Email_Address_When_Already_Registered_Returns_Bad_Request(string emailAddress)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        await Assert.That(response).IsTypeOf<BadRequestObjectResult>();
    }

    [Test]
    [Arguments("tester+public-beta@subdomain.example.technology")]
    [Arguments("player@independent-domain.games")]
    public async Task Register_Email_Address_Accepts_Aliases_Custom_Providers_And_Long_TLDs(string emailAddress)
    {
        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        await Assert.That(response).IsTypeOf<OkObjectResult>();
        await Assert.That(await databaseContext.Tokens.AnyAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification))).IsTrue();
    }

    [Test]
    public async Task Register_Email_Address_Preserves_Local_Part_Case_And_Normalises_Domain_Case()
    {
        const string submittedEmailAddress = "CaseSensitiveMailbox@EXAMPLE.TECHNOLOGY";
        const string expectedEmailAddress = "CaseSensitiveMailbox@example.technology";

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(submittedEmailAddress, submittedEmailAddress));

        await Assert.That(response).IsTypeOf<OkObjectResult>();

        Token token = await databaseContext.Tokens.SingleAsync(candidate =>
            candidate.EmailAddress.Equals(expectedEmailAddress)
            && candidate.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        RecordedEmail email = webApplicationFactory.GetInMemoryEmailService().GetRecordedFor(expectedEmailAddress).Single();

        using (Assert.Multiple())
        {
            await Assert.That(token.EmailAddress).IsEqualTo(expectedEmailAddress);
            await Assert.That(token.Data).IsEqualTo(expectedEmailAddress);
            await Assert.That(email.Recipient).IsEqualTo(expectedEmailAddress);
        }
    }

    [Test]
    public async Task Register_Email_Address_Replaces_Expired_Unconsumed_Token()
    {
        const string emailAddress = "expired-registration@example.technology";

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        ILogger<EmailAddressController> logger = scope.ServiceProvider.GetRequiredService<ILogger<EmailAddressController>>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IWebHostEnvironment hostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        MerrickContext databaseContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        EmailAddressController controller = new (databaseContext, logger, emailService, hostEnvironment);

        await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        Token originalToken = await databaseContext.Tokens.SingleAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        Guid originalValue = originalToken.Value;
        originalToken.TimestampCreated = DateTimeOffset.UtcNow.Subtract(originalToken.Validity).AddMinutes(-1);
        await databaseContext.SaveChangesAsync();

        IActionResult response = await controller.RegisterEmailAddress(new RegisterEmailAddressDTO(emailAddress, emailAddress));

        await Assert.That(response).IsTypeOf<OkObjectResult>();

        Token replacementToken = await databaseContext.Tokens.SingleAsync(token =>
            token.EmailAddress.Equals(emailAddress) && token.Purpose.Equals(TokenPurpose.EmailAddressVerification));

        using (Assert.Multiple())
        {
            await Assert.That(replacementToken.Value).IsNotEqualTo(originalValue);
            await Assert.That(replacementToken.TimestampConsumed).IsNull();
            await Assert.That(replacementToken.IsExpiredAt(DateTimeOffset.UtcNow)).IsFalse();
        }
    }
}
