using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>
/// Der Headless-Motor (Desktop „Lokal“ und Server): Abfrage, Verlauf, Meldungen, Wartungsfenster,
/// Grenzprüfung, Benchmark und Dauertest ohne Oberfläche – mit simulierten Minern unter LAN-Adressen.
/// </summary>
public class HubTests
{
    private static DeviceProfile Gamma => ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Dictionary<string, SimulatedMinerClient> Sims = new();
        public readonly List<(string Key, string Title)> Sent = new();
        public readonly MinerHub Hub;

        public DateTime Now = DateTime.Now;

        public Rig(params string[] hosts)
        {
            var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "test" } };
            foreach (var h in hosts) config.Devices.Add(new DeviceConfig { Name = "Miner " + h, Host = h });
            Hub = new MinerHub(config, new MinerHubOptions
            {
                DataDirectory = Dir.Path,
                OnlineChecks = false,
                ClientFactory = h => Sims[h] = new SimulatedMinerClient(Gamma, seed: h.Length, address: h),
                Clock = () => Now,
                BenchmarkDelay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            });
            Hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
            Hub.Notify.Sending += (key, title, _, _) => { lock (Sent) Sent.Add((key, title)); };
        }

        public HubDevice Device(string host) => Hub.Device(host)!;

        public void Dispose()
        {
            Hub.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task Daily_and_monthly_report_are_tailored_per_push_target()
    {
        using var rig = new Rig("10.0.0.11", "10.0.0.12");
        var n = rig.Hub.Config.Notifications;
        var cats = new List<string> { "DailyReport", "MonthlyReport" };
        n.Targets =
        [
            new PushTarget { Id = "privat01", Name = "Privat", NtfyTopic = "p", Categories = cats },
            new PushTarget { Id = "commun01", Name = "Community", NtfyTopic = "c", Categories = cats, Miners = ["10.0.0.12"],
                ReportExclude = [ReportParts.Costs, ReportParts.Tips] },
        ];
        var sent = new Dictionary<string, List<string>>();
        rig.Hub.Notify.Delivered += (id, _, text) => { lock (sent) (sent.TryGetValue(id, out var l) ? l : sent[id] = []).Add(text); };
        Assert.True(await rig.Hub.PollNowAsync());
        var now = DateTime.Now;
        for (var m = 600; m > 0; m -= 5)
        {
            rig.Hub.History!.AddSample("10.0.0.11", now.AddMinutes(-m), 1000, 55, 15, true);
            rig.Hub.History.AddSample("10.0.0.12", now.AddMinutes(-m), 600, 60, 14, true);
        }

        Assert.Null(await rig.Hub.SendDailyReportAsync(now, markSent: false));
        var privat = Assert.Single(sent["privat01"]);
        var community = Assert.Single(sent["commun01"]);
        Assert.Contains("Miner 10.0.0.11", privat);
        Assert.Contains("kWh", privat);
        Assert.Contains("Miner 10.0.0.12", community);
        Assert.DoesNotContain("Miner 10.0.0.11", community);              // nur die eigenen Miner
        Assert.DoesNotContain("kWh", community);                          // Stromkosten abgewählt

        sent.Clear();
        var period = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(await rig.Hub.SendMonthlyReportAsync(period, now, markSent: false));
        Assert.Contains("Miner 10.0.0.11", Assert.Single(sent["privat01"]));
        var monthly = Assert.Single(sent["commun01"]);
        Assert.DoesNotContain("Miner 10.0.0.11", monthly);
        Assert.DoesNotContain("kWh", monthly);
        Assert.False(n.Targets[1].ReportIncludes(ReportParts.Income));    // mit Miner-Auswahl nie Zuflüsse
    }

    [Fact]
    public async Task Journal_keeps_tuning_offline_and_server_events_in_history_db()
    {
        using var rig = new Rig("10.0.0.41", "10.0.0.42");
        await rig.Hub.StartAsync();
        var d = rig.Device("10.0.0.41");
        await rig.Hub.ApplyChangeAsync(d, 500, 1150);

        rig.Sims["10.0.0.42"].Offline = true;
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
            await rig.Hub.PollNowAsync();
        }
        rig.Sims["10.0.0.42"].Offline = false;
        await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
        await rig.Hub.PollNowAsync();
        d.AddLog("nur Sitzung", category: null);

        var now = DateTime.Now;
        var all = rig.Hub.History!.QueryEvents(now.AddHours(-1), now.AddMinutes(1));
        var tuning = Assert.Single(all, e => e.Category == EventCategories.Tuning);
        Assert.Equal("10.0.0.41", tuning.Host);
        Assert.Contains("500 MHz / 1150 mV", tuning.Message);
        Assert.Contains("manuell", tuning.Message);                                       // Quelle
        Assert.Equal(2, all.Count(e => e.Category == EventCategories.Connection && e.Host == "10.0.0.42"));   // offline + wieder online
        Assert.Contains(all, e => e.Category == EventCategories.System && e.Host is null);                     // Überwachung gestartet
        Assert.DoesNotContain(all, e => e.Message == "nur Sitzung");

        // Filter: nur ein Miner, nur Kategorie, nur Server, Suchtext
        Assert.All(rig.Hub.History.QueryEvents(now.AddHours(-1), now.AddMinutes(1), host: "10.0.0.42"), e => Assert.Equal("10.0.0.42", e.Host));
        Assert.Single(rig.Hub.History.QueryEvents(now.AddHours(-1), now.AddMinutes(1), categories: [EventCategories.Tuning]));
        Assert.All(rig.Hub.History.QueryEvents(now.AddHours(-1), now.AddMinutes(1), host: ""), e => Assert.Null(e.Host));
        Assert.Single(rig.Hub.History.QueryEvents(now.AddHours(-1), now.AddMinutes(1), text: "1150 mV"));
        Assert.Empty(rig.Hub.History.QueryEvents(now.AddHours(-1), now.AddMinutes(1), text: "%"));   // Platzhalter wörtlich
    }

    [Fact]
    public void Journal_survives_reopening_and_is_kept_at_least_30_days()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "history.db");
        var now = DateTime.Now;
        using (var h = new HistoryStore(file))
        {
            h.AddEvent("10.0.0.5", EventCategories.Benchmark, "alt (40 Tage)", now.AddDays(-40));
            h.AddEvent("10.0.0.5", EventCategories.Benchmark, "20 Tage", now.AddDays(-20));
            h.AddEvent(null, EventCategories.System, "heute", now);
        }
        using (var h = new HistoryStore(file))          // wie nach einem Neustart/Update
        {
            Assert.Equal(3, h.QueryEvents(now.AddDays(-60), now.AddMinutes(1)).Count);
            h.Prune(7);                                  // Verlauf nur 7 Tage – Protokoll trotzdem 30
            var left = h.QueryEvents(now.AddDays(-60), now.AddMinutes(1));
            Assert.Equal(["heute", "20 Tage"], left.Select(e => e.Message));
        }
    }

    [Fact]
    public async Task Compare_report_shows_selected_values_charts_and_no_addresses()
    {
        using var rig = new Rig("10.0.0.11", "10.0.0.12");
        Assert.True(await rig.Hub.PollNowAsync());
        var h = rig.Hub.History!;
        var now = DateTime.Now;
        // Miner .12 läuft bei gleicher Leistung 8 °C heißer, VR und Lüfter ebenso höher
        for (var m = 120; m > 0; m -= 5)
        {
            h.AddSample("10.0.0.11", now.AddMinutes(-m), 1000, 55, 15, true);
            h.AddSample("10.0.0.12", now.AddMinutes(-m), 1000, 63, 15, true);
        }
        for (var m = 120; m > 0; m -= 10)
        {
            h.AddHealthSample("10.0.0.11", now.AddMinutes(-m), 4000, 50, 60, 100, 0);
            h.AddHealthSample("10.0.0.12", now.AddMinutes(-m), 5200, 75, 72, 100, 0);
        }

        var devices = new[] { rig.Device("10.0.0.11"), rig.Device("10.0.0.12") };
        var r = Core.Reports.CompareReports.Build(rig.Hub, devices, "24h", Core.Reports.CompareReports.CoolingValues,
            Core.Reports.CompareReports.CoolingCharts, now);

        Assert.Equal(["Miner 10.0.0.11", "Miner 10.0.0.12"], r.Miners);
        var avgTemp = r.Rows.Single(x => x.Label == "Ø Temperatur");
        Assert.Equal([true, false], avgTemp.Best);                         // kühler = besser
        var perWatt = r.Rows.Single(x => x.Label == "Ø Temperatur je Watt");
        Assert.Contains("°C/W", perWatt.Cells[0]);
        Assert.Equal([true, false], r.Rows.Single(x => x.Label == "Ø VR-Temperatur").Best);   // .12 hat den heißeren Spannungswandler
        Assert.Equal(["Leistung", "ASIC-Temperatur", "VR-Temperatur", "Lüfter"], r.Charts.Select(c => c.Title));   // Reihenfolge des Katalogs
        Assert.All(r.Charts, c => Assert.All(c.Series, x => Assert.NotEmpty(x.Points)));

        var html = Core.Reports.CompareReports.Html(r);
        Assert.Contains("<svg", html);
        Assert.Contains("Miner 10.0.0.12", html);                         // Name (hier zufällig mit Adresse) ja …
        var withoutNames = html.Replace("Miner 10.0.0.11", "").Replace("Miner 10.0.0.12", "");
        Assert.DoesNotContain("10.0.0.1", withoutNames);                  // … die Adresse selbst nie
        Assert.DoesNotContain("<script", html);                           // Content-Security-Policy

        Assert.Throws<LocalizedException>(() => Core.Reports.CompareReports.Build(rig.Hub, [], "24h", [], [], now));
    }

    [Fact]
    public async Task Poll_updates_states_writes_history_and_resolves_profile()
    {
        using var rig = new Rig("10.0.0.11", "10.0.0.12");
        var polled = 0;
        rig.Hub.Polled += () => polled++;

        Assert.True(await rig.Hub.PollNowAsync());
        await Task.Delay(50); // Profilerkennung läuft nach der Runde weiter

        Assert.Equal(1, polled);
        Assert.All(rig.Hub.Devices, d => Assert.True(d.State.Online));
        Assert.Single(rig.Hub.AggregateHistory);
        var now = DateTime.Now;
        Assert.Single(rig.Hub.History!.Query("10.0.0.11", now.AddMinutes(-1), now.AddMinutes(1)));
        Assert.Single(rig.Hub.History.Query(HistoryStore.AggregateHost, now.AddMinutes(-1), now.AddMinutes(1)));

        var d = rig.Device("10.0.0.11");
        Assert.True(d.ProfileResolved);
        Assert.Equal("bitaxe-gamma", d.Profile.Id);
        Assert.Contains(d.LogLines, l => l.Contains("Erkannt"));
    }

    [Fact]
    public async Task Offline_is_reported_after_three_failures_but_not_during_maintenance()
    {
        using var rig = new Rig("10.0.0.21", "10.0.0.22");
        await rig.Hub.PollNowAsync();
        rig.Sims["10.0.0.21"].Offline = true;
        rig.Sims["10.0.0.22"].Offline = true;
        // Gewollter Neustart des zweiten Miners (Tuning/Benchmark): Wartungsfenster
        using var maintenance = rig.Device("10.0.0.22").Connection.BeginMaintenance("Test");

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100)); // Verbindungscache umgehen
            await rig.Hub.PollNowAsync();
        }

        Assert.Contains(rig.Sent, s => s.Key == "offline:10.0.0.21");
        Assert.DoesNotContain(rig.Sent, s => s.Key == "offline:10.0.0.22");

        rig.Sims["10.0.0.21"].Offline = false;
        await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
        await rig.Hub.PollNowAsync();
        Assert.Contains(rig.Sent, s => s.Key == "online:10.0.0.21");
    }

    [Fact]
    public async Task Manual_change_checks_profile_limits_and_is_logged_in_history()
    {
        using var rig = new Rig("10.0.0.31");
        await rig.Hub.PollNowAsync();
        await Task.Delay(50);
        var d = rig.Device("10.0.0.31");
        var p = d.Profile;

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Hub.PreviewChangeAsync(d, p.MaxFrequencyMhz + 50, p.DefaultVoltageMv));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Hub.ApplyChangeAsync(d, p.DefaultFrequencyMhz, p.MaxVoltageMv + 10));

        var target = p.DefaultFrequencyMhz + 25;
        var preview = await rig.Hub.PreviewChangeAsync(d, target, p.DefaultVoltageMv);
        Assert.Contains($"→  {target} MHz", preview.ConfirmText);
        Assert.Contains($"{p.DefaultFrequencyMhz} MHz", preview.ConfirmText); // alter Wert steht im Dialog

        await rig.Hub.ApplyChangeAsync(d, target, p.DefaultVoltageMv);
        Assert.Equal((target, p.DefaultVoltageMv), rig.Sims["10.0.0.31"].CurrentSettings);

        var events = rig.Hub.History!.QueryTuningEvents("10.0.0.31", DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        var e = Assert.Single(events);
        Assert.Equal((TuningSource.Manual, p.DefaultFrequencyMhz, target), (e.Source, e.OldFrequencyMhz, e.NewFrequencyMhz));
        Assert.Empty(rig.Hub.Comparisons(d)); // Vorher/Nachher nur für echte Miner (Simulation schreibt keinen Verlauf)
    }

    [Fact]
    public async Task Benchmark_runs_in_hub_saves_results_and_rejects_out_of_range_search()
    {
        using var rig = new Rig("10.0.0.41");
        await rig.Hub.PollNowAsync();
        await Task.Delay(50);
        var d = rig.Device("10.0.0.41");

        var tooHigh = BenchmarkSettings.FromProfile(d.Profile);
        tooHigh.MaxFrequencyMhz = d.Profile.MaxFrequencyMhz + 100;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Hub.Benchmarks.PrepareAsync(d, tooHigh, resume: false));

        var s = BenchmarkSettings.FromProfile(d.Profile);
        s.WarmupSeconds = 30;
        s.MeasureSeconds = 150;
        s.SampleIntervalSeconds = 15;
        s.MinSamples = 7;
        var plan = await rig.Hub.Benchmarks.PrepareAsync(d, s, resume: false);
        Assert.Contains("Benchmark starten", plan.ConfirmText);

        var states = new List<bool>();
        rig.Hub.Benchmarks.StateChanged += dev => states.Add(dev.IsBenchmarkRunning);
        await rig.Hub.Benchmarks.RunAsync(d, plan);

        Assert.Equal([true, false], states);
        Assert.False(rig.Hub.Benchmarks.AnyRunning);
        Assert.Equal("Fertig", d.Benchmark!.PhaseText);
        Assert.True(d.Benchmark.Session.IsFinished);
        Assert.Contains(d.Benchmark.Session.Results, r => r.IsStable);
        // Ergebnis liegt im Datenordner des Hubs und wird beim nächsten Start gefunden
        Assert.NotNull(rig.Hub.Results.LoadLatest("10.0.0.41", null));
        Assert.True(Directory.EnumerateFiles(rig.Dir.File("tuning"), "*.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Soak_start_and_stop_are_persisted_in_the_hub_config_file()
    {
        using var rig = new Rig("10.0.0.51");
        await rig.Hub.PollNowAsync();
        var d = rig.Device("10.0.0.51");

        Assert.Contains("Dauertest", rig.Hub.SoakConfirmText(d, 12));
        rig.Hub.StartSoak(d, 12);
        Assert.True(d.SoakActive);
        Assert.Equal(rig.Dir.File("config.json"), rig.Hub.Config.FilePath);
        Assert.NotNull(AppConfig.Load(rig.Dir.File("config.json")).Devices[0].Soak);

        rig.Hub.StopSoak(d);
        Assert.False(d.SoakActive);
        Assert.Null(AppConfig.Load(rig.Dir.File("config.json")).Devices[0].Soak);
    }

    [Fact]
    public async Task Soak_batch_starts_selected_miners_skips_unsuitable_and_stops_all()
    {
        using var rig = new Rig("10.0.0.61", "10.0.0.62", "10.0.0.63");
        await rig.Hub.PollNowAsync();
        rig.Sims["10.0.0.63"].Offline = true;
        await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100)); // Verbindungscache umgehen
        await rig.Hub.PollNowAsync();
        rig.Hub.StartSoak(rig.Device("10.0.0.62"), 6);                  // läuft schon

        var preview = rig.Hub.SoakBatchPreview();
        Assert.True(preview.Single(e => e.Device.Host == "10.0.0.61").Eligible);
        Assert.Equal("Dauertest läuft bereits", preview.Single(e => e.Device.Host == "10.0.0.62").Reason);
        Assert.Equal("nicht erreichbar", preview.Single(e => e.Device.Host == "10.0.0.63").Reason);
        var info = rig.Device("10.0.0.61").Info!;
        Assert.Equal((info.FrequencyMhz, info.CoreVoltageMv), (preview[0].FrequencyMhz!.Value, preview[0].CoreVoltageMv!.Value));

        var results = rig.Hub.StartSoakBatch(["10.0.0.61", "10.0.0.62", "10.0.0.63"], 24);
        Assert.Equal([true, false, false], results.Select(r => r.Started));
        Assert.True(rig.Device("10.0.0.61").SoakActive);
        Assert.Equal(TimeSpan.FromHours(24), rig.Device("10.0.0.61").Config.Soak!.Until - rig.Device("10.0.0.61").Config.Soak!.StartedAt);
        Assert.Equal(TimeSpan.FromHours(6), rig.Device("10.0.0.62").Config.Soak!.Until - rig.Device("10.0.0.62").Config.Soak!.StartedAt);
        Assert.Throws<InvalidOperationException>(() => rig.Hub.StartSoakBatch(["10.0.0.61"], 500));
        Assert.Throws<InvalidOperationException>(() => rig.Hub.StartSoakBatch([], 24));

        Assert.Equal(2, rig.Hub.StopAllSoaks());
        Assert.All(rig.Hub.Devices, d => Assert.False(d.SoakActive));
        Assert.All(AppConfig.Load(rig.Dir.File("config.json")).Devices, d => Assert.Null(d.Soak));
    }

    [Fact]
    public async Task Thermal_guard_only_acts_after_approval_and_logs_change()
    {
        using var rig = new Rig("10.0.0.61");
        await rig.Hub.PollNowAsync();
        await Task.Delay(50);
        var d = rig.Device("10.0.0.61");
        var guard = d.Config.ThermalGuard;
        guard.Enabled = true;
        guard.MaxChipTempC = 50;
        guard.Minutes = 0;
        rig.Sims["10.0.0.61"].ExtraTempC = 30;
        var before = rig.Sims["10.0.0.61"].CurrentSettings;

        await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
        await rig.Hub.PollNowAsync();
        await Task.Delay(100);
        Assert.Equal(before, rig.Sims["10.0.0.61"].CurrentSettings); // ohne Freigabe keine Änderung

        Assert.Contains("freigeben", MinerHub.ThermalGuardApprovalText(d));
        rig.Hub.ApproveRule(d, guard, "Temperaturschutz");
        for (var i = 0; i < 2; i++)
        {
            rig.Now = rig.Now.AddMinutes(2); // Haltezeit (mind. 1 min über der Grenze) verstreicht
            await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
            await rig.Hub.PollNowAsync();
        }
        await Task.Delay(200);

        Assert.True(rig.Sims["10.0.0.61"].CurrentSettings.Frequency < before.Frequency);
        var e = Assert.Single(rig.Hub.History!.QueryTuningEvents("10.0.0.61", DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1)));
        Assert.Equal(TuningSource.Automatic, e.Source);
        Assert.Contains(d.LogLines, l => l.Contains("Automatik:"));
    }

    [Fact]
    public async Task Removed_device_disappears_from_hub()
    {
        using var rig = new Rig("10.0.0.71", "10.0.0.72");
        var changed = 0;
        rig.Hub.DevicesChanged += () => changed++;
        rig.Hub.Config.Devices.RemoveAt(0);
        rig.Hub.SyncDevices();
        await rig.Hub.PollNowAsync();

        Assert.Equal(1, changed);
        Assert.Null(rig.Hub.Device("10.0.0.71"));
        Assert.Equal(["10.0.0.72"], rig.Hub.Devices.Select(x => x.Host));
    }

    [Fact]
    public async Task Hub_thread_serializes_calls_from_other_threads()
    {
        using var thread = new HubThread();
        using var rig = new Rig("10.0.0.81");
        var hubThreadId = await thread.RunAsync(() => Environment.CurrentManagedThreadId);

        // Start im Hub-Kontext: danach laufen Aufrufe von außen über InvokeAsync auf diesem Thread
        await thread.RunAsync(async () =>
        {
            SynchronizationContext.Current!.Post(_ => { }, null);
            await rig.Hub.PollNowAsync();
            return true;
        });
        var ids = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => thread.RunAsync(async () =>
        {
            await rig.Hub.PollNowAsync();
            return Environment.CurrentManagedThreadId;
        }))));
        Assert.All(ids, id => Assert.Equal(hubThreadId, id));
    }
}
