namespace KONGOR.MasterServer.Services;

/// <summary>
///     Configures the native replay receiver and the separately authenticated replay store.
/// </summary>
public sealed class ReplayStorageSettings
{
    public const long MaximumReplayBytes = 256L * 1024 * 1024;

    public string UploadBaseURL { get; init; } = string.Empty;
    public string DownloadBaseURL { get; init; } = string.Empty;
    public string ReceiverURL { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string NativePasswordSHA256 { get; init; } = string.Empty;
    public string NativeHashSuffix { get; init; } = string.Empty;

    public bool Enabled => UploadBaseURL.Length > 0;

    public string GetDownloadURL(int matchID) => GetFileURL(DownloadBaseURL, matchID);
    public string GetUploadURL(int matchID) => GetFileURL(UploadBaseURL, matchID);
    public string ReceiverTarget => ReceiverURL["http://".Length..];

    private static string GetFileURL(string baseURL, int matchID)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchID);
        return $"{baseURL.TrimEnd('/')}/M{matchID}.honreplay";
    }

    public static ReplayStorageSettings Load(string? credentialsFile)
    {
        if (string.IsNullOrWhiteSpace(credentialsFile) || File.Exists(credentialsFile) is false)
            return new ();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(credentialsFile));
        JsonElement root = document.RootElement;

        string Read(string name) => root.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;

        ReplayStorageSettings settings = new ()
        {
            UploadBaseURL = Read("url"),
            DownloadBaseURL = Read("download_url"),
            ReceiverURL = Read("receiver_url"),
            UserName = Read("username"),
            Password = Read("password"),
            NativePasswordSHA256 = Read("native_password_sha256"),
            NativeHashSuffix = Read("native_hash_suffix")
        };

        if (Uri.TryCreate(settings.UploadBaseURL, UriKind.Absolute, out Uri? uploadURL) is false || uploadURL.Scheme is not "https"
            || Uri.TryCreate(settings.DownloadBaseURL, UriKind.Absolute, out Uri? downloadURL) is false || downloadURL.Scheme is not ("http" or "https")
            || Uri.TryCreate(settings.ReceiverURL, UriKind.Absolute, out Uri? receiverURL) is false || receiverURL.Scheme is not "http"
            || settings.UserName.Length is 0 || settings.Password.Length is 0
            || settings.NativePasswordSHA256.Length is not 64 || settings.NativePasswordSHA256.Any(character => char.IsAsciiHexDigit(character) is false)
            || settings.NativeHashSuffix.Length is not 64
            || new[] { uploadURL, downloadURL, receiverURL }.Any(address => address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0))
            throw new InvalidOperationException("Replay Storage Requires Valid Upload, Download And Receiver URLs And Storage Credentials");

        return settings;
    }
}
