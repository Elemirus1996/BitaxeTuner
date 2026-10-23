using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Plugs;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Reports;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.25 Jahresrückblick „Dein Mining-Jahr“.</summary>
public class YearReviewTests
{
    [Fact]
    public void Periods_are_the_last_12_months_or_a_calendar_year()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0);
        Assert.True(YearReview.TryParse("last12", now, out var from, out var to, out _));
        Assert.Equal((new DateTime(2025, 11, 1), new DateTime(2026, 11, 1)), (from, to));
        Assert.True(YearReview.TryParse("2025", now, out from, out to, out var title));
        Assert.Equal((new DateTime(2025, 1, 1), new DateTime(2026, 1, 1)), (from, to));
        Assert.Contains("2025", title);
        Assert.False(YearReview.TryParse("letztes Jahr", now, out _, out _, out _));
    }

    private static PeriodReport Month(DateTime m, double hash, double kwh, double cost, decimal income, int payouts) =>
        new(m.ToString("yyyy-MM"), m, m.AddMonths(1), false,
            [new PeriodMinerRow("Gamma Keller", "10.0.25.1", 40000, 43200, hash, 55, 15, kwh, 1)], [],
            new CostResult(kwh, cost, 720, 0), income > 0 ? [new PeriodIncome("BTC", payouts, 0.0001m, income, 0)] : [], "€", null, "EUR");

    [Fact]
    public void Review_sums_months_picks_best_month_and_hides_names_and_addresses()
    {
        var now = new DateTime(2026, 10, 23);
        var from = new DateTime(2025, 11, 1);
        var months = new[]
        {
            Month(new DateTime(2026, 8, 1), 1100, 10.5, 3.2, 4.10m, 2),
            Month(new DateTime(2026, 9, 1), 1200, 11, 3.4, 6.50m, 3),
        };
        var diffs = new[]
        {
            new BestDiffRecord("10.0.25.1", "BTC", 4.2e9, "4.2G", new DateTime(2026, 9, 3)),
            new BestDiffRecord("10.0.25.1", "BCH", 9.9e9, "9.9G", new DateTime(2024, 1, 1)),   // außerhalb des Zeitraums
        };
        var stones = new[]
        {
            new NewsItem("milestone-kwh-100", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), "milestone", null, "100 kWh geschürft", "", ""),
            new NewsItem("milestone-old", new DateTime(2024, 9, 10, 0, 0, 0, DateTimeKind.Utc), "milestone", null, "Alt", "", ""),
        };
        var r = YearReview.Build("Dein Mining-Jahr", from, from.AddYears(1), now, months,
            new Dictionary<string, string> { ["10.0.25.1"] = "Miner 1" }, diffs, stones);

        Assert.Equal(21.5, r.Kwh, 6);
        Assert.Equal(10.60m, r.Income);
        Assert.Equal(5, r.Payouts);
        Assert.Equal(new DateTime(2026, 9, 1), r.BestMonth!.Month);
        Assert.Equal(("4.2G", "Miner 1"), (r.BestDiff, r.BestDiffMiner));
        Assert.Equal(["100 kWh geschürft"], r.Milestones);
        Assert.Equal("Miner 1", Assert.Single(r.Miners).Name);
        Assert.True(r.Partial);

        var html = r.Html();
        Assert.Contains("Miner 1", html);
        Assert.DoesNotContain("10.0.25.1", html);
        Assert.DoesNotContain("Gamma Keller", html);
    }

    [Fact]
    public async Task Hub_builds_the_review_and_the_epaper_page_renders()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma Keller", Host = "10.0.25.1" });
        config.Display.Pages.YearReview = true;
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path, OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h), FanDeviceFactory = _ => new SimulatedFanDevice(),
        });
        await hub.PollNowAsync();
        var now = DateTime.Now;
        for (var i = 0; i < 60; i++) hub.History!.AddSample("10.0.25.1", now.AddDays(-20).AddMinutes(i), 1150, 56, 15.5, true);

        var shared = hub.BuildYearReview("last12", showNames: false, now);
        Assert.Equal("Miner 1", Assert.Single(shared.Miners).Name);
        Assert.DoesNotContain("Gamma Keller", shared.Html());
        Assert.Equal("Gamma Keller", hub.BuildYearReview("last12", showNames: true, now).Miners[0].Name);
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => hub.BuildYearReview("irgendwann", true, now));

        var page = hub.PreviewScene(DisplayScene.YearReview, now);
        Assert.Equal(DisplayScene.YearReview, page.Scene);
        Assert.NotNull(page.YearReview);
        Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(page).Length);
        if (Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW") is { } previewDir)
        {
            var months = Enumerable.Range(0, 12).Select(i => new YearReviewMonth(new DateTime(2025, 11, 1).AddMonths(i), 1100 + i * 20, 10 + i, 3 + i * 0.1, 2m + i % 5)).ToList();
            var sample = shared with
            {
                BestDiff = "4.21G", AvgHashGh = 2310, OnlineHours = 16420, Kwh = 268, Cost = 80.4, Income = 41.7m, Payouts = 14,
                Months = months, BestMonth = months[9], Milestones = ["100 kWh", "1 Jahr online"],
            };
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(page with { YearReview = sample }), Path.Combine(previewDir, "epaper-year.png"));
        }
    }
}
