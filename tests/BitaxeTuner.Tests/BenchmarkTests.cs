using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

public class BenchmarkTests
{
    private static DeviceProfile Gamma => ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");

    private static BenchmarkSettings FastSettings(DeviceProfile p)
    {
        var s = BenchmarkSettings.FromProfile(p);
        s.WarmupSeconds = 30;
        s.MeasureSeconds = 150;
        s.SampleIntervalSeconds = 15;
        s.MinSamples = 7;
        return s;
    }

    private static BenchmarkEngine Engine(SimulatedMinerClient sim, DeviceProfile p, List<string>? log = null) =>
        new(sim, p) { Delay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, Log = m => log?.Add(m) };

    [Fact]
    public async Task Full_run_finds_stable_settings_and_applies_best()
    {
        var p = Gamma;
        var sim = new SimulatedMinerClient(p, seed: 7);
        var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = FastSettings(p) };

        await Engine(sim, p).RunAsync(session, CancellationToken.None);

        Assert.True(session.IsFinished);
        Assert.Contains(session.Results, r => r.IsStable);
        Assert.Contains(session.Results, r => r.Outcome == StepOutcome.Unstable);
        // Frequenz steigt über den Start hinaus
        Assert.True(session.Results.Max(r => r.FrequencyMhz) > p.DefaultFrequencyMhz);

        var best = ResultRanking.Best(session.Results, RankingMode.Balanced)!;
        Assert.Equal((best.FrequencyMhz, best.CoreVoltageMv), sim.CurrentSettings);
    }

    [Fact]
    public async Task Overheating_stops_immediately_and_restores_original()
    {
        var p = Gamma;
        var sim = new SimulatedMinerClient(p) { ExtraTempC = 40 };
        var s = FastSettings(p);
        s.RestoreMode = RestoreMode.Original;
        var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = s };
        var original = sim.CurrentSettings;

        await Engine(sim, p).RunAsync(session, CancellationToken.None);

        var only = Assert.Single(session.Results);
        Assert.Equal(StepOutcome.LimitExceeded, only.Outcome);
        Assert.Contains("Chiptemperatur", only.Message);
        Assert.Equal(original, sim.CurrentSettings);
    }

    [Fact]
    public async Task Cancel_restores_original_settings()
    {
        var p = Gamma;
        var sim = new SimulatedMinerClient(p);
        var s = FastSettings(p);
        s.RestoreMode = RestoreMode.Original;
        s.StartFrequencyMhz = 600;
        var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = s };
        var original = sim.CurrentSettings;

        using var cts = new CancellationTokenSource();
        var polls = 0;
        var engine = new BenchmarkEngine(sim, p)
        {
            Delay = (_, ct) =>
            {
                if (++polls == 5) cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(session, cts.Token));
        Assert.Equal(original, sim.CurrentSettings);
        Assert.Equal("Vom Benutzer abgebrochen.", session.FinishReason);
    }

    [Fact]
    public async Task Offline_device_yields_device_error()
    {
        var p = Gamma;
        var sim = new SimulatedMinerClient(p);
        var s = FastSettings(p);
        s.RestartAfterApply = false;
        var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = s };
        var engine = new BenchmarkEngine(sim, p)
        {
            Delay = (_, _) => { sim.Offline = sim.ApplyCount > 0; return Task.CompletedTask; },
        };

        await engine.RunAsync(session, CancellationToken.None);

        Assert.Equal(StepOutcome.DeviceError, Assert.Single(session.Results).Outcome);
    }

    [Fact]
    public async Task Resume_continues_after_last_result()
    {
        var p = Gamma;
        var sim = new SimulatedMinerClient(p, seed: 3);
        var s = FastSettings(p);
        var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = s };
        session.Results.Add(new StepResult { FrequencyMhz = s.StartFrequencyMhz, CoreVoltageMv = s.StartVoltageMv, Outcome = StepOutcome.Stable, AvgHashRateGh = 1000, AvgPowerW = 15 });

        await Engine(sim, p).RunAsync(session, CancellationToken.None);

        Assert.Single(session.Results, r => r.FrequencyMhz == s.StartFrequencyMhz && r.CoreVoltageMv == s.StartVoltageMv);
        Assert.Equal(s.StartFrequencyMhz + s.FrequencyStepMhz, session.Results[1].FrequencyMhz);
    }

    [Fact]
    public async Task Multiple_devices_run_in_parallel()
    {
        var profiles = ProfileRegistry.LoadBuiltIn().Where(p => p.Id is "bitaxe-gamma" or "nerdqaxe-plusplus" or "bitaxe-supra").ToList();
        var runs = profiles.Select(p =>
        {
            var sim = new SimulatedMinerClient(p, seed: p.Id.Length);
            var session = new BenchmarkSession { DeviceAddress = sim.Address, Settings = FastSettings(p) };
            return Engine(sim, p).RunAsync(session, CancellationToken.None);
        });

        var sessions = await Task.WhenAll(runs);
        Assert.All(sessions, s => Assert.Contains(s.Results, r => r.IsStable));
    }
}

