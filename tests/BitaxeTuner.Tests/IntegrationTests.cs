using System.Text.Json;
using System.Text.Json.Nodes;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Tests;

/// <summary>Temporärer Ordner, der nach dem Test gelöscht wird.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bt-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch { /* egal */ }
    }
}

public class AxeOsParsingTests
{
    private static JsonElement Fixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "axeos-2.15.3-gamma.json"))).RootElement;

    [Fact]
    public void Real_v2_15_3_response_fills_both_models()
    {
        var info = AxeOsClient.Parse(Fixture());

        Assert.Equal(FirmwareKind.AxeOS, info.Firmware);
        Assert.Equal("BM1370", info.AsicModel);
        Assert.True(info.FrequencyMhz > 0);
        Assert.True(info.CoreVoltageMv > 0);
        Assert.NotNull(info.ErrorPercent);
        Assert.NotNull(info.ExpectedHashRateGh);
        Assert.NotNull(info.OverclockEnabled);
        Assert.Equal(1260, info.PoolDifficulty);

        // Roh-DTO für die Überwachung aus derselben Antwort
        var d = Assert.IsType<SystemInfo>(info.Details);
        Assert.Equal(info.FrequencyMhz, (int)d.frequency);
        Assert.Equal(info.HashRateGh, d.hashRate);
        Assert.Equal("603", d.boardVersion);
        Assert.False(string.IsNullOrEmpty(d.bestDiff));
        Assert.StartsWith("bitcoincash:", d.stratumUser);
    }

    [Fact]
    public void Real_gamma_board_603_matches_gamma_profile()
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        Assert.Equal("bitaxe-gamma", registry.Match(AxeOsClient.Parse(Fixture())).Id);
    }
}

public class ConfigAndDataTests
{
    /// <summary>Alle Felder einer config.json aus BitaxeMonitor (Werte synthetisch).</summary>
    private const string MonitorConfig = """
    {
      "Devices": [
        { "Name": "Miner A", "Host": "10.0.0.182", "WalletAddress": "", "Coin": "BCH", "FirmwareRepo": "bitaxeorg/ESP-Miner" },
        { "Name": "Miner B", "Host": "10.0.0.187", "WalletAddress": "qabc", "Coin": "Auto", "FirmwareRepo": "" }
      ],
      "IntervalSeconds": 5, "HistoryMinutes": 60, "WalletPollMinutes": 10, "ElectricityCtPerKwh": 31.5,
      "TaxPollMinutes": 15, "BlockchairApiKey": "k", "HistoryDays": 90, "TempWarn": 70, "MinimizeToTray": true,
      "Notifications": { "Provider": "ntfy", "NtfyServer": "https://ntfy.sh", "NtfyTopic": "t", "TelegramBotToken": "",
                         "TelegramChatId": "", "OnOffline": true, "OnOverheat": true, "OnFinds": true, "OnMaintenance": true, "OnRecord": false },
      "Watchdog": { "Enabled": true, "ZeroHashMinutes": 10, "CooldownMinutes": 60 },
      "NotifiedFirmware": { "bitaxeorg/ESP-Miner": "v2.15.3" },
      "CoinGeckoApiKey": "", "Currency": "€", "StartMinimized": false,
      "LastSeenPayoutTxids": { "qabc": "txid1" }
    }
    """;

    [Fact]
    public void Monitor_config_keeps_every_existing_value_after_load_and_save()
    {
        using var dir = new TempDir();
        var file = dir.File("config.json");
        File.WriteAllText(file, MonitorConfig);

        AppConfig.Load(file).Save(file);

        var before = JsonNode.Parse(MonitorConfig)!.AsObject();
        var after = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        // Tokens stehen jetzt als Verweis in config.json (Audit P2) – aufgelöst muss jeder Wert noch da sein
        Assert.Empty(ConfigSecrets.Resolve(after, new SecretStore(dir.Path)));
        AssertContainsAll(before, after, "");
    }

