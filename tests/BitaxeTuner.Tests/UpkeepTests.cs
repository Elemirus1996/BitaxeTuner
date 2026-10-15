using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: Wartungs-Erinnerungen.</summary>
public class UpkeepTests
{
    [Fact]
    public async Task Operating_hours_are_counted_and_due_tasks_reminded()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.7.1" });
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "t";
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        Assert.True(await hub.PollNowAsync());
        var d = hub.Device("10.0.7.1")!;
        d.State.Info!.totalUptimeSeconds = 100 * 3600;
        d.Config.Upkeep = null;                                         // wie beim allerersten Kontakt

        var now = DateTime.Now.AddMinutes(1);
        hub.TickUpkeepForTest(now);                                     // Startwert aus der Firmware
        Assert.Equal(100, d.Config.Upkeep!.Hours, 3);
        Assert.All(hub.UpkeepOf(d), t => Assert.False(t.Due));
        hub.TickUpkeepForTest(now.AddMinutes(5));
        Assert.Equal(100 + 5 / 60.0, d.Config.Upkeep.Hours, 3);
        hub.TickUpkeepForTest(now.AddHours(3));                         // Lücke über 10 min: nicht gezählt
        Assert.Equal(100 + 5 / 60.0, d.Config.Upkeep.Hours, 3);

        // 8 Betriebstage später ist „Staub“ (7 Tage) fällig
        hub.Config.Upkeep.DustDays = 7;
        d.Config.Upkeep.Hours += 8 * 24;
        var dust = hub.UpkeepOf(d).Single(t => t.Key == "dust");
        Assert.True(dust.Due);
        Assert.Equal(-1, dust.DueInDays, 1);
        hub.TickUpkeepForTest(now.AddHours(5));
        Assert.Equal([$"upkeep:{d.Host}:dust"], sent);

        hub.UpkeepDone(d, "dust", "Test");
        Assert.False(hub.UpkeepOf(d).Single(t => t.Key == "dust").Due);
        Assert.Contains(d.LogLines, l => l.Contains("Wartung erledigt"));
        Assert.True(AppConfig.Load(Path.Combine(dir.Path, "config.json")).Devices[0].Upkeep!.Done.ContainsKey("dust"));   // gespeichert
    }
}
