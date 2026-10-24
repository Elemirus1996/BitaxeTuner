using System.Security.Cryptography;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.25: Firmware-Update für alle Miner nacheinander – Dateiauswahl, Prüfung, Ablauf, Fehler, Abweichungen.</summary>
public class FirmwareUpdateTests
{
    /// <summary>Miner mit echter Firmware-Kennung, der Uploads annimmt (Simulator darunter).</summary>
    private sealed class OtaClient(SimulatedMinerClient sim, FirmwareKind kind, string version, string? model = null) : IMinerClient
    {
        public string Version = version;
        public readonly List<(bool Www, int Bytes)> Uploads = [];
        /// <summary>Was die Firmware nach dem Aufspielen meldet (null = nimmt das Update nicht an).</summary>
        public string? VersionAfterUpload;
        public bool HashAfterUpload = true;
        public Action? AfterUpload;
        private bool _flashed;

        public string Address => sim.Address;
        public async Task<MinerInfo> GetInfoAsync(CancellationToken ct = default)
        {
            var i = await sim.GetInfoAsync(ct);
            return i with { Firmware = kind, FirmwareVersion = Version, DeviceModel = model ?? i.DeviceModel, HashRateGh = _flashed && !HashAfterUpload ? 0 : i.HashRateGh };
        }
        public Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) => sim.GetAsicInfoAsync(ct);
        public Task ApplySettingsAsync(int f, int v, TuningSource s = TuningSource.Manual, CancellationToken ct = default) => sim.ApplySettingsAsync(f, v, s, ct);
        public Task SetFanAsync(int a, int p, CancellationToken ct = default) => sim.SetFanAsync(a, p, ct);
        public Task RestartAsync(CancellationToken ct = default) => sim.RestartAsync(ct);
        public Task<string> GetRawInfoAsync(CancellationToken ct = default) => sim.GetRawInfoAsync(ct);
        public Task PatchSettingsAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default) => sim.PatchSettingsAsync(values, ct);
        public Task UploadFirmwareAsync(bool www, byte[] data, CancellationToken ct = default)
        {
            Uploads.Add((www, data.Length));
            if (!www)
            {
                _flashed = true;
                if (VersionAfterUpload is { } v) Version = v;
                AfterUpload?.Invoke();
            }
            return Task.CompletedTask;
        }
    }

    private static byte[] Image(int size, byte first = 0xE9)
    {
        var b = new byte[size];
        b[0] = first;
        b[size - 1] = 7;
        return b;
    }

    private static FirmwareAsset Asset(string name, byte[] data, bool withSha = true) =>
        new(name, data.Length, withSha ? Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : null, "https://example.invalid/" + name);

    private static readonly byte[] EspMiner = Image(700_000);
    private static readonly byte[] NerdBin = Image(800_000);
    private static readonly byte[] WwwBin = Image(300_000, 0x00);

    private static Dictionary<string, byte[]> Files => new()
    {
        ["https://example.invalid/esp-miner.bin"] = EspMiner,
        ["https://example.invalid/esp-miner-NerdQAxe++.bin"] = NerdBin,
        ["https://example.invalid/www.bin"] = WwwBin,
    };

    private static FirmwareRelease EspRelease(string tag = "v2.15.4", bool pre = false) =>
        new(tag, "https://github.com/bitaxeorg/ESP-Miner/releases/tag/" + tag, pre,
            [Asset("esp-miner-factory-601-" + tag + ".bin", Image(1_000_000)), Asset("esp-miner.bin", EspMiner)]);

    private static (MinerHub Hub, List<OtaClient> Clients) Hub(TempDir dir, params (FirmwareKind Kind, string Version, string? Model)[] miners)
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig { FilePath = Path.Combine(dir.Path, "config.json") };
        var clients = new List<OtaClient>();
        for (var i = 0; i < miners.Length; i++)
            config.Devices.Add(new DeviceConfig { Name = "Miner" + (i + 1), Host = $"10.0.25.{i + 1}", ProfileId = "bitaxe-gamma", MonitorOnly = false });
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path, OnlineChecks = false,
            ClientFactory = h =>
            {
                var idx = int.Parse(h.Split('.')[^1]) - 1;
                var c = new OtaClient(new SimulatedMinerClient(gamma, idx + 1, h), miners[idx].Kind, miners[idx].Version, miners[idx].Model);
                clients.Add(c);
                return c;
            },
            FirmwareDelay = (_, _) => Task.CompletedTask,
            FirmwareDownload = (url, _) => Task.FromResult(Files[url]),
        });
        return (hub, clients);
    }

    private static async Task<FirmwareJob> RunToEnd(MinerHub hub, IEnumerable<string> hosts)
    {
        var job = hub.StartFirmwareUpdate(hosts, "Test");
        for (var i = 0; i < 400 && job.Running; i++) await Task.Delay(25);
        Assert.False(job.Running, "Firmware-Update wurde nicht fertig");
        return job;
    }

    // ---------- Dateien ----------

    [Fact]
    public void Picks_the_ota_file_never_a_factory_image_and_the_board_file_for_NerdQAxe()
    {
        var esp = FirmwareFiles.Select(FirmwareKind.AxeOS, "Gamma", EspRelease(), out var e1);
        Assert.Null(e1);
        Assert.Equal("esp-miner.bin", esp!.Firmware.Name);
        Assert.Null(esp.Www);   // ab ESP-Miner 2.15 steckt die Weboberfläche in der Firmware

        var nerdRelease = new FirmwareRelease("v1.1.0.1", null, false,
            [Asset("esp-miner-factory-NerdQAxe++-v1.1.0.1.bin", Image(900_000)), Asset("esp-miner-NerdQAxe+.bin", Image(800_001)),
             Asset("esp-miner-NerdQAxe++.bin", NerdBin), Asset("www.bin", WwwBin)]);
        var nerd = FirmwareFiles.Select(FirmwareKind.NerdQAxe, "NerdQAxe++", nerdRelease, out _);
        Assert.Equal("esp-miner-NerdQAxe++.bin", nerd!.Firmware.Name);
        Assert.Equal("www.bin", nerd.Www!.Name);

        Assert.Null(FirmwareFiles.Select(FirmwareKind.NerdQAxe, "NerdOCTAXE-Gamma", nerdRelease, out var e2));
        Assert.Contains("esp-miner-NerdOCTAXE-Gamma.bin", e2);
        Assert.Null(FirmwareFiles.Select(FirmwareKind.NerdQAxe, null, nerdRelease, out _));
        Assert.Null(FirmwareFiles.Select(FirmwareKind.ZyberOS, "Zyber", EspRelease(), out _));
    }

    [Fact]
    public void Downloads_are_checked_for_size_checksum_and_esp32_header()
    {
        var a = Asset("esp-miner.bin", EspMiner);
        Assert.Null(FirmwareFiles.Verify(a, EspMiner, true));
        Assert.Contains("unvollständig", FirmwareFiles.Verify(a, EspMiner[..1000], true));
        var tampered = (byte[])EspMiner.Clone();
        tampered[500] = 1;
        Assert.Contains("Prüfsumme", FirmwareFiles.Verify(a, tampered, true));
        var notEsp = Image(700_000, 0x00);
        Assert.Contains("keine ESP32-Firmware", FirmwareFiles.Verify(Asset("esp-miner.bin", notEsp, withSha: false), notEsp, true));
        Assert.Null(FirmwareFiles.Verify(Asset("www.bin", WwwBin), WwwBin, false));   // Weboberfläche ohne ESP-Kennung
    }

    // ---------- Releases ----------

    [Fact]
    public void Releases_are_parsed_with_checksums_and_beta_is_only_offered_when_allowed()
    {
        var json = """
        [ { "tag_name": "v2.16.0-rc1", "prerelease": true, "draft": false, "html_url": "u1", "assets": [ { "name": "esp-miner.bin", "size": 5, "digest": "sha256:ABC", "browser_download_url": "d1" } ] },
          { "tag_name": "v2.16.0", "prerelease": false, "draft": true, "assets": [] },
          { "tag_name": "v2.15.4", "prerelease": false, "draft": false, "html_url": "u2", "assets": [ { "name": "esp-miner.bin", "size": 7, "browser_download_url": "d2" } ] } ]
        """;
        var list = FirmwareChecker.ParseReleases(json);
        Assert.Equal(["v2.16.0-rc1", "v2.15.4"], list.Select(r => r.Tag));   // Entwurf übersprungen
        Assert.Equal("abc", list[0].Assets[0].Sha256);
        Assert.Null(list[1].Assets[0].Sha256);

        using var fc = new FirmwareChecker();
        fc.SetReleases("bitaxeorg/ESP-Miner", list);
        Assert.Equal("v2.15.4", fc.Target("bitaxeorg/ESP-Miner")!.Tag);
        Assert.Equal("v2.16.0-rc1", fc.Target("bitaxeorg/ESP-Miner", beta: true)!.Tag);
        Assert.Equal(FirmwareStatus.UpToDate, fc.Evaluate("bitaxeorg/ESP-Miner", "v2.15.4").Status);
        Assert.Equal(FirmwareStatus.UpdateAvailable, fc.Evaluate("bitaxeorg/ESP-Miner", "v2.15.4", beta: true).Status);
    }

    [Fact]
    public void Versions_compare_with_four_parts_and_prereleases()
    {
        Assert.True(FirmwareChecker.IsNewer("v1.1.0.1", "v1.1.0"));       // NerdQAxe: vierte Stelle zählt
        Assert.False(FirmwareChecker.IsNewer("v2.15.4", "v2.15.4"));
        Assert.True(FirmwareChecker.IsNewer("v2.16.0", "v2.16.0-rc1"));   // Vorabversion → offizielle gleicher Nummer
        Assert.False(FirmwareChecker.IsNewer("v2.15.3", "2.15.4"));
        Assert.Null(FirmwareChecker.IsNewer("v2.15.4", "(Demo)"));
    }

    // ---------- Ablauf ----------

    [Fact]
    public async Task Updates_one_miner_after_the_other_with_backup_and_check()
    {
        using var dir = new TempDir();
        var (hub, clients) = Hub(dir, (FirmwareKind.AxeOS, "v2.14.0", null), (FirmwareKind.AxeOS, "v2.15.3", null));
        using var _ = hub;
        hub.Firmware.SetReleases(MinerHub.DefaultFirmwareRepo, [EspRelease()]);
        await hub.PollNowAsync();
        foreach (var c in clients) c.VersionAfterUpload = "v2.15.4";

        var candidates = hub.FirmwareCandidates();
        Assert.All(candidates, c => Assert.True(c.CanUpdate, c.Reason));
        Assert.Equal("v2.15.4", candidates[0].Target);

        var job = await RunToEnd(hub, candidates.Select(c => c.Host));
        Assert.All(job.Items, i => Assert.Equal("done", i.Status));
        Assert.Equal(["v2.14.0", "v2.15.3"], job.Items.Select(i => i.From));
        Assert.All(clients, c => Assert.Equal([(false, EspMiner.Length)], c.Uploads));   // nur esp-miner.bin, keine www.bin
        Assert.All(job.Items, i => Assert.True(File.Exists(i.SnapshotFile)));           // vorher gesichert
        Assert.Contains(hub.Devices[0].LogLines, l => l.Contains("Firmware v2.15.4 läuft, Einstellungen unverändert"));
        await hub.PollNowAsync();
        Assert.Equal("aktuell", hub.FirmwareCandidateOf(hub.Devices[0]).Reason);
    }

    [Fact]
    public async Task Stops_after_a_failure_and_leaves_the_others_alone()
    {
        using var dir = new TempDir();
        var (hub, clients) = Hub(dir, (FirmwareKind.AxeOS, "v2.14.0", null), (FirmwareKind.AxeOS, "v2.14.0", null));
        using var _ = hub;
        hub.Firmware.SetReleases(MinerHub.DefaultFirmwareRepo, [EspRelease()]);
        await hub.PollNowAsync();
        // erster Miner nimmt das Update nicht an (VersionAfterUpload bleibt null)

        var job = await RunToEnd(hub, hub.Devices.Select(d => d.Host));
        Assert.Equal("failed", job.Items[0].Status);
        Assert.Contains("nicht übernommen", job.Items[0].Message);
        Assert.Equal("skipped", job.Items[1].Status);
        Assert.Empty(clients[1].Uploads);
        Assert.Contains(hub.ProblemsOf(hub.Devices[0]), p => p.Key == "firmware");
    }

    [Fact]
    public async Task No_hashrate_after_the_update_is_a_failure()
    {
        using var dir = new TempDir();
        var (hub, clients) = Hub(dir, (FirmwareKind.AxeOS, "v2.14.0", null));
        using var _ = hub;
        hub.Firmware.SetReleases(MinerHub.DefaultFirmwareRepo, [EspRelease()]);
        await hub.PollNowAsync();
        clients[0].VersionAfterUpload = "v2.15.4";
        clients[0].HashAfterUpload = false;

        var job = await RunToEnd(hub, [hub.Devices[0].Host]);
        Assert.Equal("failed", job.Items[0].Status);
        Assert.Contains("keine Hashrate", job.Items[0].Message);
    }

    [Fact]
    public async Task Changed_settings_are_only_reported_and_restored_after_confirmation()
    {
        using var dir = new TempDir();
        var (hub, clients) = Hub(dir, (FirmwareKind.AxeOS, "v2.14.0", null));
        using var _ = hub;
        hub.Firmware.SetReleases(MinerHub.DefaultFirmwareRepo, [EspRelease()]);
        await hub.PollNowAsync();
        var before = (await clients[0].GetInfoAsync()).FrequencyMhz;
        clients[0].VersionAfterUpload = "v2.15.4";
        clients[0].AfterUpload = () => clients[0].ApplySettingsAsync(before - 50, 1150).Wait();   // Firmware setzt Werte zurück

        var job = await RunToEnd(hub, [hub.Devices[0].Host]);
        var item = Assert.Single(job.Items);
        Assert.Equal("changed", item.Status);
        Assert.Contains(item.Changes, c => c.Field == "frequency");
        Assert.Equal(before - 50, (await clients[0].GetInfoAsync()).FrequencyMhz);   // nichts ungefragt zurückgeschrieben

        await hub.RestoreAfterFirmwareAsync(item.Host, "Test");
        Assert.Equal(before, (await clients[0].GetInfoAsync()).FrequencyMhz);
        Assert.Equal("done", item.Status);
    }

    [Fact]
    public async Task NerdQAxe_gets_its_board_file_after_the_web_interface()
    {
        using var dir = new TempDir();
        var (hub, clients) = Hub(dir, (FirmwareKind.NerdQAxe, "v1.1.0", "NerdQAxe++"));
        using var _ = hub;
        hub.Firmware.SetReleases(MinerHub.NerdQAxeFirmwareRepo, [new FirmwareRelease("v1.1.0.1", null, false,
            [Asset("esp-miner-NerdQAxe++.bin", NerdBin), Asset("www.bin", WwwBin)])]);
        await hub.PollNowAsync();
        clients[0].VersionAfterUpload = "v1.1.0.1";
        Assert.Equal(MinerHub.NerdQAxeFirmwareRepo, MinerHub.FirmwareRepoOf(hub.Devices[0].State));   // nicht mit ESP-Miner vergleichen

        var job = await RunToEnd(hub, [hub.Devices[0].Host]);
        Assert.Equal("done", job.Items[0].Status);
        Assert.Equal([(true, WwwBin.Length), (false, NerdBin.Length)], clients[0].Uploads);
    }

    [Fact]
    public async Task Only_allowed_miners_are_offered_and_beta_needs_the_switch()
    {
        using var dir = new TempDir();
        var (hub, _) = Hub(dir, (FirmwareKind.AxeOS, "v2.15.4", null), (FirmwareKind.ZyberOS, "v1.0", null));
        using var __ = hub;
        hub.Firmware.SetReleases(MinerHub.DefaultFirmwareRepo, [EspRelease("v2.16.0-rc1", pre: true), EspRelease()]);
        await hub.PollNowAsync();
        Assert.Equal("aktuell", hub.FirmwareCandidateOf(hub.Devices[0]).Reason);
        Assert.False(hub.FirmwareCandidateOf(hub.Devices[1]).CanUpdate);   // ZyberOS: nur überwachen
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => hub.StartFirmwareUpdate(hub.Devices.Select(d => d.Host), "Test"));

        hub.Devices[0].Config.FirmwareBeta = true;
        var beta = hub.FirmwareCandidateOf(hub.Devices[0]);
        Assert.True(beta.CanUpdate);
        Assert.Equal("v2.16.0-rc1", beta.Target);
        Assert.True(beta.Prerelease);
    }
}
