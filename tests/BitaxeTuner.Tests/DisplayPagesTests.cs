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
    public async Task Spectra_pages_use_extra_colours_and_black_white_red_stays_unchanged()
    {
        // 0.9.17: Spectra 6 – Trennlinien/Kurven blau, „in Ordnung“ grün; das 7,5″-Display bleibt schwarz/weiß/rot.
        // Mit BT_DISPLAY_DUMP=<Ordner> werden alle Seiten als PNG abgelegt (zum Ansehen).
        await _hub.PollNowAsync();
        var dump = Environment.GetEnvironmentVariable("BT_DISPLAY_DUMP");
        var palette = StatusRenderer.SpectraPalette.Select(p => p.Rgb).ToHashSet();
        var blueSeen = false;
        foreach (var scene in Enum.GetValues<DisplayScene>())
        {
            var m = _hub.PreviewScene(scene, DateTime.Now);
            using var spectra = StatusRenderer.RenderImage(m, "spectra73");
            using var bwr = StatusRenderer.RenderImage(m);
            var spectraColours = Colours(spectra);
            Assert.Subset(palette, spectraColours);
            Assert.DoesNotContain(Colours(bwr), c => c is { R: 0, G: 60, B: 200 } or { R: 0, G: 150, B: 60 } or { R: 255, G: 230, B: 0 });
            blueSeen |= spectraColours.Contains(new SixLabors.ImageSharp.PixelFormats.Rgb24(0, 60, 200));
            Assert.Equal(StatusRenderer.SpectraBytes, StatusRenderer.Render(m with { Inverted = true }, "spectra73").Length);
            if (dump is { Length: > 0 })
            {
                Directory.CreateDirectory(dump);
                await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(spectra, Path.Combine(dump, $"{scene}-spectra.png"));
                await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(bwr, Path.Combine(dump, $"{scene}-bwr.png"));
            }
        }
        Assert.True(blueSeen);

        // Diagramm mit Messpunkten: Kurve blau statt rot
        var now = DateTime.Now;
        var chart = _hub.PreviewScene(DisplayScene.Overview, now) with
        {
            Scene = DisplayScene.Chart, ChartKind = "hashrate",
            Chart = Enumerable.Range(0, 48).Select(i => new DisplayPoint(now.AddMinutes(-30 * (47 - i)), 1000 + 80 * Math.Sin(i / 5.0), 55)).ToList(),
        };
        using var chartImg = StatusRenderer.RenderImage(chart, "spectra73");
        var blue = 0;
        chartImg.ProcessPixelRows(rows =>
        {
            for (var y = 100; y < 400; y++)
                foreach (var p in rows.GetRowSpan(y)) if (p is { R: 0, G: 60, B: 200 }) blue++;
        });
        Assert.True(blue > 500, $"blaue Pixel: {blue}");
        if (dump is { Length: > 0 }) await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(chartImg, Path.Combine(dump, "ChartData-spectra.png"));
    }

    private static HashSet<SixLabors.ImageSharp.PixelFormats.Rgb24> Colours(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img)
    {
        var set = new HashSet<SixLabors.ImageSharp.PixelFormats.Rgb24>();
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var p in rows.GetRowSpan(y)) set.Add(p);
        });
        return set;
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

    [Fact]
    public async Task Bitcoin_pool_and_month_compare_pages_render()
    {
        // 0.9.16: Seiten „Bitcoin-Netz“, „Pool“, „Monatsvergleich“
        await _hub.PollNowAsync();
        var fees = BitaxeTuner.Core.Network.NetworkClient.ParseFees("""{"fastestFee":12,"halfHourFee":8,"hourFee":5,"economyFee":2,"minimumFee":1}""");
        Assert.Equal((12, 8, 5, 2), (fees.Fastest, fees.HalfHour, fees.Hour, fees.Economy));
        foreach (var scene in new[] { DisplayScene.Bitcoin, DisplayScene.PoolAccount, DisplayScene.MonthCompare })
            RendersFully(_hub.PreviewScene(scene, DateTime.Now));

        var now = DateTime.Now;
        var m = _hub.PreviewScene(DisplayScene.Overview, now);
        var btc = m with
        {
            Scene = DisplayScene.Bitcoin,
            Bitcoin = new DisplayBitcoin(934_512, "Foundry USA", now.AddMinutes(-4), fees, new DisplayDifficulty(62.5, 3.4, 756, now.AddDays(5)),
                1_050_000, 115_488, 3.125, 1.5625),
        };
        var pool = m with
        {
            Scene = DisplayScene.PoolAccount,
            PoolPage = new DisplayPoolPage([new("Gamma 1", "BTC", "Mining-Dutch", "PPS"), new("Gamma 2", "DGB", "Mining-Dutch", "PPS"), new("Nerd", "BCH", "Public Pool", null)],
                [new("BTC", 0.00012345m, 9.87m), new("DGB", 154.2m, 1.25m)], "€", now.AddDays(-2), "DGB", "Mining-Dutch", "99,8 % verfügbar · 0,12 % abgelehnt"),
        };
        var months = Enumerable.Range(0, 6).Select(i => new DisplayMonthBar(new DateTime(now.Year, now.Month, 1).AddMonths(i - 5), 80 + i * 4, 20m + i, i == 0 ? null : 14m + i * 2)).ToList();
        var compare = m with { Scene = DisplayScene.MonthCompare, MonthCompare = new DisplayMonthCompare(months, "€") };
        foreach (var x in new[] { btc, pool, compare }) RendersFully(x);
        var dir = Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW");
        if (dir is not null)
        {
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(btc), Path.Combine(dir, "epaper-bitcoin.png"));
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(pool), Path.Combine(dir, "epaper-pool.png"));
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(compare), Path.Combine(dir, "epaper-months.png"));
        }
    }

    [Fact]
    public void Page_windows_limit_pages_and_interval_by_time_of_day()
    {
        // 0.9.16: im Zeitfenster nur Verlauf und Kurs, mit eigenem Intervall
        _config.Display.Pages = new DisplayPages { Overview = true, Daily = true, Chart = true, Prices = true };
        _config.Display.AlarmFullscreen = false;                     // keine Warnung als Vollbild dazwischen
        var hour = DateTime.Now.Hour;
        _config.Display.PageWindows = [new DisplayPageWindow { From = hour, To = (hour + 1) % 24, Pages = ["Chart", "Prices"], IntervalMinutes = 20 }];
        var seen = Enumerable.Range(0, 6).Select(_ => _hub.ComposeDisplay(DateTime.Now, nextPage: true).Scene).Distinct().ToHashSet();
        Assert.Subset(new HashSet<DisplayScene> { DisplayScene.Chart, DisplayScene.Prices }, seen);
        Assert.DoesNotContain(DisplayScene.Overview, seen);
        // Fenster ohne eingeschaltete Seite: alle Seiten wie sonst
        _config.Display.PageWindows[0].Pages = ["Bitcoin"];
        Assert.Contains(_hub.ComposeDisplay(DateTime.Now, nextPage: true).Scene, new[] { DisplayScene.Overview, DisplayScene.Daily, DisplayScene.Chart, DisplayScene.Prices });
    }

    [Fact]
    public async Task Profit_and_solo_pages_render()
    {
        // 0.9.16: Seiten „Ertrag & Strom“ und „Solo-Chance“
        await _hub.PollNowAsync();
        _hub.Odds.SetDifficultyForTest(BitaxeTuner.Core.Tax.Models.CoinType.Bitcoin, 1.4e14);
        RendersFully(_hub.PreviewScene(DisplayScene.Profit, DateTime.Now));
        var solo = _hub.PreviewScene(DisplayScene.Solo, DateTime.Now);
        Assert.Equal(2, solo.Solo!.Count);                                    // BTC und BCH
        Assert.Equal(1.4e14, solo.Solo[0].Difficulty);
        RendersFully(solo);

        var m = solo with
        {
            Scene = DisplayScene.Profit,
            Profit = new DisplayProfit("€", "pool", [
                new("24 Stunden", 0.42m, 0.61m, -0.19m, 2.9, false), new("7 Tage", 3.10m, 4.20m, -1.10m, 20.3, false),
                new("30 Tage", 15.80m, 17.95m, -2.15m, 86.7, true), new("Oktober", 7.40m, 8.10m, -0.70m, 39.1, false)]),
        };
        RendersFully(m);
        var oneCoin = solo with { Solo = [new("BTC", 3, 3150, 1.4e14, 2.3e-5, 0.0083, 43500)] };
        var twoCoins = solo with { Solo = [new("BTC", 3, 3150, 1.4e14, 2.3e-5, 0.0083, 43500), new("BCH", 1, 1100, 5.6e11, 1.9e-3, 0.5, 520)] };
        RendersFully(oneCoin);
        RendersFully(twoCoins);
        var dir = Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW");
        if (dir is not null)
        {
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(m), Path.Combine(dir, "epaper-profit.png"));
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(oneCoin), Path.Combine(dir, "epaper-solo1.png"));
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(twoCoins), Path.Combine(dir, "epaper-solo2.png"));
        }
    }

    [Fact]
    public async Task Price_page_can_show_the_coins_being_mined()
    {
        await _hub.PollNowAsync();
        _config.Display.PriceCoins = "mined";
        foreach (var sym in new[] { "BTC", "BCH", "DGB", "NMC" }) _hub.SetCoinQuoteForTest(sym, 1000, 2.5);
        var m = _hub.PreviewScene(DisplayScene.Prices, DateTime.Now);
        Assert.NotEmpty(m.Coins!);                                            // geschürfte Coins der Simulation
        RendersFully(m);

        // 2×2-Raster mit vier Coins
        var now = DateTime.Now;
        var pts = Enumerable.Range(0, 24).Select(i => new DisplayValue(now.AddHours(-24 + i), 0.0078 + i * 0.00002)).ToList();
        var four = new List<DisplayCoin> { new("BTC", "Bitcoin (BTC)", 74000, 1.2, pts), new("DGB", "DigiByte (DGB)", 0.0081, -3.4, pts),
            new("NMC", "Namecoin (NMC)", 1.23, 0.4, pts), new("WJK", "WJK", 0.00012, null, []) };
        var model = m with { Coins = four, Difficulty = null };
        RendersFully(model);
        var dir = Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW");
        if (dir is not null) SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(model), Path.Combine(dir, "epaper-coins4.png"));
    }
}
