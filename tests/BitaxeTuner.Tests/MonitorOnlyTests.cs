using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.17: Miner mit anderer Firmware nur überwachen – alles außer Overclocking.</summary>
public class MonitorOnlyTests
{
    [Fact]
    public void ZyberOS_is_detected_by_name()
    {
        static FirmwareKind Kind(string json) => AxeOsClient.DetectFirmware(JsonDocument.Parse(json).RootElement);
        Assert.Equal(FirmwareKind.ZyberOS, Kind("""{"ASICModel":"BM1370","deviceModel":"Zyber Blanc","version":"v1.1.6"}"""));
        Assert.Equal(FirmwareKind.ZyberOS, Kind("""{"ASICModel":"BM1370","boardVersion":"zyber-blanc-oc"}"""));
        Assert.Equal(FirmwareKind.AxeOS, Kind("""{"ASICModel":"BM1370","deviceModel":"Gamma","axeOSVersion":"v2.15.3"}"""));
        Assert.Equal(FirmwareKind.Unknown, Kind("""{"hashRate":1000}"""));
    }

    [Fact]
    public async Task Monitor_only_blocks_tuning_but_not_monitoring()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.7.1" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
        Assert.True(await hub.PollNowAsync());
        var d = hub.Device("10.0.7.1")!;
        Assert.Null(d.MonitorOnlyReason);                                 // Simulation/AxeOS: Tuning erlaubt

        d.Config.MonitorOnly = true;
        Assert.Equal("von Hand eingestellt", d.MonitorOnlyReason);
        var info = d.Info!;
        var ex = await Assert.ThrowsAsync<LocalizedException>(() => hub.ApplyChangeAsync(d, info.FrequencyMhz, info.CoreVoltageMv));
        Assert.Contains("nur überwacht", ex.Message);
        await Assert.ThrowsAsync<LocalizedException>(() => d.Connection.PatchSettingsAsync(new Dictionary<string, object> { ["coreVoltage"] = 1200 }));
        await Assert.ThrowsAsync<LocalizedException>(() => hub.Benchmarks.RunAsync(d, new BenchmarkPlan(BenchmarkSettings.FromProfile(d.Profile), false, "")));
        await d.Connection.PatchSettingsAsync(new Dictionary<string, object> { ["fanspeed"] = 80 });   // Lüfter/Pools weiter erlaubt
        Assert.True(await hub.PollNowAsync());                            // Überwachung läuft weiter

        d.Config.MonitorOnly = false;                                     // „Tuning erlauben“ übersteuert die Automatik
        Assert.Null(d.MonitorOnlyReason);
    }

    [Fact]
    public void Firmware_check_uses_the_right_repository()
    {
        var s = new Core.Monitoring.MinerState(new DeviceConfig { Host = "10.0.7.2" });
        Assert.Equal(MinerHub.DefaultFirmwareRepo, MinerHub.FirmwareRepoOf(s));
        s.Normalized = new MinerInfo { Firmware = FirmwareKind.ZyberOS };
        Assert.Equal("TinyChipHub/ZyberOS-Release", MinerHub.FirmwareRepoOf(s));
        s.Normalized = new MinerInfo { Firmware = FirmwareKind.Unknown };
        Assert.Equal("", MinerHub.FirmwareRepoOf(s));                     // kein falscher Update-Hinweis
        s.Config.FirmwareRepo = "eigen/repo";
        Assert.Equal("eigen/repo", MinerHub.FirmwareRepoOf(s));           // eigene Angabe gilt immer
    }
}