public class PlannerAndRankingTests
{
    private static readonly BenchmarkSettings S = new()
    {
        StartFrequencyMhz = 500, MaxFrequencyMhz = 550, FrequencyStepMhz = 25,
        MinVoltageMv = 1100, StartVoltageMv = 1150, MaxVoltageMv = 1190, VoltageStepMv = 20,
    };

    private static StepResult R(int f, int v, StepOutcome o, double hash = 1000, double power = 15) =>
        new() { FrequencyMhz = f, CoreVoltageMv = v, Outcome = o, AvgHashRateGh = hash, AvgPowerW = power };

    [Fact]
    public void Starts_at_start_values()
    {
        Assert.Equal(new StepPlanner.Step(500, 1150), StepPlanner.Next(S, [], out _));
    }

    [Fact]
    public void Stable_increases_frequency_unstable_increases_voltage()
    {
        Assert.Equal(new StepPlanner.Step(525, 1150), StepPlanner.Next(S, [R(500, 1150, StepOutcome.Stable)], out _));
        Assert.Equal(new StepPlanner.Step(500, 1170), StepPlanner.Next(S, [R(500, 1150, StepOutcome.Unstable)], out _));
    }

    [Fact]
    public void Stops_at_limits()
    {
        Assert.Null(StepPlanner.Next(S, [R(550, 1150, StepOutcome.Stable)], out var r1));
        Assert.Contains("Frequenz", r1);
        Assert.Null(StepPlanner.Next(S, [R(500, 1190, StepOutcome.Unstable)], out var r2));
        Assert.Contains("Spannung", r2);
        Assert.Null(StepPlanner.Next(S, [R(500, 1150, StepOutcome.LimitExceeded)], out var r3));
        Assert.Contains("Grenze", r3);
    }

    [Fact]
    public void Lower_voltage_search_finds_minimum_then_moves_on()
    {
        var s = S.Clone();
        s.TryLowerVoltage = true;
        Assert.Equal(new StepPlanner.Step(500, 1130), StepPlanner.Next(s, [R(500, 1150, StepOutcome.Stable)], out _));
        var history = new List<StepResult> { R(500, 1150, StepOutcome.Stable), R(500, 1130, StepOutcome.Unstable) };
        Assert.Equal(new StepPlanner.Step(525, 1150), StepPlanner.Next(s, history, out _));
    }

    [Fact]
    public void Ranking_modes_pick_expected_results()
    {
        var results = new[]
        {
            R(500, 1150, StepOutcome.Stable, hash: 1000, power: 14),  // 14 J/TH
            R(600, 1200, StepOutcome.Stable, hash: 1200, power: 19.2), // 16 J/TH
            R(650, 1250, StepOutcome.Unstable, hash: 1400, power: 20),
        };
        Assert.Equal(600, ResultRanking.Best(results, RankingMode.MaxHashrate)!.FrequencyMhz);
        Assert.Equal(500, ResultRanking.Best(results, RankingMode.Efficiency)!.FrequencyMhz);
        Assert.Equal(600, ResultRanking.Best(results, RankingMode.Balanced, 0.9)!.FrequencyMhz);
        Assert.Equal(500, ResultRanking.Best(results, RankingMode.Balanced, 0.1)!.FrequencyMhz);
    }

    [Fact]
    public void Trimmed_mean_ignores_outliers()
    {
        double[] values = [100, 100, 100, 100, 100, 100, 100, 0, 0, 5000, 5000, 5000];
        Assert.Equal(100, SampleStats.TrimmedMean(values));
    }
}
