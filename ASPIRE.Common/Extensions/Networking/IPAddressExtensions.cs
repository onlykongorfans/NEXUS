namespace ASPIRE.Common.Extensions.Networking;

public static class IPAddressExtensions
{
    /// <summary>
    ///     Returns a stable rate-limit partition key for an IPv4 address or the enclosing IPv6 /64 network.
    /// </summary>
    public static string ToRateLimitPartitionKey(this IPAddress? ipAddress)
    {
        if (ipAddress is null)
            return "UNKNOWN";

        if (ipAddress.AddressFamily is AddressFamily.InterNetwork || ipAddress.IsIPv4MappedToIPv6)
            return $"IPv4:{ipAddress.MapToIPv4()}";

        if (ipAddress.AddressFamily is not AddressFamily.InterNetworkV6)
            return $"UNKNOWN:{ipAddress}";

        byte[] networkBytes = ipAddress.GetAddressBytes();
        Array.Clear(networkBytes, 8, 8);

        IPAddress networkAddress = new (networkBytes, ipAddress.ScopeId);

        return $"IPv6:{networkAddress}/64";
    }
}
