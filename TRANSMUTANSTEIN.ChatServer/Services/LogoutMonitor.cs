namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
///     Subscribes to <see cref="DistributedCacheExtensions.AccountLogoutChannel"/> on the distributed cache and force-terminates the matching chat session whenever the master server publishes an account logout notification.
///     This guarantees that a logged-out account is no longer visible to peers in chat channels or friend/clan lists, even in scenarios where the client did not close its chat socket cleanly.
/// </summary>
public class LogoutMonitor(IConnectionMultiplexer distributedCacheProvider, ILogger<LogoutMonitor> logger) : IHostedService
{
    private ChannelMessageQueue? LogoutQueue { get; set; }

    private ChannelMessageQueue? SessionLogoutQueue { get; set; }

    private static readonly RedisChannel AccountLogoutChannel = RedisChannel.Literal(DistributedCacheExtensions.AccountLogoutChannel);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ISubscriber subscriber = distributedCacheProvider.GetSubscriber();

        LogoutQueue = await subscriber.SubscribeAsync(AccountLogoutChannel);

        LogoutQueue.OnMessage(HandleAccountLogout);

        SessionLogoutQueue = await subscriber.SubscribeAsync(RedisChannel.Literal(DistributedCacheExtensions.AccountSessionLogoutChannel));

        SessionLogoutQueue.OnMessage(HandleSessionLogout);

        logger.LogInformation(@"Subscribed To Account Logout Event Channel ""{Channel}""", DistributedCacheExtensions.AccountLogoutChannel);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (LogoutQueue is not null)
            await LogoutQueue.UnsubscribeAsync();

        if (SessionLogoutQueue is not null)
            await SessionLogoutQueue.UnsubscribeAsync();

        logger.LogInformation(@"Unsubscribed From Account Logout Event Channel ""{Channel}""", DistributedCacheExtensions.AccountLogoutChannel);
    }

    private async Task HandleAccountLogout(ChannelMessage channelMessage)
    {
        string? accountName = channelMessage.Message.IsNullOrEmpty ? null : channelMessage.Message.ToString();

        if (accountName is null)
        {
            logger.LogWarning(@"Received Empty Or Null Payload On Account Logout Event Channel ""{Channel}""", DistributedCacheExtensions.AccountLogoutChannel);

            return;
        }

        await EndSession(accountName, null);
    }

    private async Task HandleSessionLogout(ChannelMessage channelMessage)
    {
        AccountLogout? logout;

        try
        {
            logout = channelMessage.Message.IsNullOrEmpty ? null : JsonSerializer.Deserialize<AccountLogout>(channelMessage.Message.ToString());
        }

        catch (JsonException)
        {
            logger.LogWarning("Received Malformed Session Logout Notification");

            return;
        }

        if (logout is null || string.IsNullOrWhiteSpace(logout.AccountName) || string.IsNullOrWhiteSpace(logout.SessionCookie))
        {
            logger.LogWarning("Received Incomplete Session Logout Notification");

            return;
        }

        await EndSession(logout.AccountName, logout.SessionCookie);
    }

    private async Task EndSession(string accountName, string? sessionCookie)
    {
        if (Context.ClientChatSessions.TryGetValue(accountName, out ClientChatSession? session) is false)
        {
            // No Active Session For This Account; The Client Likely Already Closed Its Chat Socket
            logger.LogDebug(@"No Active Chat Session Found For Logged-Out Account ""{AccountName}""", accountName);

            return;
        }

        if (sessionCookie is not null && string.Equals(session.Metadata.SessionCookie, sessionCookie, StringComparison.Ordinal) is false)
            return;

        logger.LogInformation(@"Force-Terminating Chat Session For Logged-Out Account ""{AccountName}""", accountName);

        try
        {
            await session.Terminate();
        }

        catch (Exception exception)
        {
            logger.LogError(exception, @"[BUG] Failed To Force-Terminate Chat Session For Logged-Out Account ""{AccountName}""", accountName);
        }
    }
}
