namespace DAWNBRINGER.WebPortal.UI.Services;

public sealed class RegisteredAccountCountService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<RegisteredAccountCountService> logger)
{
    private const string Path = "/Accounts/RegisteredCount";

    public Task<int?> GetCount() => cache.GetOrCreateAsync<int?>(Path, async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(4));
        using HttpClient client = httpClientFactory.CreateClient(PortalAuthenticationService.HTTPClientName);

        try
        {
            int count = await client.GetFromJsonAsync<int>(Path, timeout.Token);
            return count >= 0 ? count : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            logger.LogDebug(exception, "Unable To Get The Registered Account Count");
            return null;
        }
    });
}
