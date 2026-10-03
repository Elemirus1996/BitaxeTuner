using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>Neue E-Paper-Seiten (0.9.7): Kurs, Tages-/Monatsbilanz mit Graph, Gruppen, Strompreis-Ampel, QR-Code, Fühler.</summary>
public sealed class DisplayPagesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MinerHub _hub;
    private readonly AppConfig _config = new();

    public DisplayPagesTests()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        _config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.8.1", Groups = ["Keller"] });
        _config.Devices.Add(new DeviceConfig { Name = "Supra", Host = "10.0.8.2", Groups = ["Büro"] });
        _config.Display.Enabled = true;
        _hub = new MinerHub(_config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => new SimulatedFanDevice(),
            WebUrl = () => "https://192.0.2.5:8484/",
        });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private static void RendersFully(DisplayModel m) => Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(m).Length);

    [Fact]
    public async Task Every_new_page_renders_with_and_without_data()
    {
        await _hub.PollNowAsync();
        foreach (var scene in new[] { DisplayScene.Prices, DisplayScene.Monthly, DisplayScene.Group, DisplayScene.Power, DisplayScene.Qr, DisplayScene.Sensors })
        {
            var m = _hub.PreviewScene(scene, DateTime.Now);
            RendersFully(m);
            RendersFully(m with { Inverted = true });
        }
        _config.Display.DailyChart = "efficiency";
        RendersFully(_hub.PreviewScene(DisplayScene.Daily, DateTime.Now));
    }

    [Fact]
    public void Price_page_shows_btc_bch_or_both_and_difficulty_only_for_btc()
    {
        var now = DateTime.Now;
        var pts = Enumerable.Range(0, 24).Select(i => new DisplayValue(now.AddHours(-24 + i), 60000 + i * 100)).ToList();
        var coins = new List<DisplayCoin> { new("BTC", "Bitcoin (BTC)", 62300, 3.8, pts), new("BCH", "Bitcoin Cash (BCH)", 412.5, -1.2, pts) };
        var m = new DisplayModel("T", now, 1000, 15, 15, 1, 1, null, "Auto", false, false, [], []);
        RendersFully(m with { Scene = DisplayScene.Prices, Coins = coins, Difficulty = new DisplayDifficulty(42.5, 2.1, 1160, now.AddDays(8)) });
        RendersFully(m with { Scene = DisplayScene.Prices, Coins = coins.Take(1).ToList() });

        _config.Display.PriceCoins = "both";
        Assert.Equal(["BTC", "BCH"], _hub.PreviewScene(DisplayScene.Prices, now).Coins!.Select(c => c.Symbol));
        _config.Display.PriceCoins = "bch";
        var bch = _hub.PreviewScene(DisplayScene.Prices, now);
        Assert.Equal(["BCH"], bch.Coins!.Select(c => c.Symbol));
        Assert.Null(bch.Difficulty);                                                  // BCH passt die Difficulty je Block an
    }

    [Fact]
    public void Difficulty_answer_from_mempool_is_parsed()
    {
        var d = NetworkClient.ParseDifficulty("""
            {"progressPercent":42.56,"difficultyChange":-1.5,"estimatedRetargetDate":1790000000000,"remainingBlocks":1158,"nextRetargetHeight":917280}
            """);
        Assert.Equal(42.56, d.ProgressPercent);
        Assert.Equal(-1.5, d.ExpectedChangePercent);
        Assert.Equal(1158, d.RemainingBlocks);
        Assert.Equal(917280, d.NextHeight);
        Assert.NotNull(d.Eta);
    }

    [Fact]
    public async Task Monthly_page_sums_the_month_and_draws_a_bar_per_day()
    {
        var now = DateTime.Now;
        var start = new DateTime(now.Year, now.Month, 1);
        for (var t = start; t < now && t < start.AddDays(3); t = t.AddMinutes(10))
            _hub.History!.AddSample("10.0.8.1", t, 1200, 55, 18, true);
        await _hub.PollNowAsync();
        _config.Display.MonthlyChart = "kwh";
        var m = _hub.PreviewScene(DisplayScene.Monthly, now);
        Assert.Equal(DisplayScene.Monthly, m.Scene);
        Assert.NotEmpty(m.Monthly!.Bars);
        Assert.True(m.Monthly.Kwh > 0);
        RendersFully(m);
    }

    [Fact]
    public async Task Group_page_only_contains_the_miners_of_that_group()
    {
        await _hub.PollNowAsync();
        _config.Display.Pages = new DisplayPages { Overview = false, Groups = true, Daily = false, Chart = false, Soak = false };
        var first = _hub.ComposeDisplay(DateTime.Now);
        var second = _hub.ComposeDisplay(DateTime.Now, nextPage: true);
        Assert.Equal(DisplayScene.Group, first.Scene);
        Assert.Single(first.Miners);
        Assert.Single(second.Miners);
        Assert.NotEqual(first.Miners[0].Name, second.Miners[0].Name);
        Assert.Contains("·", first.Title);
        Assert.Equal("Seite 1/2", first.PageLabel);
    }

    [Fact]
    public void Power_page_marks_the_cheapest_three_hours()
    {
        var now = DateTime.Now;
        var h0 = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Local).ToUniversalTime();
        double[] ct = [30, 28, 25, 12, 10, 11, 26, 31, 33, 29];
        _hub.Prices.SetPrices(ct.Select((c, i) => new PricePoint(h0.AddHours(i), h0.AddHours(i + 1), c)).ToList(), "aWATTar Test");
        var m = _hub.PreviewScene(DisplayScene.Power, now);
        Assert.Equal(10, m.Power!.Hours.Count);
        Assert.Equal(h0.AddHours(3).ToLocalTime(), m.Power.CheapFrom);
        Assert.Equal(11, m.Power.CheapAvg);
        RendersFully(m);
    }

    [Fact]
    public void Qr_page_uses_this_server_or_an_own_address()
    {
        var m = _hub.PreviewScene(DisplayScene.Qr, DateTime.Now);
        Assert.Equal("https://192.0.2.5:8484/", m.Qr!.Url);
        Assert.True(m.Qr.Modules.Count >= 21);                                        // kleinste QR-Version 21 × 21 (+ Rand)
        Assert.All(m.Qr.Modules, row => Assert.Equal(m.Qr.Modules.Count, row.Length));
        _config.Display.QrUrl = "https://bitaxetuner.local:8484/";
        Assert.Equal("https://bitaxetuner.local:8484/", _hub.PreviewScene(DisplayScene.Qr, DateTime.Now).Qr!.Url);
    }

    [Fact]
    public void Sensor_page_lists_sensors_with_their_limits()
    {
        var m = new DisplayModel("T", DateTime.Now, 1000, 15, 15, 1, 1, null, "Auto", false, false, [], [])
        {
            Scene = DisplayScene.Sensors,
            Sensors = [new("Netzteil", 41.5, 45, false), new("Miner-Raum", 47.2, 45, true), new("Außen", null, 40, false)],
        };
        RendersFully(m);
    }
}
