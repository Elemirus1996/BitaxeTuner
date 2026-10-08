using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>E-Paper-Szenen: Vorrang, Quittieren per Taste 1, gespeicherter Zustand, Seitenwechsel.</summary>
public class DisplaySceneTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir;
        public readonly SimulatedFanDevice Sim = new();
        public readonly MinerHub Hub;
        public DateTime Now = DateTime.Now;
        private readonly bool _ownsDir;

        public Rig(TempDir? dir = null)
        {
            _ownsDir = dir is null;
            Dir = dir ?? new TempDir();
            var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
            var config = File.Exists(Dir.File("config.json")) ? AppConfig.Load(Dir.File("config.json")) : new AppConfig();
            if (config.Devices.Count == 0) config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.7.1" });
            config.Display.Enabled = true;
            config.Fans.CaseTempWarn = 40;
            Hub = new MinerHub(config, new MinerHubOptions
            {
                DataDirectory = Dir.Path,
                OnlineChecks = false,
                ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
                FanDeviceFactory = _ => Sim,
                Clock = () => Now,
            });
        }

        public async Task TickAsync(int seconds = 31)
        {
            Now = Now.AddSeconds(seconds);
            await Hub.FanTickAsync();
        }

        public void Dispose()
        {
            Hub.Dispose();
            if (_ownsDir) Dir.Dispose();
        }
    }

    [Fact]
    public async Task Overview_shows_the_miner_fan_when_no_pico_channel_is_assigned()
    {
        using var rig = new Rig();
        await rig.Hub.PollNowAsync();
        var miner = rig.Hub.BuildDisplayModel(DateTime.Now).Miners.Single();
        Assert.Equal((int)Math.Round(rig.Hub.Devices[0].State.Info!.fanspeed), miner.FanPercent);   // eigener Lüfter des Miners
        Assert.NotNull(miner.FanPercent);
    }

    [Fact]
    public async Task Alarm_block_found_and_best_diff_take_turns_and_are_acknowledged_with_button_1()
    {
        using var dir = new TempDir();
        using (var rig = new Rig(dir))
        {
            rig.Sim.Sensors.Add(new TempReading("28aa000000000001", 45));   // Fühler über der Warnschwelle → Warnung
            await rig.Hub.PollNowAsync();
            await rig.TickAsync(0);                                          // Verbinden, erster Messwert
            await rig.TickAsync(200);
            Assert.Equal(DisplayScene.Alarm, rig.Hub.LastDisplayScene);

            rig.Sim.Press("BTN 1");                                          // quittieren
            await rig.TickAsync(1);
            await rig.TickAsync(31);
            Assert.Equal(DisplayScene.Overview, rig.Hub.LastDisplayScene);   // Warnung bleibt als rote Zeile, kein Vollbild

            rig.Hub.OnBlockFound("Gamma", 1, rig.Now);
            await rig.TickAsync(31);                                         // Blockfund zeigt sich schnell (wie Tastendruck)
            Assert.Equal(DisplayScene.BlockFound, rig.Hub.LastDisplayScene);
            Assert.Equal("Gamma", rig.Hub.SceneState.BlockFound!.Miner);
        }

        // Neustart: der unquittierte Blockfund steht weiter
        using (var rig = new Rig(dir))
        {
            rig.Sim.Sensors.Add(new TempReading("28aa000000000001", 45));
            await rig.Hub.PollNowAsync();
            await rig.TickAsync(0);
            Assert.Equal(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);
            await rig.TickAsync(200);
            Assert.Equal(DisplayScene.BlockFound, rig.Hub.LastDisplayScene);

            rig.Sim.Press("BTN 1");
            await rig.TickAsync(1);
            await rig.TickAsync(31);
            Assert.True(rig.Hub.SceneState.BlockFoundAcknowledged);
            Assert.Equal(DisplayScene.Overview, rig.Hub.LastDisplayScene);   // Warnung war schon quittiert

            // Neue Warnung (zweiter Fühler zu warm) → wieder Vollbild
            rig.Sim.Sensors.Add(new TempReading("28aa000000000002", 46));
            await rig.TickAsync(200);
            Assert.Equal(DisplayScene.Alarm, rig.Hub.LastDisplayScene);
            rig.Sim.Press("BTN 1");
            await rig.TickAsync(1);

            // Rekord: genau einmal
            rig.Hub.OnBestDiffRecord("Gamma", "BTC", "845 M", "1,23 G", rig.Now);
            await rig.TickAsync(31);
            Assert.Equal(DisplayScene.BestDiff, rig.Hub.LastDisplayScene);
            await rig.TickAsync(301);                                        // nächste regelmäßige Aktualisierung (5 min)
            Assert.NotEqual(DisplayScene.BestDiff, rig.Hub.LastDisplayScene);
        }
    }

    [Fact]
    public async Task Block_found_screen_ends_after_hold_time_and_pages_rotate()
    {
        using var rig = new Rig();
        rig.Hub.Config.Display.Pages = new DisplayPages { Overview = true, Daily = true, Chart = true, Soak = true, Network = true };
        await rig.Hub.PollNowAsync();
        rig.Hub.OnBlockFound("Gamma", 2, rig.Now.AddHours(-25));
        Assert.NotEqual(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);  // Haltezeit 24 h vorbei
        rig.Hub.Config.Display.BlockFoundUntil = "button";                                  // 0.9.9: bleibt bis Taste 1
        Assert.Equal(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);
        rig.Hub.SceneState.BlockFoundAcknowledged = true;
        Assert.NotEqual(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);
        rig.Hub.Config.Display.BlockFoundUntil = "hours";

        // Kein Dauertest aktiv → Seite „Dauertest“ entfällt; Wechsel reihum
        var seen = new List<DisplayScene>();
        for (var i = 0; i < 4; i++) seen.Add(rig.Hub.ComposeDisplay(rig.Now, nextPage: true).Scene);
        Assert.Equal(4, seen.Distinct().Count());
        Assert.DoesNotContain(DisplayScene.Soak, seen);
        Assert.Matches(@"^Seite [1-4]/4$", rig.Hub.ComposeDisplay(rig.Now, nextPage: true).PageLabel);

        rig.Hub.StartSoak(rig.Hub.Devices[0], 6);
        var withSoak = new List<DisplayScene>();
        for (var i = 0; i < 5; i++) withSoak.Add(rig.Hub.ComposeDisplay(rig.Now, nextPage: true).Scene);
        Assert.Contains(DisplayScene.Soak, withSoak);

        rig.Hub.Config.Display.RotatePages = false;
        var fixedScene = rig.Hub.ComposeDisplay(rig.Now, nextPage: true).Scene;
        Assert.Equal(fixedScene, rig.Hub.ComposeDisplay(rig.Now, nextPage: true).Scene);

        // Jede Szene lässt sich als Vorschau zeichnen (auch ohne Ereignis, mit Beispieldaten)
        foreach (var scene in Enum.GetValues<DisplayScene>())
        {
            var m = rig.Hub.PreviewScene(scene, rig.Now);
            Assert.Equal(scene, m.Scene);
            Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(m).Length);
        }
    }

    [Fact]
    public async Task All_special_screens_can_end_the_same_way()
    {
        // 0.9.11: „alle erst mit Taste 1“ bzw. „alle nach Stunden“
        using var rig = new Rig();
        await rig.Hub.PollNowAsync();
        var d = rig.Hub.Config.Display;
        rig.Hub.OnBlockFound("Gamma", 2, rig.Now.AddHours(-30));
        d.SpecialUntil = "button";                                                  // Blockfund trotz BlockFoundUntil=hours bis Taste
        Assert.Equal(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);
        d.SpecialUntil = "hours";
        Assert.NotEqual(DisplayScene.BlockFound, rig.Hub.ComposeDisplay(rig.Now).Scene);
        rig.Hub.SceneState.BlockFoundAcknowledged = true;

        // Best-Diff-Rekord: „button“ bleibt über mehrere Aktualisierungen, „each“ nur einmal
        rig.Hub.OnBestDiffRecord("Gamma", "BTC", "845 M", "1,23 G", rig.Now);
        d.SpecialUntil = "button";
        var first = rig.Hub.ComposeDisplay(rig.Now);
        Assert.Equal(DisplayScene.BestDiff, first.Scene);
        rig.Hub.SceneShown(first);
        Assert.Equal(DisplayScene.BestDiff, rig.Hub.ComposeDisplay(rig.Now).Scene);
        d.SpecialUntil = "each";
        rig.Hub.SceneShown(rig.Hub.ComposeDisplay(rig.Now));
        Assert.NotEqual(DisplayScene.BestDiff, rig.Hub.ComposeDisplay(rig.Now).Scene);
    }

    [Fact]
    public void Red_only_for_special_screens_and_warnings()
    {
        var now = new DateTime(2026, 9, 27, 14, 0, 0);
        var m = new DisplayModel("T", now, 1000, 20, 20, 1, 1, null, "Automatik", false, false,
            [new DisplayMiner("A", true, false, 1000, 55, 65, 50, false, false, false, null)], []);
        static int Red(byte[] p) => p.Skip(48000).Sum(b => System.Numerics.BitOperations.PopCount(b));
        Assert.Equal(0, Red(StatusRenderer.Render(m with { Scene = DisplayScene.Daily, Daily = new DisplayDaily(1000, 20, 0.5, 0.1, "EUR", null, null, [new("A", 1000, 20, 55, 1)]) })));
        // 0.9.11: Verlaufsgraph in Rot (Wunsch des Nutzers) – auch als Temperatur, Leistung oder Effizienz
        foreach (var kind in new[] { "hashrate", "temp", "power", "efficiency" })
            Assert.True(Red(StatusRenderer.Render(m with { Scene = DisplayScene.Chart, ChartKind = kind, Chart = [new(now.AddMinutes(-120), 1000, 55), new(now.AddMinutes(-110), 1010, 56), new(now.AddMinutes(-100), 990, 57)] })) > 50, kind);
        Assert.True(Red(StatusRenderer.Render(m with { Scene = DisplayScene.BlockFound, BlockFound = new DisplayBlockFound("A", now, 1, null) })) > 5000);
        Assert.True(Red(StatusRenderer.Render(m with { Scene = DisplayScene.Alarm, Alerts = ["A offline"] })) > 5000);
    }
}
