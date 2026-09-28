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
    [InlineData("xx.ckpool.org", Address)]
    [InlineData("public-pool.io", "")]
    [InlineData("public-pool.io", "a/b")]
    [InlineData("public-pool.io", "addr#x")]
    [InlineData("", Address)]
    public void NoLinkForUnknownHostsOrOddUsers(string stratumUrl, string user)
    {
        Assert.Null(PoolQuickLinks.For(stratumUrl, user));
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
