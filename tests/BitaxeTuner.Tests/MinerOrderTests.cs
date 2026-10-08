using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11: Reihenfolge der Miner frei wählbar – gilt überall, nichts geht verloren.</summary>
public sealed class MinerOrderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MinerHub _hub;

    public MinerOrderTests()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        foreach (var (name, host) in new[] { ("Gamma 3", "10.0.11.3"), ("Gamma 1", "10.0.11.1"), ("Gamma 2", "10.0.11.2") })
            config.Devices.Add(new DeviceConfig { Name = name, Host = host });
        _hub = new MinerHub(config, new MinerHubOptions
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

    [Fact]
    public void Reordering_changes_every_view_and_keeps_unnamed_miners()
    {
        _hub.ReorderDevices(["10.0.11.1", "10.0.11.2"], "Test");                  // Gamma 3 nicht genannt → ans Ende
        Assert.Equal(["Gamma 1", "Gamma 2", "Gamma 3"], _hub.Devices.Select(d => d.Title));
        Assert.Equal(["Gamma 1", "Gamma 2", "Gamma 3"], _hub.Config.Devices.Select(d => d.Name));
        Assert.Equal(["Gamma 1", "Gamma 2", "Gamma 3"], _hub.BuildDisplayModel(DateTime.Now).Miners.Select(m => m.Name));

        _hub.ReorderDevices(["10.0.11.9", "10.0.11.3"], "Test");                  // unbekannte Adresse wird ignoriert
        Assert.Equal(["Gamma 3", "Gamma 1", "Gamma 2"], _hub.Devices.Select(d => d.Title));
        Assert.Equal(3, _hub.Config.Devices.Count);
    }

    [Fact]
    public void Overview_layout_is_validated()
    {
        var layout = new OverviewLayout { Panels = [new() { Type = "chart", ColSpan = 40, Title = new string('x', 100) }, null!] };
        OverviewLayouts.Validate(layout);
        Assert.Single(layout.Panels);
        Assert.Equal(12, layout.Panels[0].ColSpan);
        Assert.Equal(60, layout.Panels[0].Title.Length);
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => OverviewLayouts.Validate(new OverviewLayout { Panels = [new() { Type = "iframe" }] }));
        Assert.Equal("kpis", OverviewLayouts.Default().Panels[0].Type);
    }
}
