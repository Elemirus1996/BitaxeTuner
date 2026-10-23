using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Discovery;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24: IP-Wechsel eines Miners über die MAC-Adresse erkennen und nach Bestätigung übernehmen.</summary>
public class IpChangeTests
{
    private const string Mac = "24:DC:C3:01:02:03";

    [Fact]
    public void Mac_is_read_and_normalized()
    {
        using var doc = JsonDocument.Parse("""{"hashRate": 1200, "macAddr": "24:dc:c3:01:02:03"}""");
        Assert.Equal(Mac, MinerHub.NormalizeMac(AxeOsClient.Parse(doc.RootElement).MacAddress));
        Assert.Equal(Mac, MinerHub.NormalizeMac("24-DC-C3-01-02-03"));
        Assert.Null(MinerHub.NormalizeMac("00:00:00:00:00:00"));
        Assert.Null(MinerHub.NormalizeMac("kaputt"));
    }

    private static (MinerHub Hub, HubDevice Dev, TempDir Dir) Setup()
    {
        var dir = new TempDir();
        var config = new AppConfig { FilePath = Path.Combine(dir.Path, "config.json") };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.24.5", Mac = Mac });
        config.Devices.Add(new DeviceConfig { Name = "Supra", Host = "10.0.24.6" });
        config.Notifications.Targets.Add(new PushTarget { Id = "abcd1234", Provider = "ntfy", Miners = ["10.0.24.5"] });
        config.Plugs.Items.Add(new SmartPlugConfig { Name = "Steckdose", Host = "10.0.24.50", Miners = ["10.0.24.5"] });
        var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var d = hub.Devices.Single(x => x.Host == "10.0.24.5");
        d.State.Info = null;
        d.State.Error = "Zeitüberschreitung";
        d.State.LastOk = DateTime.Now.AddHours(-1);
        return (hub, d, dir);
    }

    private static DiscoveredMiner Found(string ip, string? mac) => new(ip, new MinerInfo { MacAddress = mac, HashRateGh = 1100 });

    [Fact]
    public void Same_mac_at_a_new_address_is_proposed_not_applied()
    {
        var (hub, d, dir) = Setup();
        using var _ = dir;
        using var __ = hub;
        hub.ApplyScan([d], [Found("10.0.24.6", Mac), Found("10.0.24.9", "AA:BB:CC:DD:EE:FF")], DateTime.Now);
        Assert.Null(hub.HostMoveOf(d));                                  // Adresse gehört schon einem anderen Miner

        hub.ApplyScan([d], [Found("10.0.24.77", "24dcc3010203")], DateTime.Now);
        var move = hub.HostMoveOf(d)!;
        Assert.Equal(("10.0.24.5", "10.0.24.77"), (move.OldHost, move.NewHost));
        Assert.Equal("10.0.24.5", d.Config.Host);                         // nichts von selbst geändert
        var p = Assert.Single(hub.ProblemsOf(d));
        Assert.Equal(("ipchange", "hostmove:10.0.24.77"), (p.Key, p.Link));
    }

    [Fact]
    public void Accepting_moves_history_and_references_without_deleting()
    {
        var (hub, d, dir) = Setup();
        using var _ = dir;
        using var __ = hub;
        var t = DateTime.Now.AddHours(-2);
        hub.History!.AddSample("10.0.24.5", t, 1200, 55, 15, true);
        hub.History.AddWifi("10.0.24.5", t, -60);

        hub.ChangeHost(d, "10.0.24.77");

        Assert.Equal("10.0.24.77", d.Config.Host);
        Assert.Equal(["10.0.24.5"], d.Config.PreviousHosts);
        Assert.Equal(["10.0.24.77"], hub.Config.Notifications.Targets[0].Miners);
        Assert.Equal(["10.0.24.77"], hub.Config.Plugs.Items[0].Miners);
        Assert.Single(hub.History.Query("10.0.24.77", t.AddMinutes(-5), t.AddMinutes(5)), s => s.HashRateGh > 0);
        Assert.Single(hub.History.QueryWifi("10.0.24.77", t.AddMinutes(-5), t.AddMinutes(5)));
        Assert.Contains("10.0.24.77", File.ReadAllText(hub.Config.FilePath!));
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => hub.ChangeHost(d, "10.0.24.6"));   // schon belegt
    }

    [Fact]
    public async Task Scan_runs_only_for_miners_offline_long_enough_and_at_most_every_30_minutes()
    {
        var (hub, d, dir) = Setup();
        using var _ = dir;
        using var __ = hub;
        var scans = 0;
        hub.MinerScan = _ => { Interlocked.Increment(ref scans); return Task.FromResult<IReadOnlyList<DiscoveredMiner>>([Found("10.0.24.77", Mac)]); };
        var now = DateTime.Now;

        d.State.LastOk = now.AddMinutes(-5);
        hub.TickIpWatch(now);
        Assert.Equal(0, scans);                                          // erst nach 10 min offline

        d.State.LastOk = now.AddMinutes(-15);
        hub.TickIpWatch(now);
        for (var i = 0; i < 50 && hub.HostMoveOf(d) is null; i++) await Task.Delay(20);
        Assert.Equal(1, scans);
        Assert.NotNull(hub.HostMoveOf(d));

        hub.TickIpWatch(now.AddMinutes(10));
        Assert.Equal(1, scans);                                          // höchstens alle 30 min
    }
}
