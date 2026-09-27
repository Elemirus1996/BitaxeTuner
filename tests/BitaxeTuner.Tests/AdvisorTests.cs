using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using Microsoft.Data.Sqlite;

namespace BitaxeTuner.Tests;

/// <summary>Effizienz-Ratgeber, Dauertest-Ergebnisse in history.db, Dauertest nach bestätigter Änderung.</summary>
public class AdvisorTests
{
    private static DeviceProfile Gamma => ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");

    private static StepResult R(int f, int mv, double gh, double w, bool stable = true, int minutesAgo = 60) => new()
    {
        FrequencyMhz = f, CoreVoltageMv = mv, AvgHashRateGh = gh, AvgPowerW = w,
        Outcome = stable ? StepOutcome.Stable : StepOutcome.Unstable, Timestamp = DateTime.Now.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void Picks_best_setting_per_goal_within_limits_and_computes_savings()
    {
        var p = Gamma;
        var results = new[]
        {
            R(525, 1150, 1100, 18.0),                  // 16,4 J/TH – effizienteste
            R(600, 1200, 1250, 22.5),                  // 18,0 J/TH
            R(700, 1300, 1450, 29.0),                  // 20,0 J/TH – meiste Hashrate
            R(p.MaxFrequencyMhz + 100, p.MaxVoltageMv, 1900, 35, true),   // außerhalb der Profilgrenzen
            R(650, 1250, 1350, 20.0, stable: false),   // instabil
        };
        var soaks = new[] { new SoakResultRecord("h", DateTime.Now.AddDays(-2), DateTime.Now.AddDays(-1), 525, 1150, true, 0.99, "ok") };

        var r = EfficiencyAdvisor.Evaluate("h", "Gamma", p, 600, 1200, 1250, 22.5, "Ø 24 h", results, soaks, ctPerKwh: 30, AdvisorGoal.Efficiency);
        var c = r.Recommended!;
        Assert.Equal((525, 1150), (c.FrequencyMhz, c.CoreVoltageMv));
        Assert.True(c.SoakPassed);
        Assert.Equal(-4.5, c.DeltaW, 3);
        Assert.Equal(-4.5 * 24 * 30 / 1000.0 * 0.30, c.MonthlyCostDelta, 3);             // ≈ −0,97 pro Monat
        Assert.Equal((700, 1300), r.Candidates.Single(x => x.Goal == AdvisorGoal.Hashrate) is var h ? (h.FrequencyMhz, h.CoreVoltageMv) : default);
        Assert.DoesNotContain(r.Candidates, x => x.FrequencyMhz > p.MaxFrequencyMhz || x.FrequencyMhz == 650);

        // Schon die beste Einstellung → kein Vorschlag
        var same = EfficiencyAdvisor.Evaluate("h", "Gamma", p, 525, 1150, 1100, 18.0, "aktuell", results, soaks, 30, AdvisorGoal.Efficiency);
        Assert.Null(same.Recommended);
        Assert.Contains("bereits die beste", same.Note);

        // Kaum besser (< 3 %) → lohnt nicht
        var close = EfficiencyAdvisor.Evaluate("h", "Gamma", p, 540, 1150, 1120, 18.6, "aktuell", results, soaks, 30, AdvisorGoal.Efficiency);
        Assert.Null(close.Recommended);
        Assert.Contains("lohnt nicht", close.Note);

        // Im Dauertest durchgefallen → nie vorgeschlagen
        var failed = soaks.Append(new SoakResultRecord("h", DateTime.Now.AddHours(-5), DateTime.Now, 525, 1150, false, 0.8, "fehlgeschlagen")).ToArray();
        var r2 = EfficiencyAdvisor.Evaluate("h", "Gamma", p, 700, 1300, 1450, 29, "aktuell", results, failed, 30, AdvisorGoal.Efficiency);
        Assert.Equal((600, 1200), (r2.Recommended!.FrequencyMhz, r2.Recommended.CoreVoltageMv));
        Assert.False(r2.Recommended.SoakPassed);

        Assert.Contains("Benchmark", EfficiencyAdvisor.Evaluate("h", "G", p, 600, 1200, 1000, 20, "aktuell", [], [], 30, AdvisorGoal.Balanced).Note);
    }

    [Fact]
    public void Soak_results_table_is_added_to_an_existing_history_without_touching_data()
    {
        using var dir = new TempDir();
        var file = dir.File("history.db");
        // Alter Stand (0.3.x): nur die bisherigen Tabellen mit Daten
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE samples (host TEXT NOT NULL, ts INTEGER NOT NULL, hashrate REAL NOT NULL, temp REAL NOT NULL, power REAL NOT NULL, online INTEGER NOT NULL, PRIMARY KEY (host, ts));
                INSERT INTO samples VALUES ('10.0.0.1', 1700000000, 1000, 55, 15, 1), ('10.0.0.1', 1700000060, 1010, 55, 15, 1);
                """;
            cmd.ExecuteNonQuery();
        }
        using (var h = new HistoryStore(file))
        {
            h.AddSoakResult(new SoakResultRecord("10.0.0.1", new DateTime(2026, 9, 1, 10, 0, 0), new DateTime(2026, 9, 2, 10, 0, 0), 575, 1150, true, 0.985, "bestanden"));
            var s = Assert.Single(h.QuerySoakResults("10.0.0.1"));
            Assert.Equal((575, 1150, true, 0.985), (s.FrequencyMhz, s.CoreVoltageMv, s.Passed, s.Ratio!.Value));
            Assert.Equal(2, h.Query("10.0.0.1", DateTimeOffset.FromUnixTimeSeconds(1699999000).LocalDateTime, DateTimeOffset.FromUnixTimeSeconds(1700001000).LocalDateTime).Count);
        }
        SqliteConnection.ClearAllPools();
        var (ok, rows) = HistoryStore.Verify(file);
        Assert.True(ok);
        Assert.Equal(2, rows["samples"]);
    }

    [Fact]
    public async Task Soak_after_confirmed_change_starts_when_miner_is_back()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.4.1" });
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(Gamma, 1, h),
        });
        await hub.PollNowAsync();
        var d = hub.Devices[0];
        Assert.Throws<InvalidOperationException>(() => hub.ScheduleSoak(d, 0));
        hub.ScheduleSoak(d, 24);
        Assert.False(d.SoakActive);
        await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
        await hub.PollNowAsync();
        Assert.True(d.SoakActive);
        Assert.Null(d.PendingSoak);
        Assert.Contains(d.LogLines, l => l.Contains("Dauertest (24 h) startet"));
    }
}
