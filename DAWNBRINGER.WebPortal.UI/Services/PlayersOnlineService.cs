namespace DAWNBRINGER.WebPortal.UI.Services;

public sealed class PlayersOnlineService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<PlayersOnlineService> logger) : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim HeadlineGate = new(1);
    private readonly SemaphoreSlim DetailsGate = new(1);

    public Task<PlayersOnlineHeadline?> GetHeadline(CancellationToken cancellationToken)
        => GetSnapshot<PlayersOnlineHeadline>("/PlayersOnline", HeadlineGate, cancellationToken);

    public Task<PlayersOnlineDetails?> GetDetails(CancellationToken cancellationToken)
        => GetSnapshot<PlayersOnlineDetails>("/PlayersOnline/Details", DetailsGate, cancellationToken);

    private async Task<TSnapshot?> GetSnapshot<TSnapshot>(string path, SemaphoreSlim gate, CancellationToken cancellationToken) where TSnapshot : class
    {
        if (cache.TryGetValue(path, out TSnapshot? snapshot))
            return snapshot;

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (cache.TryGetValue(path, out snapshot))
                return snapshot;

            snapshot = await RequestSnapshot<TSnapshot>(path, cancellationToken);
            cache.Set(path, snapshot, CacheDuration);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TSnapshot?> RequestSnapshot<TSnapshot>(string path, CancellationToken cancellationToken) where TSnapshot : class
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(PortalAuthenticationService.HTTPClientName);
            using HttpResponseMessage response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync<TSnapshot>(timeout.Token)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException or IOException)
        {
            logger.LogDebug(exception, "Unable To Refresh Players Online At {Path}", path);
            return null;
        }
    }

    public void Dispose()
    {
        HeadlineGate.Dispose();
        DetailsGate.Dispose();
    }
}
