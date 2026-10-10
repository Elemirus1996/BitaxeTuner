using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.13: Pool je Miner erkennen (Name, Link zur Pool-Seite) für die Übersicht.</summary>
public class PoolIdentityTests
{
    private static SystemInfo Info(string url, string user = "konto.werkbank", bool fallback = false) => fallback
        ? new SystemInfo { stratumURL = "other.invalid", fallbackStratumURL = url, fallbackStratumUser = user, isUsingFallbackStratum = 1 }
        : new SystemInfo { stratumURL = url, stratumUser = user };

    [Fact]
    public void Known_pools_get_their_name_and_home_page()
    {
        var md = PoolIdentities.For(Info("stratum+tcp://sha256.mining-dutch.nl:9996"))!;
        Assert.Equal(("Mining-Dutch", "https://www.mining-dutch.nl/"), (md.Name, md.Url!.AbsoluteUri));

        var fallback = PoolIdentities.For(Info("btc.viabtc.com", fallback: true))!;
        Assert.Equal("ViaBTC", fallback.Name);                                       // aktiver Pool = Fallback

        // Quick-Link-Pools: Seite des Pool-Benutzers
        var pp = PoolIdentities.For(Info("public-pool.io", "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4.w"))!;
        Assert.Equal("public-pool", pp.Name);
        Assert.Contains("/#/app/bc1q", pp.Url!.AbsoluteUri);
    }

    [Fact]
    public void Unknown_pools_fall_back_to_their_domain_and_local_nodes_get_no_link()
    {
        var other = PoolIdentities.For(Info("stratum+tcp://eu.stratum.example-pool.com:3333"))!;
        Assert.Equal(("example-pool.com", "https://example-pool.com/"), (other.Name, other.Url!.AbsoluteUri));
        Assert.Equal("example.co.uk", PoolIdentities.For(Info("solo.example.co.uk"))!.Name);

        var node = PoolIdentities.For(Info("10.0.0.5:3333"))!;
        Assert.Equal(("10.0.0.5", null), (node.Name, node.Url));
        Assert.Null(PoolIdentities.For(Info("")));
        Assert.Null(PoolIdentities.For(null));
    }
}