    /// <summary>Jedes alte Feld (auch verschachtelt) muss unverändert vorhanden sein; neue Felder sind erlaubt.</summary>
    private static void AssertContainsAll(JsonObject before, JsonObject after, string path)
    {
        foreach (var (key, value) in before)
        {
            Assert.True(after.ContainsKey(key), $"Feld fehlt nach dem Speichern: {path}{key}");
            if (value is JsonObject inner && after[key] is JsonObject innerAfter)
                AssertContainsAll(inner, innerAfter, path + key + ".");
            else if (value is JsonArray arr && after[key] is JsonArray arrAfter && arr.All(e => e is JsonObject))
            {
                Assert.Equal(arr.Count, arrAfter.Count);
                for (var i = 0; i < arr.Count; i++)
                    AssertContainsAll((JsonObject)arr[i]!, (JsonObject)arrAfter[i]!, $"{path}{key}[{i}].");
            }
            else
                Assert.True(JsonNode.DeepEquals(value, after[key]), $"Feld verändert: {path}{key}");
        }
    }

    [Fact]
    public void Tuner_migration_adds_only_new_hosts_and_never_overwrites()
    {
        using var legacy = new TempDir();
        using var data = new TempDir();
        File.WriteAllText(legacy.File("settings.json"),
            """{ "DataDirectory": null, "DeviceAddresses": ["10.0.0.182", "10.0.0.200"], "WarningAccepted": true, "CheckForUpdates": false }""");
        Directory.CreateDirectory(legacy.File("results"));
        File.WriteAllText(legacy.File("results", "a.json"), "neu");
        File.WriteAllText(legacy.File("results", "b.json"), "neu");
        File.WriteAllText(legacy.File("profiles.json"), "[]");
        Directory.CreateDirectory(data.File("results"));
        File.WriteAllText(data.File("results", "b.json"), "vorhanden");

        var config = JsonSerializer.Deserialize<AppConfig>(MonitorConfig)!;
        var log = ConfigMigrator.MigrateTunerData(config, legacy.Path, data.Path);

        Assert.Equal(3, config.Devices.Count);
        Assert.Contains(config.Devices, d => d.Host == "10.0.0.200");
        Assert.True(config.WarningAccepted);
        Assert.True(config.TunerDataMigrated);
        Assert.Equal("neu", File.ReadAllText(data.File("results", "a.json")));
        Assert.Equal("vorhanden", File.ReadAllText(data.File("results", "b.json")));
        Assert.True(File.Exists(legacy.File("results", "a.json")), "Original muss erhalten bleiben");
        Assert.Contains(log, l => l.StartsWith("Übersprungen"));

        Assert.Empty(ConfigMigrator.MigrateTunerData(config, legacy.Path, data.Path)); // nur einmal
    }

