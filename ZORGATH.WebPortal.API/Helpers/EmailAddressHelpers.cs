namespace ZORGATH.WebPortal.API.Helpers;

public static class EmailAddressHelpers
{
    /// <summary>
    ///     This method validates and sanitises the supplied email address.
    ///     It returns <see langword="null"/> when sanitisation succeeds, exposing the result via <paramref name="sanitisedEmailAddress"/>, or an <see cref="IActionResult"/> describing the failure otherwise.
    /// </summary>
    public static IActionResult? TrySanitiseEmailAddress(string emailAddress, IWebHostEnvironment hostEnvironment, ILogger logger, out string sanitisedEmailAddress)
    {
        sanitisedEmailAddress = string.Empty;

        IActionResult result = SanitiseEmailAddress(emailAddress, hostEnvironment);

        if (result is not ContentResult contentResult)
            return result;

        if (contentResult.Content is null)
        {
            logger.LogError(@"[BUG] Sanitised Email Address ""{SubmittedEmailAddress}"" Is NULL", emailAddress);

            return new UnprocessableEntityObjectResult($@"Unable To Process Email Address ""{emailAddress}""");
        }

        sanitisedEmailAddress = contentResult.Content;

        return null;
    }

    private static IActionResult SanitiseEmailAddress(string email, IWebHostEnvironment hostEnvironment)
    {
        string candidate = email.Trim();

        // Require A Bare Mailbox Address; Display Names And Other RFC Mailbox List Syntax Are Not Valid Account Identifiers
        if (MailboxAddress.TryParse(candidate, out MailboxAddress? mailboxAddress).Equals(false)
            || mailboxAddress is null
            || mailboxAddress.Address.Equals(candidate, StringComparison.OrdinalIgnoreCase).Equals(false))
            return new BadRequestObjectResult($@"Email Address ""{email}"" Is Not Valid");

        string parsedAddress = mailboxAddress.Address;
        int domainSeparatorIndex = parsedAddress.LastIndexOf('@');

        if (domainSeparatorIndex <= 0 || domainSeparatorIndex == parsedAddress.Length - 1)
            return new BadRequestObjectResult($@"Email Address ""{email}"" Is Not Valid");

        string localPart = parsedAddress[..domainSeparatorIndex];
        string domain = parsedAddress[(domainSeparatorIndex + 1)..].ToLowerInvariant();

        return new ContentResult
        {
            // Domain Names Are Case-Insensitive, But Some Providers Treat Mailbox Local Parts As Case-Sensitive
            Content = $"{localPart}@{domain}"
        };
    }
}
