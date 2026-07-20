namespace KONGOR.MasterServer.Models.ServerManagement;

public class MatchServer
{
    public required int HostAccountID { get; set; }

    public required string HostAccountName { get; set; }

    public required int ID { get; set; }

    public required string Name { get; set; }

    public required int? MatchServerManagerID { get; set; }

    public required int Instance { get; set; }

    public required string IPAddress { get; set; }

    public required int Port { get; set; }

    public required string Location { get; set; }

    public required string Description { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.SERVER_STATUS_UNKNOWN;

    /// <summary>
    ///     Indicates that the previous chat session retired while this registration is being retained for a replacement handshake.
    ///     Retired servers remain authenticated by their existing cookie but are excluded from server selection until a live chat status update reactivates them.
    /// </summary>
    public bool IsRetired { get; set; } = false;

    public string Cookie { get; set; } = Guid.CreateVersion7().ToString();

    public DateTimeOffset TimestampRegistered { get; set; } = DateTimeOffset.UtcNow;
}