    [Fact]
    public void Tax_files_round_trip_unchanged()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        var rewards = new List<MinedReward>
        {
            new() { WalletAddressId = "w1", WalletLabel = "Gamma 603", Coin = CoinType.BitcoinCash, TxId = "abc",
                    ReceivedAtUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), Amount = 0.125m, BlockHeight = 900000,
                    EurPriceAtReceipt = 312.5m, PriceSource = "manuell eingetragen am 01.05.2026", Note = "Test" },
        };
        var disposals = new List<Disposal>
        {
            new() { Coin = CoinType.BitcoinCash, SoldAtUtc = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), Amount = 0.1m, ProceedsEur = 40m, Note = "Börse" },
        };
        repo.SaveRewards(rewards);
        repo.SaveDisposals(disposals);
        var rewardsJson = File.ReadAllText(dir.File("rewards.json"));
        var disposalsJson = File.ReadAllText(dir.File("disposals.json"));

        // Enum als Name (nicht Zahl) – wie im BitaxeMonitor
        Assert.Contains("\"Coin\": \"BitcoinCash\"", rewardsJson);

        repo.SaveRewards(repo.LoadRewards());
        repo.SaveDisposals(repo.LoadDisposals());
        Assert.Equal(rewardsJson, File.ReadAllText(dir.File("rewards.json")));
        Assert.Equal(disposalsJson, File.ReadAllText(dir.File("disposals.json")));
    }

    [Fact]
    public void Backup_before_schema_change_is_complete_and_consistent()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), MonitorConfig);
        Directory.CreateDirectory(dir.File("tax"));
        File.WriteAllText(dir.File("tax", "wallets.json"), "[]");
        using (var h = new HistoryStore(dir.File("history.db")))
            h.AddSample("10.0.0.182", DateTime.Now, 1000, 55, 17, true);

        var backup = ConfigMigrator.BackupDataDirectory(dir.Path);

        Assert.True(File.Exists(Path.Combine(backup, "config.json")));
        Assert.True(File.Exists(Path.Combine(backup, "tax", "wallets.json")));
        var (ok, rows) = HistoryStore.Verify(Path.Combine(backup, "history.db"));
        Assert.True(ok);
        Assert.Equal(1, rows["samples"]);
    }

    [Fact]
    public void Data_directory_move_copies_verifies_and_switches_without_touching_source()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var boot = new TempDir();
        File.WriteAllText(src.File("config.json"), MonitorConfig);
        Directory.CreateDirectory(src.File("tax"));
        File.WriteAllText(src.File("tax", "rewards.json"), "[1]");
        using var history = new HistoryStore(src.File("history.db"));
        history.AddSample("h", DateTime.Now, 1, 1, 1, true);
        history.AddTuningEvent(new TuningEvent("h", DateTime.Now, TuningSource.Manual, 525, 1150, 550, 1170, null));

        var target = Path.Combine(dst.Path, "neu");
        var result = DataDirectoryMigrator.Migrate(src.Path, target, history, boot.File("datadir.json"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("[1]", File.ReadAllText(Path.Combine(target, "tax", "rewards.json")));
        Assert.Equal(MonitorConfig, File.ReadAllText(src.File("config.json")));
        Assert.True(File.Exists(src.File("UMGEZOGEN.txt")));
        Assert.Contains(target.Replace("\\", "\\\\"), File.ReadAllText(boot.File("datadir.json")));
        Assert.Equal(1, HistoryStore.Verify(Path.Combine(target, "history.db")).Rows["tuning_events"]);
    }

    [Fact]
    public void Data_directory_move_aborts_on_existing_files()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var boot = new TempDir();
        File.WriteAllText(src.File("config.json"), "neu");
        File.WriteAllText(dst.File("config.json"), "fremd");

        var result = DataDirectoryMigrator.Migrate(src.Path, dst.Path, null, boot.File("datadir.json"));

        Assert.False(result.Success);
        Assert.Equal("fremd", File.ReadAllText(dst.File("config.json")));
        Assert.False(File.Exists(boot.File("datadir.json")));
    }
}

public class CoordinationTests
{
    /// <summary>Langsamer Fake-Miner, der gleichzeitige Anfragen zählt.</summary>
    private sealed class SlowClient : IMinerClient
    {
        private int _active;
        public int MaxActive;
        public int InfoCalls;
        public int Freq = 525, Mv = 1150;
        public string Address => "10.0.0.1";

        private async Task Work()
        {
            var now = Interlocked.Increment(ref _active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref _active);
        }

