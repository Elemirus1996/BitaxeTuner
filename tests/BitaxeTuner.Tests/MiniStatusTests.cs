using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.25: Mini-Anzeige und Tray-Tooltip der Desktop-App.</summary>
public class MiniStatusTests
{
    [Fact]
    public void Server_status_is_read_with_totals_and_warnings()
    {
        using var doc = JsonDocument.Parse("""
            { "running": true,
              "totals": { "hashrate": 2350, "power": 31.4, "online": 2, "count": 3, "maxTemp": 58.2, "costPerDay": 0.42, "currency": "€" },
              "devices": [ { "name": "Gamma", "problems": [ { "title": "Miner antwortet nicht" } ] }, { "name": "Supra", "problems": null } ] }
            """);
        var s = MiniStatus.FromStatusJson(doc.RootElement);
        Assert.Equal((2, 3, 2350d, 31.4), (s.Online, s.Total, s.HashGh, s.PowerW));
        Assert.Equal(["Gamma: Miner antwortet nicht"], s.Warnings);
        Assert.Equal("warn", s.Level);
        Assert.Equal("2.35 TH/s", s.HashText.Replace(',', '.'));
        var tip = s.Tooltip();
        Assert.True(tip.Length <= 127);
        Assert.Contains("2/3", tip);
        Assert.Contains("Gamma", tip);
    }

    [Fact]
    public void Levels_follow_the_state()
    {
        MiniStatus S(int online, int total, bool running = true, params string[] warnings) => new(online, total, 1000, 15, 50, 0.1, "€", warnings, running);
        Assert.Equal("unknown", S(0, 0).Level);
        Assert.Equal("error", S(0, 2).Level);
        Assert.Equal("ok", S(2, 2).Level);
        Assert.Equal("warn", S(2, 2, running: false).Level);
        Assert.Equal("warn", S(2, 2, true, "zu warm").Level);
        var many = S(2, 2, true, Enumerable.Range(0, 20).Select(i => $"Miner {i}: sehr lange Warnung mit viel Text").ToArray());
        Assert.True(many.Tooltip().Length <= 127);
    }

    [Fact]
    public async Task Local_status_comes_from_the_hub()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "sim:a" });
        config.Devices.Add(new DeviceConfig { Name = "Supra", Host = "sim:b" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
        await hub.PollNowAsync();
        var s = MiniStatus.FromHub(hub, DateTime.Now);
        Assert.Equal((2, 2), (s.Online, s.Total));
        Assert.True(s.HashGh > 0 && s.PowerW > 0 && s.CostPerDay > 0);
        Assert.NotNull(s.MaxTemp);
    }

    [Fact]
    public void Mini_window_settings_are_additive()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(file, """{ "IntervalSeconds": 30 }""");
        var c = AppConfig.Load(file);
        Assert.False(c.Mini.Enabled);
        Assert.Equal(0.9, c.Mini.Opacity);
        c.Mini.Enabled = true;
        c.Mini.Left = 100;
        c.Save(file);
        Assert.True(AppConfig.Load(file).Mini is { Enabled: true, Left: 100 });
    }
}
