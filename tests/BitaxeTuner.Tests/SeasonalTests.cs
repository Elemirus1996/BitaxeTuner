using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.25: Saisonaler Feinschliff – nur Vorschläge aus stabilen Benchmarks, nie automatisch angewendet.</summary>
public class SeasonalTests
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0);

    private static StepResult Step(int f, int mv, double gh, double w, double temp, StepOutcome outcome = StepOutcome.Stable) =>
        new() { FrequencyMhz = f, CoreVoltageMv = mv, AvgHashRateGh = gh, AvgPowerW = w, AvgChipTempC = temp, Outcome = outcome, SampleCount = 10, Timestamp = Now.AddMonths(-3) };

    // Benchmark im Frühjahr: 600 MHz bei 58 °C, 550 MHz bei 54 °C, 500 MHz bei 50 °C
    private static readonly StepResult[] Bench =
    [
        Step(600, 1200, 1500, 20, 58), Step(550, 1150, 1380, 17, 54), Step(500, 1100, 1250, 15, 50),
    ];

    [Fact]
    public void Much_warmer_near_the_limit_suggests_the_next_more_economical_tested_setting()
    {
        var s = SeasonalAdvisor.Evaluate(Gamma, 600, 1200, avg7dC: 66, tempWarnC: 70, Bench, [], null, Now);
        Assert.NotNull(s);
        Assert.Equal("down", s!.Direction);
        Assert.Equal((550, 1150), (s.ToMhz, s.ToMv));   // die nächste Stufe, nicht gleich die sparsamste
        Assert.Equal(8, s.ShiftC, 3);
        Assert.Equal(62, s.ExpectedC, 3);                // 54 °C im Benchmark + 8 °C wärmer
    }

    [Fact]
    public void Nothing_without_a_clear_shift_far_from_the_limit_or_without_benchmark()
    {
        Assert.Null(SeasonalAdvisor.Evaluate(Gamma, 600, 1200, 61, 70, Bench, [], null, Now));      // nur 3 °C wärmer
        Assert.Null(SeasonalAdvisor.Evaluate(Gamma, 600, 1200, 64, 80, Bench, [], null, Now));      // weit unter der Warnschwelle
        Assert.Null(SeasonalAdvisor.Evaluate(Gamma, 575, 1175, 66, 70, Bench, [], null, Now));      // aktuelle Einstellung nie gebenchmarkt
    }

    [Fact]
    public void Unstable_or_soak_failed_settings_are_never_suggested()
    {
        var bench = new[] { Bench[0], Step(550, 1150, 1380, 17, 54, StepOutcome.Unstable), Bench[2] };
        Assert.Equal(500, SeasonalAdvisor.Evaluate(Gamma, 600, 1200, 66, 70, bench, [], null, Now)!.ToMhz);
        var failed = new SoakResultRecord("h", Now.AddDays(-30), Now.AddDays(-29), 550, 1150, false, 0.8, "zu heiß");
        Assert.Equal(500, SeasonalAdvisor.Evaluate(Gamma, 600, 1200, 66, 70, Bench, [failed], null, Now)!.ToMhz);
    }

    [Fact]
    public void Cooler_again_goes_back_only_to_the_setting_from_before()
    {
        var up = SeasonalAdvisor.Evaluate(Gamma, 550, 1150, avg7dC: 48, tempWarnC: 70, Bench, [], (600, 1200), Now);
        Assert.Equal("up", up!.Direction);
        Assert.Equal((600, 1200), (up.ToMhz, up.ToMv));
        Assert.Null(SeasonalAdvisor.Evaluate(Gamma, 550, 1150, 48, 70, Bench, [], null, Now));          // nie selbst hochgeschraubt
        Assert.Null(SeasonalAdvisor.Evaluate(Gamma, 550, 1150, 48, 55, Bench, [], (600, 1200), Now));   // wäre zu nah an der Warnschwelle
    }

    private static (MinerHub Hub, SimulatedMinerClient Sim) Hub(TempDir dir)
    {
        var config = new AppConfig { FilePath = Path.Combine(dir.Path, "config.json"), TempWarn = 70 };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.25.9", ProfileId = "bitaxe-gamma", MonitorOnly = false });
        SimulatedMinerClient? sim = null;
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path, OnlineChecks = false,
            ClientFactory = h => sim = new SimulatedMinerClient(Gamma, 3, h),
        });
        return (hub, sim!);
    }

    [Fact]
    public async Task Hub_suggests_once_a_week_and_applies_only_on_request()
    {
        using var dir = new TempDir();
        var (hub, sim) = Hub(dir);
        using var _ = hub;
        await sim.ApplySettingsAsync(600, 1200);
        await hub.PollNowAsync();
        var d = hub.Devices[0];
        var now = DateTime.Now;
        await hub.Results.SaveAsync(new BenchmarkSession { DeviceAddress = d.Host, StartedAt = now.AddMonths(-3), Results = [.. Bench] });
        for (var t = now.AddDays(-7).AddMinutes(1); t < now; t = t.AddMinutes(1)) hub.History!.AddSample(d.Host, t, 1500, 66, 20, true);

        hub.TickSeasonal(now, force: true);
        var s = hub.SeasonalOf(d);
        Assert.NotNull(s);
        Assert.Equal(550, s!.ToMhz);
        Assert.Contains(hub.ProblemsOf(d), p => p.Key == "seasonal");
        Assert.Equal((600, 1200), sim.CurrentSettings);   // nichts ungefragt geändert

        hub.DismissSeasonal(d, never: false, "Test");
        hub.TickSeasonal(now.AddHours(2), force: true);
        Assert.Null(hub.SeasonalOf(d));                  // höchstens einmal pro Woche

        d.Config.SeasonalSuggestedAt = now.AddDays(-8);
        hub.TickSeasonal(now.AddHours(4), force: true);
        Assert.NotNull(hub.SeasonalOf(d));
        await hub.ApplySeasonalAsync(d, "Test");
        Assert.Equal((550, 1150), sim.CurrentSettings);
        Assert.Equal((600, 1200), (d.Config.SeasonalFromMhz, d.Config.SeasonalFromMv));   // Ziel für den Winter gemerkt
        Assert.Null(d.Config.SeasonalPending);
    }

    [Fact]
    public async Task Never_ask_again_is_kept_per_miner()
    {
        using var dir = new TempDir();
        var (hub, sim) = Hub(dir);
        using var _ = hub;
        await sim.ApplySettingsAsync(600, 1200);
        await hub.PollNowAsync();
        var d = hub.Devices[0];
        var now = DateTime.Now;
        await hub.Results.SaveAsync(new BenchmarkSession { DeviceAddress = d.Host, StartedAt = now.AddMonths(-3), Results = [.. Bench] });
        for (var t = now.AddDays(-7).AddMinutes(1); t < now; t = t.AddMinutes(1)) hub.History!.AddSample(d.Host, t, 1500, 66, 20, true);

        hub.TickSeasonal(now, force: true);
        hub.DismissSeasonal(d, never: true, "Test");
        d.Config.SeasonalSuggestedAt = now.AddDays(-30);
        hub.TickSeasonal(now.AddHours(2), force: true);
        Assert.Null(hub.SeasonalOf(d));
        Assert.True(d.Config.SeasonalMuted);
    }
}
