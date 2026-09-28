using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

public class PoolQuickLinkTests
{
    private const string Address = "bc1qexampleexampleexampleexampleexample0";

    [Theory]
    [InlineData("public-pool.io", "https://web.public-pool.io/#/app/" + Address)]
    [InlineData("stratum+tcp://public-pool.io:21496", "https://web.public-pool.io/#/app/" + Address)]
    [InlineData("mine.ocean.xyz", "https://ocean.xyz/stats/" + Address)]
    [InlineData("solo.ckpool.org", "https://stats.ckpool.org/users/" + Address)]
    [InlineData("eusolo.ckpool.org", "https://stats.ckpool.org/users/" + Address)]
    [InlineData("eu.m45core.com", "https://eu.m45core.com/user/" + Address)]
    [InlineData("m45core.com", "https://m45core.com/user/" + Address)]
    [InlineData("STRATUM.BTCPOWLAB-POOL.COM:3333", "https://btcpowlab-pool.com/miner/" + Address)]
    [InlineData("btc.solofury.com:6060", "https://solofury.com/miner/?coin=btc&addr=" + Address)]
    [InlineData("pool.nerdminers.org", "https://pool.nerdminers.org/users/" + Address)]
    [InlineData("solo.mineshop.eu", "https://solo.mineshop.eu/miner/?wallet=" + Address)]
    [InlineData("stratum-de.solo.mineshop.eu:3335", "https://solo.mineshop.eu/miner/?wallet=" + Address)]
    [InlineData("stratum+tcp://eu3.solopool.org:8005", "https://btc.solopool.org/miner/" + Address)]
    public void KnownPools(string stratumUrl, string expected)
    {
        var link = PoolQuickLinks.For(stratumUrl, Address + ".bitaxe1");
        Assert.Equal(expected, link?.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("pool.example.com", Address)]
    [InlineData("public-pool.io.evil.example", Address)]
    [InlineData("evilpublic-pool.io", Address)]
    [InlineData("stratum.btcpowlab-pool.com.evil.example", Address)]
    [InlineData("bch.solofury.com:7070", Address)]          // anderer Coin auf SoloFury
    [InlineData("eu3.solopool.org:8002", Address)]          // SoloPool.org, aber nicht der BTC-Port
    [InlineData("eu3.solopool.org", Address)]               // ohne Port: Coin unklar
    [InlineData("xx.ckpool.org", Address)]
    [InlineData("public-pool.io", "")]
    [InlineData("public-pool.io", "a/b")]
    [InlineData("public-pool.io", "addr#x")]
    [InlineData("", Address)]
    public void NoLinkForUnknownHostsOrOddUsers(string stratumUrl, string user)
    {
        Assert.Null(PoolQuickLinks.For(stratumUrl, user));
    }

    [Theory]
    [InlineData(8005)]
    [InlineData(9005)]
    public void Port_given_separately_as_in_AxeOS(int port)
    {
        var link = PoolQuickLinks.For("us1.solopool.org", Address, port);
        Assert.Equal("https://btc.solopool.org/miner/" + Address, link?.Url.AbsoluteUri);
        Assert.Null(PoolQuickLinks.For("us1.solopool.org", Address, 3333));
    }

    [Fact]
    public void UsesActivePoolWhenOnFallback()
    {
        var info = new SystemInfo
        {
            stratumURL = "public-pool.io", stratumUser = Address + ".w1",
            fallbackStratumURL = "solo.ckpool.org", fallbackStratumUser = Address + ".w1",
        };
        Assert.Equal("public-pool", PoolQuickLinks.For(info)?.Pool);

        info.isUsingFallbackStratum = 1;
        Assert.Equal("CKPool Solo", PoolQuickLinks.For(info)?.Pool);

        info.fallbackStratumUser = null;
        Assert.Null(PoolQuickLinks.For(info));
    }
}
