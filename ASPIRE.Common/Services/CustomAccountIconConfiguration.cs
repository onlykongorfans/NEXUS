namespace ASPIRE.Common.Services;

public sealed class CustomAccountIconConfiguration
{
    public const string SectionName = "CustomAccountIcons";
    public const string ImageRoute = "/icons/{million:int}/{thousand:int}/{accountID:int}/{slotID:int}.cai";
    public const int MaximumUploadBytes = 2 * 1024 * 1024;
    public const int ImageSize = 128;
    public const int MaximumSourceDimension = 2048;

    public string StorageDirectory { get; set; } = "/var/lib/nexus/account-icons";
    public string PublicBaseURL { get; set; } = "https://portal.kongor.fans";

    public string GetImagePath(int userID, int slotID)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userID);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotID);

        if (Path.IsPathFullyQualified(StorageDirectory) is false)
            throw new InvalidOperationException("Custom Icon Storage Directory Must Be An Absolute Path");

        return Path.Combine(StorageDirectory, userID.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{slotID}.png");
    }

    public string GetImageURL(int accountID, int slotID)
        => $"{PublicBaseURL.TrimEnd('/')}/icons/{accountID / 1000000}/{accountID / 1000 % 1000}/{accountID}/{slotID}.cai";
}
