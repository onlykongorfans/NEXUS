namespace KONGOR.MasterServer.Services;

/// <summary>
///     Coordinates short-lived upload grants and transfers completed replays to the replay store.
/// </summary>
public sealed class ReplayStorageService(HttpClient httpClient, ReplayStorageSettings settings, IDatabase distributedCacheStore, ILogger<ReplayStorageService> logger)
{
    public ReplayStorageSettings Settings { get; } = settings;
    private HttpClient HttpClient { get; } = httpClient;
    private IDatabase DistributedCache { get; } = distributedCacheStore;
    private ILogger Logger { get; } = logger;

    public static string AvailabilityKey(int matchID) => $"REPLAY:AVAILABLE:{matchID}";
    public static string PendingRequestKey(int managerID, int matchID, int accountID) => $"REPLAY:REQUEST:{managerID}:{matchID}:{accountID}";
    public static string WatchersKey(int managerID, int matchID) => $"REPLAY:WATCHERS:{managerID}:{matchID}";
    public static string LocalSizeKey(int matchID) => $"REPLAY:LOCAL-SIZE:{matchID}";
    public static string GrantKey(string token) => $"REPLAY:GRANT:{token}";
    public static string AuthorisationKey(int managerID, int matchID) => $"REPLAY:AUTHORISATION:{managerID}:{matchID}";

    public async Task CreateAuthorisation(int matchID, string hostAccountName, string sourceIPAddress)
    {
        await DistributedCache.StringSetAsync(AuthorisationKey(hostAccountName.GetDeterministicInt32Hash(), matchID),
            JsonSerializer.Serialize(new ReplayAuthorisation(matchID, hostAccountName, sourceIPAddress)), TimeSpan.FromMinutes(5));
    }

    public async Task<ReplayAuthorisation?> GetAuthorisation(int matchID, string hostAccountName, bool consume = false)
    {
        string key = AuthorisationKey(hostAccountName.GetDeterministicInt32Hash(), matchID);
        RedisValue value = consume ? await DistributedCache.StringGetDeleteAsync(key) : await DistributedCache.StringGetAsync(key);
        return value.HasValue ? JsonSerializer.Deserialize<ReplayAuthorisation>(value.ToString()) : null;
    }

    public bool ValidateNativeAuthorisation(int matchID, string extension, string password, string signature)
    {
        if (Settings.NativePasswordSHA256.Length is not 64 || signature.Length is not 40 || signature.Any(character => char.IsAsciiHexDigit(character) is false))
            return false;

        byte[] expectedPassword = Convert.FromHexString(Settings.NativePasswordSHA256);
        byte[] expectedSignature = SHA1.HashData(Encoding.UTF8.GetBytes($"{matchID}{extension}{Settings.NativeHashSuffix}"));
        return CryptographicOperations.FixedTimeEquals(expectedPassword, SHA256.HashData(Encoding.UTF8.GetBytes(password)))
            && CryptographicOperations.FixedTimeEquals(expectedSignature, Convert.FromHexString(signature));
    }

    public async Task<int?> GetAvailableSize(int matchID, CancellationToken cancellationToken = default, bool refresh = false)
    {
        if (Settings.Enabled is false)
            return null;

        RedisValue cached = refresh ? RedisValue.Null : await DistributedCache.StringGetAsync(AvailabilityKey(matchID));

        if (int.TryParse(cached.ToString(), out int cachedSize))
            return cachedSize > 0 ? cachedSize : null;

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using HttpRequestMessage request = new (HttpMethod.Head, Settings.GetUploadURL(matchID));
            using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            long? length = response.Content.Headers.ContentLength;
            int? size = response.IsSuccessStatusCode && length is > 0 and <= ReplayStorageSettings.MaximumReplayBytes ? (int) length.Value : null;

            await DistributedCache.StringSetAsync(AvailabilityKey(matchID), size ?? -1, size.HasValue ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10));
            return size;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            Logger.LogWarning("Replay Availability Probe Failed For Match {MatchID}: {FailureType}", matchID, exception.GetType().Name);
            return null;
        }
    }

    public async Task<string> CreateGrant(ReplayUploadGrant grant)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await DistributedCache.StringSetAsync(GrantKey(token), JsonSerializer.Serialize(grant), TimeSpan.FromMinutes(10));
        return token;
    }

    public async Task<ReplayUploadGrant?> GetGrant(string token)
    {
        if (token.Length is not 64 || token.Any(character => char.IsAsciiHexDigit(character) is false))
            return null;

        RedisValue value = await DistributedCache.StringGetAsync(GrantKey(token));
        return value.HasValue ? JsonSerializer.Deserialize<ReplayUploadGrant>(value.ToString()) : null;
    }

    public async Task<bool> Upload(int matchID, Stream replay, int fileSize, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new (HttpMethod.Put, Settings.GetUploadURL(matchID));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Settings.UserName}:{Settings.Password}")));
        request.Content = new StreamContent(replay);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = fileSize;

        try
        {
            using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode is false)
            {
                Logger.LogWarning("Replay Store Rejected Match {MatchID} With HTTP Status {StatusCode}", matchID, (int) response.StatusCode);
                return false;
            }

            await DistributedCache.StringSetAsync(AvailabilityKey(matchID), fileSize, TimeSpan.FromMinutes(2));
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            Logger.LogWarning("Replay Store Transfer Failed For Match {MatchID}: {FailureType}", matchID, exception.GetType().Name);
            return false;
        }
    }
}

public sealed record ReplayUploadGrant(int MatchID, string HostAccountName, int FileSize, string MD5Checksum);
public sealed record LocalReplaySize(string HostAccountName, int FileSize);
public sealed record ReplayAuthorisation(int MatchID, string HostAccountName, string SourceIPAddress);
