using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;

namespace BitaxeTuner.Tests;

public class LogAlertTests
{
    private static LogLine L(LogLevel level, string tag, string msg) => new(DateTime.Now, level, 1, tag, msg);

    [Fact]
    public void Default_rules_match_typical_problems_but_not_normal_lines()
    {
        var rules = new LogAlertRules(new LogAlertSettings());
        Assert.NotNull(rules.Match(L(LogLevel.Warning, "stratum_task", "Stratum connection lost, reconnecting")));
        Assert.NotNull(rules.Match(L(LogLevel.Error, "asic", "irgendwas")));
        Assert.NotNull(rules.Match(L(LogLevel.Info, "power", "Overheat mode activated")));
        Assert.Null(rules.Match(L(LogLevel.Info, "fan_controller", "Temp: 48.7°C, SetPoint: 60.0°C")));
        Assert.Null(rules.Match(L(LogLevel.Info, "stratum_api", "Result success")));
        Assert.Null(rules.Match(L(LogLevel.App, "Tuning", "── manuell: 525→550 MHz ──")));
    }

    [Fact]
    public void Invalid_pattern_is_ignored_not_thrown()
    {
        var rules = new LogAlertRules(new LogAlertSettings { Patterns = ["(kaputt", "ok"] });
        Assert.Contains("(kaputt", rules.InvalidPatterns);
        Assert.Equal("ok", rules.Match(L(LogLevel.Info, "x", "alles ok")));
    }

    [Fact]
    public async Task Service_shares_one_connection_and_is_silent_during_maintenance()
    {
        var config = new AppConfig { Notifications = { OnLogAlerts = true } };
        var device = new DeviceConfig { Name = "Gamma", Host = "10.0.0.1", LogAlerts = true };
        config.Devices.Add(device);
        var maintenance = new MaintenanceTracker();
        var sent = new List<Alert>();
        var fake = new FakeLogSource();
        using var connection = new MinerConnection(new FakeClient("10.0.0.1"), maintenance);
        connection.LogSourceOverride = () => fake;
        var hub = connection.Logs;

        using var service = new LogAlertService(() => config, maintenance, sent.Add);
        service.Sync([(device, connection)]);
        using var tab = hub.Subscribe(_ => { });           // Log-Tab zusätzlich geöffnet
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fake.RunCount);                      // nur EINE Verbindung zum Miner

        fake.Emit(L(LogLevel.Warning, "stratum_task", "Stratum connection lost"));
        Assert.Single(sent);
        Assert.Equal(NotifyPriority.Normal, sent[0].Priority);
        fake.Emit(L(LogLevel.Warning, "stratum_task", "Stratum connection lost again"));
        Assert.Single(sent);                                 // gleiche Regel innerhalb der Sperrzeit: nichts Neues

        maintenance.Extend("10.0.0.1", TimeSpan.FromMinutes(5));
        fake.Emit(L(LogLevel.Error, "stratum_task", "Stratum connection lost"));
        Assert.Single(sent);                                 // während Tuning/Neustart still

