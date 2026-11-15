namespace KONGOR.MasterServer.Models.ServerManagement;

/// <summary>
///     Identifies the account and session cookie of a logout, so an older session cannot end a newer login.
/// </summary>
public sealed record AccountLogout
{
    public required string AccountName { get; init; }

    public required string SessionCookie { get; init; }
}
