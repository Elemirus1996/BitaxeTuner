using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.20: eigene Meilensteine als lokale Neuigkeiten (Art „milestone“).</summary>
public sealed class MilestoneTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppConfig _config = new();
    private readonly MinerHub _hub;

    public MilestoneTests()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        _config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.9.1" });
        _hub = new MinerHub(_config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
        });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private List<Core.Network.NewsItem> Milestones() => _hub.News.Items(["milestone"], 50);

    [Fact]
    public void Energy_runtime_and_record_are_announced_once()
    {
        var now = DateTime.Now;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        // Gesamtverbrauch: 1.200 kWh gezählt, 500 schon gemeldet → 1.000 kWh genau einmal
        File.WriteAllText(_dir.File("milestones.json"), JsonSerializer.Serialize(new { TotalKwh = 1200.0, CountedUntil = hour, AnnouncedKwh = 500.0 }));
        _hub.EnergyMilestone(now);
        _hub.EnergyMilestone(now.AddHours(2));
        Assert.Single(Milestones(), m => m.Title.Contains("1.000 kWh") || m.Title.Contains("1,000 kWh"));
        Assert.Equal(1000, _hub.LoadMilestoneState().AnnouncedKwh);

        // Betriebsjahr eines Miners, auch nach einem Neustart nur einmal
        _config.Devices[0].Upkeep = new DeviceUpkeep { Hours = 9000 };
        _hub.TickMilestones(now);
        _hub.TickMilestones(now.AddHours(3));
        Assert.Single(Milestones(), m => m.Title.Contains("1 Jahr"));

        // Neuer Best-Diff-Rekord
        _hub.OnBestDiffRecord("Gamma", "BTC", "845 M", "1.23 G", now);
        Assert.Single(Milestones(), m => m.Title.Contains("Best-Diff") && m.Text.Contains("1.23 G"));
        Assert.All(Milestones(), m => Assert.Equal("milestone", m.Kind));
    }

    [Fact]
    public void Milestones_are_added_once_to_older_news_settings()
    {
        // Ältere Einstellungen ohne „milestone“: einmalig ergänzen; wer es danach abwählt, behält das
        var cfg = new AppConfig();
        cfg.Display.NewsKinds = ["solo", "halving"];
        cfg.ExtraDisplays.Add(new ExtraDisplayConfig { Name = "Büro" });
        cfg.ExtraDisplays[0].Settings.NewsKinds = ["price"];
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        using var dir = new TempDir();
        using (var hub = new MinerHub(cfg, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) }))
        {
            Assert.Contains("milestone", cfg.Display.NewsKinds);
            Assert.Contains("milestone", cfg.ExtraDisplays[0].Settings.NewsKinds);
            Assert.Equal(1, cfg.Display.NewsKindsVersion);
        }
        cfg.Display.NewsKinds.Remove("milestone");
        using var again = new MinerHub(cfg, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
        Assert.DoesNotContain("milestone", cfg.Display.NewsKinds);
    }
}
