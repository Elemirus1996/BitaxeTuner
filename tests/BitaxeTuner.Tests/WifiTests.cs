using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24 WLAN-Qualität je Miner: Bewertung, Verlauf, „Was ist los?“, Hinweis bei offline.</summary>
public class WifiTests
{
    [Fact]
    public void Rating_follows_average_and_swing()
    {
        Assert.Equal("unknown", MinerHub.Rate(null, []).State);
        Assert.Equal("good", MinerHub.Rate(-55, []).State);            // zu wenig Verlauf: aktueller Wert
        Assert.Equal("weak", MinerHub.Rate(-80, []).State);
        Assert.Equal("good", MinerHub.Rate(-58, Enumerable.Repeat(-58.0, 20).ToList()).State);
        Assert.Equal("ok", MinerHub.Rate(-70, Enumerable.Repeat(-70.0, 20).ToList()).State);
        var weak = MinerHub.Rate(-60, Enumerable.Repeat(-79.0, 20).ToList());   // im Mittel schwach, auch wenn gerade gut
        Assert.Equal(("weak", -79.0), (weak.State, weak.Avg!.Value));
        var swing = MinerHub.Rate(-60, Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? -50.0 : -72.0).ToList());
        Assert.Equal(("unstable", -72, -50), (swing.State, swing.Min!.Value, swing.Max!.Value));
    }

    [Fact]
    public void Weak_wifi_is_stored_shown_as_problem_and_named_when_offline()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.17.1" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var d = hub.Devices.Single();
        d.State.Info = new SystemInfo { temp = 55, vrTemp = 50, hashRate = 1200, expectedHashrate = 1200, uptimeSeconds = 3600, wifiRSSI = -81 };
        var now = DateTime.Now;
        for (var m = 30; m >= 1; m--) hub.RecordWifi(d, -81, now.AddMinutes(-m));
        Assert.Equal(30, hub.History!.QueryWifi(d.Host, now.AddHours(-1), now).Count);
        var q = hub.WifiOf(d, now.AddSeconds(1));
        Assert.Equal("weak", q.State);
        var problem = Assert.Single(hub.ProblemsOf(d), p => p.Key == "wifi");
        Assert.Equal("warn", problem.Severity);
        Assert.Contains("-81", problem.Why);
        Assert.DoesNotContain("10.0.17.1", problem.Why + problem.Check);
        Assert.Contains("-81", hub.WifiOfflineHint(d.Host));
        hub.RecordWifi(d, -55, now);
        Assert.Equal("", hub.WifiOfflineHint(d.Host));
    }
}
