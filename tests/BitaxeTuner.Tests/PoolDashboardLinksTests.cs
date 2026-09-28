using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

public sealed class PoolDashboardLinksTests
{
    private const string ExampleAddress = "bc1q00000000000000000000000000000000000000";

    [Theory]
    [InlineData("stratum.btcpowlab-pool.com")]
    [InlineData("STRATUM.BTCPOWLAB-POOL.COM:3333")]
    [InlineData("stratum+tcp://stratum.btcpowlab-pool.com:3333")]
    public void BuildsBtcPowLabMinerUrlForExactHost(string host)
    {
        var result = PoolDashboardLinks.ForMiner(host, ExampleAddress);

        Assert.Equal($"https://btcpowlab-pool.com/miner/{ExampleAddress}", result?.AbsoluteUri);
    }

    [Theory]
    [InlineData("other.pool.example", ExampleAddress)]
    [InlineData("stratum.btcpowlab-pool.com.evil.example", ExampleAddress)]
    [InlineData("stratum.btcpowlab-pool.com", "not-a-wallet")]
    [InlineData("stratum.btcpowlab-pool.com", "")]
    public void RejectsUnrelatedHostsAndInvalidAddresses(string host, string address)
    {
        Assert.Null(PoolDashboardLinks.ForMiner(host, address));
    }
}
