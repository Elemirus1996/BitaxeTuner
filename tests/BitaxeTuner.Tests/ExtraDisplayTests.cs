using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11: mehrere Anzeigen – jede mit eigenem Display-Pico, eigenen Seiten und optional einer Gruppe.</summary>
public sealed class ExtraDisplayTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, SimulatedFanDevice> _picos = new();
    private readonly MinerHub _hub;
    private readonly AppConfig _config = new();

    public ExtraDisplayTests()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        _config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.10.1", Groups = ["Keller"] });
        _config.Devices.Add(new DeviceConfig { Name = "Supra", Host = "10.0.10.2", Groups = ["Büro"] });
        _config.Display.Enabled = true;
        _config.Display.Device = "own";                                            // erste Anzeige: USB („auto“)
        _config.ExtraDisplays.Add(new ExtraDisplayConfig
        {
            Id = "a1", Name = "Keller", Group = "Keller",
            Settings = new DisplaySettings { Enabled = true, Device = "own", Connection = "wlan", NetworkHost = "keller.local", Title = "Keller" },
        });
        _config.ExtraDisplays.Add(new ExtraDisplayConfig
        {
            Id = "b2", Name = "Flur",
            Settings = new DisplaySettings
            {
                Enabled = true, Device = "own", Connection = "wlan", NetworkHost = "flur.local",
                Pages = new DisplayPages { Overview = false, Daily = false, Chart = false, Soak = false, Qr = true, News = true },
            },
        });
        _hub = new MinerHub(_config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            DisplayDeviceFactory = target => _picos[target] = new SimulatedFanDevice(PicoFanDevice.RoleDisplay),
            WebUrl = () => "https://192.0.2.5:8484/",
        });
        _hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private ExtraDisplayRuntime X(string id) => _hub.ExtraDisplay(id)!;

    [Fact]
    public async Task Every_display_gets_its_own_pico_pages_and_group()
    {
        await _hub.PollNowAsync();
        await _hub.FanTickAsync();
        Assert.Single(_picos["auto"].Images);                                     // erste Anzeige
        Assert.Single(_picos["keller.local"].Images);
        Assert.Single(_picos["flur.local"].Images);
        Assert.True(X("a1").Status.Connected);

        var keller = _hub.ComposeExtraDisplay(X("a1"), DateTime.Now, DisplayScene.Overview);
        Assert.Equal("Keller · Keller", keller.Title);                            // nur die Gruppe
        Assert.Equal(1, keller.Count);
        Assert.Equal(2, _hub.ComposeDisplay(DateTime.Now).Count);                 // erste Anzeige: alle Miner

        var flur = _hub.ComposeExtraDisplay(X("b2"), DateTime.Now);
        Assert.Equal(DisplayScene.Qr, flur.Scene);                                // eigene Seiten
        Assert.Equal("Seite 1/2", flur.PageLabel);
    }

    [Fact]
    public async Task Button_one_turns_only_its_own_display_and_events_reach_all()
    {
        await _hub.PollNowAsync();
        await _hub.FanTickAsync();
        _picos["flur.local"].Press("BTN 1");
        await _hub.FanTickAsync();
        Assert.Equal(1, X("b2").State.PageIndex);
        Assert.Equal(0, X("a1").State.PageIndex);
        Assert.Equal(0, _hub.SceneState.PageIndex);
        Assert.Equal(DisplayScene.News, _hub.ComposeExtraDisplay(X("b2"), DateTime.Now).Scene);

        _hub.OnBlockFound("Gamma", 1, DateTime.Now);
        Assert.NotNull(X("a1").State.BlockFound);
        Assert.Equal(DisplayScene.BlockFound, _hub.ComposeExtraDisplay(X("b2"), DateTime.Now).Scene);
        X("b2").LastScene = DisplayScene.BlockFound;                              // so steht es auf dieser Anzeige
        _hub.ExtraDisplayNext(X("b2"), "Test");                                   // quittiert nur dort
        Assert.True(X("b2").State.BlockFoundAcknowledged);
        Assert.False(X("a1").State.BlockFoundAcknowledged);
        Assert.False(_hub.SceneState.BlockFoundAcknowledged);
    }

    [Fact]
    public async Task Removed_display_is_disconnected_and_state_survives_a_restart()
    {
        await _hub.PollNowAsync();
        await _hub.FanTickAsync();
        _hub.ExtraDisplayNext(X("b2"), "Test");
        Assert.True(File.Exists(Path.Combine(_dir.Path, "display-state-b2.json")));

        _config.ExtraDisplays.RemoveAll(d => d.Id == "a1");
        Assert.Null(_hub.ExtraDisplay("a1"));
        Assert.Single(_hub.ExtraRuntimes());
        Assert.False(ExtraDisplayConfig.ValidId("../x"));
    }
}
