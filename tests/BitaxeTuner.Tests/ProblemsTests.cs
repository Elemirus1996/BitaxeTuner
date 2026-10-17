using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.23 „Was ist los?“: Probleme eines Miners mit Ursache und nächstem Schritt.</summary>
public class ProblemsTests
{
    private static (MinerHub Hub, HubDevice Device) Setup(TempDir dir)
    {
        var config = new AppConfig { TempWarn = 65 };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.16.1" });
        var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        return (hub, hub.Devices.Single());
    }

    private static SystemInfo Healthy() => new()
    {
        temp = 55, vrTemp = 50, hashRate = 1200, expectedHashrate = 1200, uptimeSeconds = 3600,
        sharesAccepted = 1000, sharesRejected = 2,
    };

    [Fact]
    public void Healthy_miner_has_no_problems_and_offline_explains_what_to_check()
    {
        using var dir = new TempDir();
        var (hub, d) = Setup(dir);
        using var _ = hub;
        d.State.Info = Healthy();
        Assert.Empty(hub.ProblemsOf(d));

        d.State.Error = "Zeitüberschreitung";
        var off = Assert.Single(hub.ProblemsOf(d));
        Assert.Equal(("offline", "bad"), (off.Key, off.Severity));
        Assert.Equal("Zeitüberschreitung", off.Why);
        Assert.Equal("settings:miners", off.Link);
        Assert.DoesNotContain("10.0.16.1", off.Title + off.Why + off.Check);   // keine Adressen
    }

    [Fact]
    public void Heat_errors_rejects_low_hashrate_and_coinbase_are_found_worst_first()
    {
        using var dir = new TempDir();
        var (hub, d) = Setup(dir);
        using var _ = hub;
        var i = Healthy();
        hub.Config.TempWarn = d.Profile.MaxChipTempC - 4;
        i.temp = d.Profile.MaxChipTempC - 1;   // über der Warnschwelle, unter der Modellgrenze → gelb
        i.sharesRejected = 40;             // 3,8 %
        i.hashRate = 800;                  // 67 % des Solls
        i.coinbasePayoutStatus = "not_found";
        i.isUsingFallbackStratum = 1;
        d.State.Info = i;

        var p = hub.ProblemsOf(d);
        Assert.Equal("coinbase", p[0].Key);                      // rot zuerst
        Assert.Contains(p, x => x.Key == "chip-hot" && x.Severity == "warn" && x.Link == "device:automation");
        Assert.Contains(p, x => x.Key == "rejects" && x.Why.Contains("3,8"));
        Assert.Contains(p, x => x.Key == "low-hash" && x.Why.Contains("67"));
        Assert.Contains(p, x => x.Key == "fallback");
        Assert.All(p, x => Assert.False(string.IsNullOrWhiteSpace(x.Check)));

        // Kurz nach dem Start schwankt die Hashrate – dann kein Hinweis
        i.uptimeSeconds = 120;
        Assert.DoesNotContain(hub.ProblemsOf(d), x => x.Key == "low-hash");
        // Zu wenige Shares für eine Aussage
        i.sharesAccepted = 10; i.sharesRejected = 5;
        Assert.DoesNotContain(hub.ProblemsOf(d), x => x.Key == "rejects");
        // Über der Modellgrenze wird es rot
        i.temp = d.Profile.MaxChipTempC + 1;
        Assert.Contains(hub.ProblemsOf(d), x => x.Key == "chip-hot" && x.Severity == "bad");
    }
}
