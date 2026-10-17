using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: weitere Kurse neben den geschürften Coins.</summary>
public class WatchCoinsTests
{
    [Fact]
    public void Catalog_knows_common_coins_and_accepts_own_ids()
    {
        Assert.Equal(("ETH", "ethereum"), CoinCatalog.Parse(" eth "));
        Assert.Equal(("XYZ", "xyz-coin"), CoinCatalog.Parse("xyz=XYZ-Coin"));
        Assert.Null(CoinCatalog.Parse("UNBEKANNT"));                       // ohne ID unbekannt
        Assert.Null(CoinCatalog.Parse("ETH=../../evil"));                  // nur harmlose IDs
        Assert.Equal("xyz-coin", CoinCatalog.IdFor("XYZ", ["XYZ=xyz-coin"]));
    }

    [Fact]
    public async Task Watched_coins_follow_the_mined_ones()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.7.1" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
        Assert.True(await hub.PollNowAsync());
        Assert.Throws<LocalizedException>(() => hub.SetWatchCoins(["ETH", "WASISTDAS"], "Test"));
        Assert.Empty(hub.Config.WatchCoins);                               // nichts gespeichert
        hub.SetWatchCoins(["ETH", "eth", "XYZ=xyz-coin"], "Test");
        Assert.Equal(["ETH", "XYZ=xyz-coin"], hub.Config.WatchCoins);

        hub.SetCoinQuoteForTest("ETH", 2500, -3.2);
        var quotes = hub.CoinQuotes();
        Assert.Equal("mined", quotes[0].Kind);                             // geschürfte zuerst
        var eth = Assert.Single(quotes, q => q.Symbol == "ETH");
        Assert.Equal(("watch", 0), (eth.Kind, eth.Miners));
        Assert.DoesNotContain(quotes, q => q.Symbol == "XYZ");             // noch kein Kurs

        // 0.9.21: Kurse ausblenden – weg aus der Anzeige, für Rechnungen weiter da
        hub.SetHiddenCoins(["eth", " btc ", "bad;coin"], "Test");
        Assert.Equal(["ETH", "BTC"], hub.Config.HiddenCoins);
        Assert.DoesNotContain(hub.CoinQuotes(), q => q.Symbol is "ETH" or "BTC");
        Assert.Contains(hub.AllCoinQuotes(), q => q.Symbol == "ETH");
        hub.SetHiddenCoins(["BTC"], "Test");                              // ETH wieder anzeigen
        Assert.Contains(hub.CoinQuotes(), q => q.Symbol == "ETH");
    }
}
