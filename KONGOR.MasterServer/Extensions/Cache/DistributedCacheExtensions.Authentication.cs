namespace KONGOR.MasterServer.Extensions.Cache;

public static partial class DistributedCacheExtensions
{
    private static string ConstructSRPAuthenticationSessionDataKey(string accountName) => $@"SRP-SESSION-DATA:[""{accountName}""]";

    public static async Task SetSRPAuthenticationSessionData(this IDatabase distributedCacheStore, string accountName, SRPAuthenticationSessionDataStageOne data)
    {
        string serializedData = JsonSerializer.Serialize(data);

        await distributedCacheStore.StringSetAsync(ConstructSRPAuthenticationSessionDataKey(accountName), serializedData, TimeSpan.FromSeconds(30));
    }

    public static async Task<SRPAuthenticationSessionDataStageOne?> GetSRPAuthenticationSessionData(this IDatabase distributedCacheStore, string accountName)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructSRPAuthenticationSessionDataKey(accountName));

        return cachedValue.IsNullOrEmpty ? null : JsonSerializer.Deserialize<SRPAuthenticationSessionDataStageOne>(cachedValue.ToString());
    }

    public static async Task RemoveSRPAuthenticationSessionData(this IDatabase distributedCacheStore, string accountName)
        => await distributedCacheStore.KeyDeleteAsync(ConstructSRPAuthenticationSessionDataKey(accountName));

    private static string ConstructSRPAuthenticationSystemInformationKey(string accountName) => $@"SYSTEM-INFORMATION:[""{accountName}""]";

    public static async Task SetSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore, string accountName, string systemInformation)
        => await distributedCacheStore.StringSetAsync(ConstructSRPAuthenticationSystemInformationKey(accountName), systemInformation, TimeSpan.FromSeconds(30));

    public static async Task<string?> GetSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore, string accountName)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructSRPAuthenticationSystemInformationKey(accountName));

        return cachedValue.IsNullOrEmpty ? null : cachedValue.ToString();
    }

    public static async Task RemoveSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore, string accountName)
        => await distributedCacheStore.KeyDeleteAsync(ConstructSRPAuthenticationSystemInformationKey(accountName));

    // The Cookie Is Stored In The Key Because Most Requests From HoN Are Made With Just A Cookie But No Account Information
    // Additionally, Cached Values Cannot Be Retrieved Without Their Respective Key, So Iterating Just The Values Is Not Possible Without Complex Reflection
    private static string ConstructAccountSessionCookieKey(string cookie) => $@"ACCOUNT-SESSION-COOKIE:[""{cookie}""]";

    private static string ConstructAccountSessionCookieIndexKey(string accountName) => $@"ACCOUNT-SESSION-COOKIES:[""{accountName}""]";

    public static async Task SetAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie, string accountName)
    {
        RedisKey cookieKey = ConstructAccountSessionCookieKey(cookie);
        RedisKey accountCookieIndexKey = ConstructAccountSessionCookieIndexKey(accountName);
        RedisValue previousAccountName = await distributedCacheStore.StringGetAsync(cookieKey);
        TimeSpan sessionLifetime = TimeSpan.FromHours(24);

        ITransaction transaction = distributedCacheStore.CreateTransaction();

        _ = transaction.StringSetAsync(cookieKey, accountName, sessionLifetime);
        _ = transaction.SetAddAsync(accountCookieIndexKey, cookie);
        _ = transaction.KeyExpireAsync(accountCookieIndexKey, sessionLifetime);

        if (previousAccountName.IsNullOrEmpty is false && previousAccountName.Equals(accountName).Equals(false))
            _ = transaction.SetRemoveAsync(ConstructAccountSessionCookieIndexKey(previousAccountName.ToString()), cookie);

        await transaction.ExecuteAsync();
    }

    public static async Task<string?> GetAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructAccountSessionCookieKey(cookie));

        return cachedValue.IsNullOrEmpty ? null : cachedValue.ToString();
    }

    public static async Task RemoveAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie)
    {
        RedisKey cookieKey = ConstructAccountSessionCookieKey(cookie);
        RedisValue accountName = await distributedCacheStore.StringGetAsync(cookieKey);

        ITransaction transaction = distributedCacheStore.CreateTransaction();
        _ = transaction.KeyDeleteAsync(cookieKey);

        if (accountName.IsNullOrEmpty is false)
            _ = transaction.SetRemoveAsync(ConstructAccountSessionCookieIndexKey(accountName.ToString()), cookie);

        await transaction.ExecuteAsync();
    }

    /// <summary>
    ///     Remaps every still-valid session cookie for a renamed account while preserving each cookie's remaining lifetime.
    /// </summary>
    public static async Task RenameAccountSessionCookies(this IDatabase distributedCacheStore, string previousAccountName, string newAccountName)
    {
        const string renameScript = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                redis.call('SET', KEYS[1], ARGV[2], 'KEEPTTL')
                return 1
            end

            return 0
            """;

        RedisKey previousIndexKey = ConstructAccountSessionCookieIndexKey(previousAccountName);
        RedisKey newIndexKey = ConstructAccountSessionCookieIndexKey(newAccountName);
        RedisValue[] cookies = await distributedCacheStore.SetMembersAsync(previousIndexKey);

        foreach (RedisValue cookie in cookies)
        {
            RedisKey cookieKey = ConstructAccountSessionCookieKey(cookie.ToString());
            RedisResult result = await distributedCacheStore.ScriptEvaluateAsync(renameScript, [cookieKey], [previousAccountName, newAccountName]);

            if ((long) result == 1)
                await distributedCacheStore.SetAddAsync(newIndexKey, cookie);
        }

        if (cookies.Length > 0)
            await distributedCacheStore.KeyExpireAsync(newIndexKey, TimeSpan.FromHours(24));

        await distributedCacheStore.KeyDeleteAsync(previousIndexKey);
    }

    public static async Task<(bool IsValid, string? AccountName)> ValidateAccountSessionCookie(this IDatabase distributedCacheStore, string cookie)
    {
        string? accountName = await distributedCacheStore.GetAccountNameForSessionCookie(cookie);

        return (accountName is not null, accountName);
    }

    /// <summary>
    ///     Purges all session cookies from the cache.
    ///     This should be called at application startup to clear stale session data.
    /// </summary>
    public static async Task PurgeSessionCookies(this IConnectionMultiplexer distributedCacheProvider)
    {
        EndPoint endpoint = distributedCacheProvider.GetEndPoints().Single();
        IServer server = distributedCacheProvider.GetServer(endpoint);
        IDatabase distributedCacheStore = distributedCacheProvider.GetDatabase();

        await foreach (RedisKey key in server.KeysAsync(pattern: "ACCOUNT-SESSION-COOKIE:*"))
        {
            await distributedCacheStore.KeyDeleteAsync(key);
        }

        await foreach (RedisKey key in server.KeysAsync(pattern: "ACCOUNT-SESSION-COOKIES:*"))
        {
            await distributedCacheStore.KeyDeleteAsync(key);
        }
    }

    /// <summary>
    ///     The distributed cache pub/sub channel on which account logout notifications are published by the master server and consumed by the chat server.
    ///     Used to force-terminate any active TCP chat session for the logged-out account, so that peers no longer see them as connected even if the client's chat socket has not closed.
    /// </summary>
    public const string AccountLogoutChannel = "ACCOUNT-LOGOUT";

    /// <summary>
    ///     Publishes an account logout notification on <see cref="AccountLogoutChannel"/>.
    ///     The payload is the canonical account name as stored against the session cookie.
    /// </summary>
    public static async Task PublishAccountLogout(this IDatabase distributedCacheStore, string accountName)
        => await distributedCacheStore.Multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(AccountLogoutChannel), accountName);
}