        device.LogAlerts = false;
        service.Sync([(device, connection)]);
        tab.Dispose();
        Assert.False(hub.IsRunning);                         // niemand liest mehr → Verbindung zu
    }

    private sealed class FakeLogSource : IMinerLogSource
    {
        private Action<LogLine>? _onLine;
        public int RunCount;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogLine line) => _onLine?.Invoke(line);

        public async Task RunAsync(Action<LogLine> onLine, Action<string> onStatus, CancellationToken ct)
        {
            Interlocked.Increment(ref RunCount);
            _onLine = onLine;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        }
    }

    private sealed class FakeClient(string address) : IMinerClient
    {
        public string Address => address;
        public Task<MinerInfo> GetInfoAsync(CancellationToken ct = default) => Task.FromResult(new MinerInfo());
        public Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) => Task.FromResult<AsicInfo?>(null);
        public Task ApplySettingsAsync(int f, int mv, TuningSource s = TuningSource.Manual, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFanAsync(int a, int m, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

public class PoolWatchTests
{
    private static readonly PoolWatchSettings S = new() { RejectPercent = 5, WindowMinutes = 30, MinShares = 20, ResponseMs = 500 };

    private static SystemInfo I(double acc, double rej, int fallback = 0, double ms = 40) => new()
    {
        sharesAccepted = acc, sharesRejected = rej, isUsingFallbackStratum = fallback, responseTime = ms,
        stratumURL = "pool.example", stratumPort = 3333, fallbackStratumURL = "backup.example", fallbackStratumPort = 4444,
    };

    [Fact]
    public void Fallback_switch_is_reported_once_each_way()
    {
        var w = new PoolWatch();
        var t = DateTime.Now;
        Assert.Empty(w.Evaluate("h", "Gamma", I(0, 0), t, S, false));
        var a = w.Evaluate("h", "Gamma", I(1, 0, fallback: 1), t.AddMinutes(1), S, false);
        Assert.Contains(a, x => x.Title.Contains("Fallback"));
        Assert.DoesNotContain(w.Evaluate("h", "Gamma", I(2, 0, fallback: 1), t.AddMinutes(2), S, false), x => x.Title.Contains("Fallback"));
        Assert.Contains(w.Evaluate("h", "Gamma", I(3, 0), t.AddMinutes(3), S, false), x => x.Title.Contains("Primär"));
    }

    [Fact]
    public void Reject_rate_uses_window_and_survives_counter_reset()
    {
        var w = new PoolWatch();
        var t = DateTime.Now;
        w.Evaluate("h", "G", I(1000, 10), t, S, false);
        var alerts = w.Evaluate("h", "G", I(1090, 20), t.AddMinutes(10), S, false); // 10 von 100 abgelehnt
        Assert.Equal(10, w.RejectRate("h", S)!.Value, 3);
        Assert.Contains(alerts, a => a.Title.Contains("abgelehnt"));

        // Neustart: Zähler fallen auf 0 → kein Fehlalarm, Fenster neu
        Assert.DoesNotContain(w.Evaluate("h", "G", I(5, 0), t.AddMinutes(11), S, false), a => a.Title.Contains("abgelehnt"));
        Assert.Null(w.RejectRate("h", S));
    }

    [Fact]
    public void Slow_pool_needs_half_a_window_and_nothing_during_maintenance()
    {
        var w = new PoolWatch();
        var t = DateTime.Now;
        Assert.Empty(w.Evaluate("h", "G", I(0, 0, ms: 900), t, S, false));
        Assert.Empty(w.Evaluate("h", "G", I(1, 0, ms: 900), t.AddMinutes(16), S, inMaintenance: true));
        Assert.Contains(w.Evaluate("h", "G", I(2, 0, ms: 900), t.AddMinutes(17), S, false), a => a.Title.Contains("langsam"));
        Assert.Contains("Antwort Ø 900 ms", w.StatusText("h", I(2, 0, ms: 900), S));
    }
}

public class SnapshotTests
{
    private const string Saved = """
    { "frequency": 525, "coreVoltage": 1150, "overclockEnabled": 0, "autofanspeed": 1, "manualFanSpeed": 100,
      "temptarget": 60, "stratumURL": "pool.a", "stratumPort": 3333, "stratumUser": "addr.w1", "version": "v2.15.3",
      "hashRate": 1000 }
    """;
    private const string Current = """
    { "frequency": 600, "coreVoltage": 1150, "overclockEnabled": 1, "autofanspeed": 1, "manualFanSpeed": 100,
      "temptarget": 65, "stratumURL": "pool.b", "stratumPort": 3333, "stratumUser": "addr.w1", "hashRate": 1200 }
    """;

    [Fact]
    public void Diff_lists_only_changed_restorable_fields_present_on_device()
    {
        using var dir = new TempDir();
        var store = new SettingsSnapshots(dir.Path);
        var snap = store.Save("10.0.0.1", "Gamma", Saved, "manuell");

        var diff = SettingsSnapshots.Diff(snap, Current);

        Assert.Equal(["frequency", "overclockEnabled", "temptarget", "stratumURL"], diff.Select(d => d.Field).ToArray());
        Assert.Equal(525, SettingsSnapshots.ToPatchValue(diff[0].Value));
        Assert.Equal("pool.a", SettingsSnapshots.ToPatchValue(diff[3].Value));
        Assert.DoesNotContain(diff, d => d.Field == "hashRate");
    }

    [Fact]
    public void Saves_lists_newest_first_and_prunes_only_automatic_snapshots()
    {
        using var dir = new TempDir();
        var store = new SettingsSnapshots(dir.Path);
        var t = new DateTime(2026, 9, 25, 10, 0, 0);
        store.Save("sim:bitaxe-gamma", "Sim", Saved, "manuell", t);
        for (var i = 1; i <= 35; i++) store.Save("sim:bitaxe-gamma", "Sim", Saved, "vor Benchmark", t.AddMinutes(i));

        var list = store.List("sim:bitaxe-gamma");
        Assert.Equal(31, list.Count);                         // 30 automatische + 1 manuelle
        Assert.Contains(list, s => s.Reason == "manuell");
        Assert.Equal(t.AddMinutes(35), list[0].TakenAt);
        Assert.Equal("525", list[0].Value("frequency"));
        Assert.Empty(store.List("10.0.0.99"));
    }
}

public class DailyReportTests
{
    [Fact]
    public void Due_once_per_day_after_hour()
    {
        var s = new DailyReportSettings { Enabled = true, Hour = 20 };
        Assert.False(DailyReport.IsDue(s, new DateTime(2026, 9, 25, 19, 59, 0)));
        Assert.True(DailyReport.IsDue(s, new DateTime(2026, 9, 25, 20, 0, 0)));
        s.LastSent = "2026-09-25";
        Assert.False(DailyReport.IsDue(s, new DateTime(2026, 9, 25, 23, 0, 0)));
        Assert.True(DailyReport.IsDue(s, new DateTime(2026, 9, 26, 21, 0, 0)));
        Assert.False(DailyReport.IsDue(new DailyReportSettings { Enabled = false }, DateTime.Now.Date.AddHours(23)));
    }

    [Fact]
    public void Report_contains_totals_costs_and_tuning_changes()
    {
        using var dir = new TempDir();
        using var h = new HistoryStore(dir.File("history.db"));
        var now = new DateTime(2026, 9, 25, 20, 0, 0);
        for (var m = 1; m <= 60; m++) h.AddSample("a", now.AddMinutes(-m), 1200, 55, 20, true);
        h.AddTuningEvent(new TuningEvent("a", now.AddHours(-2), TuningSource.Manual, 525, 1150, 550, 1170, null));
        h.UpdateBestDiff("a", "BCH", 2.07e9, "2.07G");

        var (title, text) = DailyReport.Build(h, new AppConfig { ElectricityCtPerKwh = 30 }, [("Gamma", "a"), ("Weg", "b")], now);

        Assert.Equal("Tagesbericht 25.09.2026", title);
        Assert.Contains("1,20 TH/s", text);
        Assert.Contains("0,48 kWh ≈ 0,14 €", text);            // 20 W × 24 h
        Assert.Contains("16,7 J/TH", text);
        Assert.Contains("1 Tuning-Änderung", text);
        Assert.Contains("2.07G (Gamma", text);
        Assert.Contains("Weg: keine Daten", text);
    }
}
