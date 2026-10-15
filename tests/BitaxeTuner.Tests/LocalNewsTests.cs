using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: Neuigkeiten, die der Server selbst erzeugt.</summary>
public class LocalNewsTests
{
    [Fact]
    public async Task Halving_countdown_comes_in_stages_and_survives_a_restart()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.7.1" });
        using (var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) }))
        {
            Assert.True(await hub.PollNowAsync());                           // ein Miner schürft (BTC)
            var far = hub.HalvingNews("BTC", 920_000, now)!;                 // 130 000 Blöcke ≈ 903 Tage
            Assert.Equal("halving-BTC-1050000-1000", far.Id);
            Assert.Contains("2,5 Jahren", far.Title);
            Assert.Contains("1,5625", far.Text);                             // Blockprämie danach
            Assert.Null(hub.HalvingNews("BTC", 920_500, now));               // gleiche Stufe: keine neue Meldung
            Assert.Equal("halving-BTC-1050000-7", hub.HalvingNews("BTC", 1_049_100, now)!.Id);
            Assert.Equal("halving-BTC-1050000-done", hub.HalvingNews("BTC", 1_050_010, now)!.Id);

            // Kurssprung: je Coin, Tag und Richtung einmal
            hub.SetCoinQuoteForTest("BTC", 81000, 12.4);
            hub.PriceMoveNews(now);
            hub.PriceMoveNews(now.AddHours(1));
            var price = Assert.Single(hub.News.Items(["price"], 10));
            Assert.Contains("+12,4 %", price.Title);
        }
        using var again = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        Assert.Equal(3, again.News.Items(["halving"], 10).Count);           // im Datenordner gemerkt
        Assert.Equal(4, again.News.Items(null, 10).Count);
    }
}
