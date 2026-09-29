using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>Gesundheits-Frühwarnung: Schwellen, Zeiträume aus history.db, tägliche Meldung.</summary>
public class HealthTests
{
    private static HealthWindow W(double? tpw = null, double? temp = null, double? jth = null, double? rpm = null, double? rej = null, double? avail = null) =>
        new(tpw, temp, jth, rpm, rej, avail);

    [Fact]
    public void Clear_changes_are_reported_small_ones_are_not()
    {
        var baseW = W(3.0, 60, 17.0, 100, 0.005, 0.998);
        Assert.Empty(HealthCheck.Evaluate(W(3.1, 61, 17.3, 95, 0.008, 0.99), baseW, tuningChanged: false));

        var bad = HealthCheck.Evaluate(W(3.4, 66, 18.0, 80, 0.02, 0.95), baseW, tuningChanged: false);
        Assert.Equal(["cooling", "efficiency", "fan", "rejects", "availability"], bad.Select(f => f.Code));

        // Effizienz nach Tuning-Änderung nicht vergleichbar
        Assert.DoesNotContain(HealthCheck.Evaluate(W(jth: 18.0), W(jth: 17.0), tuningChanged: true), f => f.Code == "efficiency");
        // Wärmer um 10 %, aber keine 3 °C (kühler Miner) → keine Meldung
        Assert.Empty(HealthCheck.Evaluate(W(1.1, 22), W(1.0, 20), false));
        // Ohne Vergleichswerte keine Aussage
        Assert.Empty(HealthCheck.Evaluate(W(3.4, 66, 18, 80, 0.02, 0.95), W(), false));
    }

    [Fact]
    public void Windows_are_built_from_history_and_counter_resets_are_ignored()
    {
        using var dir = new TempDir();
        using var db = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        var now = new DateTime(2026, 9, 29, 12, 0, 0);
        double acc = 0, rej = 0;
        db.AddSamples("a", Enumerable.Range(1, 35 * 1440).Select(m => (now.AddMinutes(-m), 1000.0, m <= 7 * 1440 ? 67.0 : 60.0, 20.0, true)));
        for (var m = 35 * 1440; m > 0; m -= 10)
        {
            var t = now.AddMinutes(-m);
            var recent = m <= 7 * 1440;
            if (m == 3 * 1440) acc = rej = 0;                             // Neustart: Zähler zurück
            acc += 100;
            rej += recent ? 3 : 0.5;
            db.AddHealthSample("a", t, recent ? 4000 : 5000, 50, 60, acc, rej);
        }
        var (r, b) = HealthCheck.Windows(db, "a", now);
        Assert.Equal(3.35, r.TempPerWatt!.Value, 3);
        Assert.Equal(3.0, b.TempPerWatt!.Value, 3);
        Assert.Equal(80, r.RpmPerPercent!.Value, 3);
        Assert.Equal(100, b.RpmPerPercent!.Value, 3);
        Assert.Equal(3 / 103.0, r.RejectShare!.Value, 3);
        Assert.Equal(1.0, r.Availability!.Value, 6);
        Assert.Equal(["cooling", "fan", "rejects"], HealthCheck.Evaluate(r, b, false).Select(f => f.Code));
    }

    [Fact]
    public void Hub_reports_once_per_day_after_nine_and_respects_setting()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 9, 29, 8, 0, 0);
        var config = new AppConfig();
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "t";
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.5.1" });
        var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, Clock = () => now });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        try
        {
            hub.History!.AddSamples("10.0.5.1", Enumerable.Range(1, 35 * 1440).Select(m => (now.AddMinutes(-m), 1000.0, m <= 7 * 1440 ? 67.0 : 60.0, 20.0, true)));
            Assert.Equal("cooling", Assert.Single(hub.HealthOf("10.0.5.1", now).Findings).Code);

            hub.CheckHealthWarningsForTest(now);                         // vor 9 Uhr: nichts
            Assert.Empty(sent);
            now = now.AddHours(2);
            hub.CheckHealthWarningsForTest(now);
            hub.CheckHealthWarningsForTest(now.AddHours(1));             // am selben Tag nur einmal
            Assert.Equal(["health:10.0.5.1:cooling"], sent);

            hub.Config.Notifications.OnHealth = false;
            hub.CheckHealthWarningsForTest(now.AddDays(1));
            Assert.Single(sent);
        }
        finally
        {
            hub.Dispose();
        }
    }
}
