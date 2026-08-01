namespace ASPIRE.Common.Services;

/// <summary>
///     Limits authentication starts independently for each canonical account name so rotating source addresses cannot reset the attempt quota.
/// </summary>
public sealed class AuthenticationAttemptLimiter : IAsyncDisposable
{
    private PartitionedRateLimiter<string> RateLimiter { get; } = PartitionedRateLimiter.Create<string, string>(accountName =>
        RateLimitPartition.GetSlidingWindowLimiter(accountName, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));

    /// <summary>
    ///     Determines whether another authentication attempt may begin for the account, consuming one permit when allowed.
    /// </summary>
    public bool RequestIsAllowed(string accountName)
    {
        string partitionKey = accountName.ToUpperInvariant();

        using RateLimitLease lease = RateLimiter.AttemptAcquire(partitionKey);

        return lease.IsAcquired;
    }

    public async ValueTask DisposeAsync()
    {
        await RateLimiter.DisposeAsync();
    }
}