        public async Task<MinerInfo> GetInfoAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref InfoCalls);
            await Work();
            return new MinerInfo { FrequencyMhz = Freq, CoreVoltageMv = Mv, HashRateGh = 1000 };
        }
        public async Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) { await Work(); return null; }
        public async Task ApplySettingsAsync(int f, int mv, TuningSource source = TuningSource.Manual, CancellationToken ct = default)
        { await Work(); Freq = f; Mv = mv; }
        public Task SetFanAsync(int a, int m, CancellationToken ct = default) => Work();
        public Task RestartAsync(CancellationToken ct = default) => Work();
    }

    [Fact]
    public async Task Never_more_than_one_request_per_miner_and_info_is_shared()
    {
        var client = new SlowClient();
        using var connection = new MinerConnection(client, new MaintenanceTracker());

        var tasks = Enumerable.Range(0, 10).Select(_ => connection.GetInfoAsync())
            .Append(connection.ApplySettingsAsync(550, 1170))
            .Append(connection.RestartAsync())
            .Concat(Enumerable.Range(0, 5).Select(_ => connection.GetInfoAsync()));
        await Task.WhenAll(tasks);

        Assert.Equal(1, client.MaxActive);
        Assert.Equal(1, connection.MaxConcurrentRequests);
        Assert.True(client.InfoCalls < 10, $"Info-Abfragen sollten geteilt werden, waren {client.InfoCalls}");
    }

    [Fact]
    public async Task Every_apply_is_reported_with_old_and_new_values_and_stored()
    {
        using var dir = new TempDir();
        using var history = new HistoryStore(dir.File("history.db"));
        var maintenance = new MaintenanceTracker();
        using var connection = new MinerConnection(new SlowClient(), maintenance);
        connection.TuningApplied += history.AddTuningEvent;

        await connection.ApplySettingsAsync(600, 1200, TuningSource.Benchmark);

        var e = Assert.Single(history.QueryTuningEvents("10.0.0.1", DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1)));
        Assert.Equal((525, 1150, 600, 1200), (e.OldFrequencyMhz, e.OldCoreVoltageMv, e.NewFrequencyMhz, e.NewCoreVoltageMv));
        Assert.Equal(TuningSource.Benchmark, e.Source);
        Assert.True(maintenance.IsActive("10.0.0.1"), "Änderung muss ein Wartungsfenster öffnen");
    }

    [Fact]
    public void Maintenance_window_opens_closes_and_has_tail()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new MaintenanceTracker(() => now);

        var scope = tracker.Begin("h", "Benchmark");
        now = now.AddHours(2);
        Assert.True(tracker.IsActive("h"));
        scope.Dispose();
        Assert.True(tracker.IsActive("h"), "Nachlauf nach Ende");
        now = now + MaintenanceTracker.DefaultTail + TimeSpan.FromSeconds(1);
        Assert.False(tracker.IsActive("h"));
    }

    [Fact]
    public void Watchdog_never_reboots_during_maintenance_and_restarts_zero_phase_afterwards()
    {
        var watchdog = new Watchdog();
        var settings = new WatchdogSettings { Enabled = true, ZeroHashMinutes = 1, CooldownMinutes = 10 };

        Assert.False(watchdog.ShouldReboot("h", true, 0, settings));            // Nullphase beginnt
        Assert.NotNull(watchdog.ZeroMinutes("h"));
        Assert.False(watchdog.ShouldReboot("h", true, 0, settings, inMaintenance: true));
        Assert.Null(watchdog.ZeroMinutes("h"));                                 // zurückgesetzt
    }

    [Fact]
    public async Task Polling_keeps_history_when_settings_replace_device_copies()
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        using var polling = new MinerPollingService(new MaintenanceTracker(), host => MinerClientFactory.Create(host, registry));
        var devices = new List<DeviceConfig> { new() { Name = "Sim", Host = "sim:bitaxe-gamma" } };
        polling.Sync(devices);
        await polling.PollOnceAsync();
        Assert.Single(polling.States[0].History);
        Assert.True(polling.States[0].Online);
        Assert.NotNull(polling.States[0].Info); // SystemInfo auch ohne Roh-JSON

        // Einstellungsfenster liefert Kopien mit geändertem Namen
        polling.Sync(devices.Select(d => { var c = d.Clone(); c.Name = "Umbenannt"; return c; }).ToList());
        Assert.Single(polling.States[0].History);
        Assert.Equal("Umbenannt", polling.States[0].Config.Name);
    }

    [Fact]
    public async Task Benchmark_through_connection_logs_steps_and_restore()
    {
        var profile = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var events = new List<TuningEvent>();
        using var connection = new MinerConnection(new SimulatedMinerClient(profile, 5), new MaintenanceTracker());
        connection.TuningApplied += events.Add;

        var s = BitaxeTuner.Core.Benchmark.BenchmarkSettings.FromProfile(profile);
        s.WarmupSeconds = 15; s.MeasureSeconds = 105; s.SampleIntervalSeconds = 15; s.MaxFrequencyMhz = 575;
        var session = new BitaxeTuner.Core.Benchmark.BenchmarkSession { DeviceAddress = connection.Address, Settings = s };
        var engine = new BitaxeTuner.Core.Benchmark.BenchmarkEngine(connection, profile) { Delay = (_, _) => Task.CompletedTask };
        using (connection.BeginMaintenance("Benchmark"))
            await engine.RunAsync(session, CancellationToken.None);

        Assert.Equal(session.Results.Count, events.Count(e => e.Source == TuningSource.Benchmark));
        Assert.Equal(TuningSource.Restore, events[^1].Source);
    }
}
