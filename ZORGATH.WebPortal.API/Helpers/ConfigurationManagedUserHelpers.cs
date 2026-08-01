namespace ZORGATH.WebPortal.API.Helpers;

public static class ConfigurationManagedUserHelpers
{
    /// <summary>
    ///     Determines whether the user's Production email address and passwords are managed by protected deployment configuration.
    /// </summary>
    public static bool IsConfigurationManaged(User user)
        => user.Accounts.Any(account =>
            account.Name.Equals("KONGOR", StringComparison.OrdinalIgnoreCase)
            || account.Name.Equals(OOTB.Accounts.OPERATOR.Name, StringComparison.OrdinalIgnoreCase)
            || account.Name.Equals("MODERATOR", StringComparison.OrdinalIgnoreCase));
}
