using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24: Ausfall einzelner Chips bei Mehrchip-Minern erkennen (nur lesend).</summary>
public class ChipHealthTests
{
    [Fact]
    public void Per_chip_hashrate_is_read_from_the_hashrate_monitor()
    {
        using var doc = JsonDocument.Parse("""
            {"hashRate": 4800, "asicCount": 4,
             "hashrateMonitor": {"asics": [{"total": 1210.5, "domains": [300, 302, 304, 304]}, {"total": 1190}, {"total": 1205}, {"total": 1195}]}}
            """);
        Assert.Equal([1210.5, 1190, 1205, 1195], AxeOsClient.Parse(doc.RootElement).ChipHashratesGh);
        using var plain = JsonDocument.Parse("""{"asics": [1000, 990]}""");
        Assert.Equal([1000, 990], AxeOsClient.ChipHashrates(plain.RootElement));
        using var none = JsonDocument.Parse("""{"hashRate": 1200}""");
        Assert.Null(AxeOsClient.ChipHashrates(none.RootElement));
        using var one = JsonDocument.Parse("""{"hashrateMonitor": {"asics": [{"total": 1200}]}}""");
        Assert.Null(AxeOsClient.ChipHashrates(one.RootElement));                 // ein Chip: nichts zu vergleichen
    }

    [Fact]
    public void Chips_are_compared_with_the_median_of_the_others()
    {
        var r = MinerHub.RateChips([1200, 1190, 1210, 600, 0]);              // Median 1190
        Assert.Equal(["ok", "ok", "ok", "weak", "dead"], r.Select(x => x.State));
        Assert.Equal(50, r[3].PercentOfMedian);
        Assert.Equal(0, r[4].PercentOfMedian);
        Assert.Empty(MinerHub.RateChips([0, 0]));
        Assert.Empty(MinerHub.RateChips([1200]));
    }

    [Fact]
    public void A_weak_chip_is_only_reported_after_30_minutes_and_clears_again()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Hex", Host = "10.0.18.1" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var d = hub.Devices.Single();
        d.State.Info = new SystemInfo { temp = 55, vrTemp = 50, hashRate = 4000, expectedHashrate = 4000, uptimeSeconds = 7200 };
        var t0 = DateTime.Now.AddHours(-1);
        double[] weak = [1300, 1310, 300, 1290];

        hub.RecordChipHashrates(d, weak, t0);
        hub.RecordChipHashrates(d, weak, t0.AddMinutes(10));
        Assert.All(hub.ChipStatesOf(d), c => Assert.Equal("ok", c.State));     // zweimal auffällig: noch nicht
        hub.RecordChipHashrates(d, weak, t0.AddMinutes(15));                  // innerhalb von 10 min: zählt nicht
        hub.RecordChipHashrates(d, weak, t0.AddMinutes(20));
        var chip3 = hub.ChipStatesOf(d).Single(c => c.State != "ok");
        Assert.Equal((3, "weak"), (chip3.Index, chip3.State));
        var p = Assert.Single(hub.ProblemsOf(d), x => x.Key == "chip-3");
        Assert.Equal("warn", p.Severity);
        Assert.Contains("23", p.Why);                                          // 300 von Median 1295 ≈ 23 %
        var rows = hub.History!.QueryChipHashrates(d.Host, t0.AddMinutes(-1), t0.AddHours(1));
        Assert.Equal(4, rows.Count);                                           // jede Minute gespeichert, auch zwischen den Prüfungen
        Assert.All(rows, x => Assert.Equal(4, x.Temps.Length));

        hub.RecordChipHashrates(d, [1300, 1310, 1295, 1290], t0.AddMinutes(30));   // wieder gleichauf
        Assert.All(hub.ChipStatesOf(d), c => Assert.Equal("ok", c.State));
        Assert.DoesNotContain(hub.ProblemsOf(d), x => x.Key.StartsWith("chip-"));
    }
}
