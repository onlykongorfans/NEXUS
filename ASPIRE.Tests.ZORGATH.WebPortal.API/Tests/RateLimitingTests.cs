namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Tests;

public sealed class RateLimitingTests
{
    [Test]
    public async Task IPv6_Addresses_In_The_Same_64_Bit_Network_Share_A_Partition()
    {
        string firstPartition = IPAddress.Parse("2001:db8:1234:5678::1").ToRateLimitPartitionKey();
        string secondPartition = IPAddress.Parse("2001:db8:1234:5678:ffff:ffff:ffff:ffff").ToRateLimitPartitionKey();
        string differentPartition = IPAddress.Parse("2001:db8:1234:5679::1").ToRateLimitPartitionKey();

        using (Assert.Multiple())
        {
            await Assert.That(secondPartition).IsEqualTo(firstPartition);
            await Assert.That(differentPartition).IsNotEqualTo(firstPartition);
        }
    }

    [Test]
    public async Task IPv4_And_IPv4_Mapped_Addresses_Share_A_Partition()
    {
        string ipv4Partition = IPAddress.Parse("192.0.2.10").ToRateLimitPartitionKey();
        string mappedPartition = IPAddress.Parse("::ffff:192.0.2.10").ToRateLimitPartitionKey();

        await Assert.That(mappedPartition).IsEqualTo(ipv4Partition);
    }

    [Test]
    public async Task Account_Authentication_Limit_Is_Case_Insensitive_And_Independent_Of_Client_Address()
    {
        await using AuthenticationAttemptLimiter limiter = new ();

        for (int attempt = 0; attempt < 5; attempt++)
            await Assert.That(limiter.RequestIsAllowed("ExampleAccount")).IsTrue();

        await Assert.That(limiter.RequestIsAllowed("exampleaccount")).IsFalse();
        await Assert.That(limiter.RequestIsAllowed("DifferentAccount")).IsTrue();
    }
}
