namespace MERRICK.DatabaseContext.Entities.Utility;

public class Token
{
    [Key]
    public int ID { get; set; }

    public required TokenPurpose Purpose { get; set; }

    public required string EmailAddress { get; set; }

    public DateTimeOffset TimestampCreated { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TimestampConsumed { get; set; }

    public required Guid Value { get; set; }

    public required string Data { get; set; }

    /// <summary>
    ///     The duration, measured from <see cref="TimestampCreated"/>, during which this token can be redeemed.
    ///     After this window elapses, the token is considered expired and is purged by the token cleanup service.
    /// </summary>
    public required TimeSpan Validity { get; set; }

    /// <summary>
    ///     Determines whether this token can no longer be redeemed at the supplied point in time.
    /// </summary>
    public bool IsExpiredAt(DateTimeOffset timestamp)
        => TimestampCreated + Validity < timestamp;
}

public enum TokenPurpose
{
    EmailAddressVerification,
    EmailAddressUpdate,
    AccountPasswordRecovery,
    AccountPasswordReset,
    AccountPasswordUpdate,
    HostAccountAuthorisation
}
