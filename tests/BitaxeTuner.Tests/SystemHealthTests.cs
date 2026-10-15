using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: Systemzustand mit Push-Warnung.</summary>
public class SystemHealthTests
{
    [Fact]
    public void Pi_problems_are_reported_once_and_recovery_is_logged()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        var config = new AppConfig();
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "t";
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            Clock = () => now,
            ExtraSystemChecks = _ => [new SystemCheck("cert", "HTTPS-Zertifikat", "gültig bis 20.10.2026", "warn", "Erneuerung prüfen")],
        });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        var files = new Dictionary<string, string>
        {
            ["/proc/meminfo"] = "MemTotal:        3884332 kB\nMemFree:          40000 kB\nMemAvailable:      61440 kB\n",
            ["/sys/class/thermal/thermal_zone0/temp"] = "86200\n",
            ["/sys/devices/platform/soc/soc:firmware/get_throttled"] = "0x50005\n",
        };
        hub.SystemFileReader = p => files.TryGetValue(p, out var v) ? v : null;
        try
        {
            var checks = hub.SystemChecks(now).ToDictionary(c => c.Key);
            Assert.Equal("warn", checks["memory"].State);
            Assert.Equal("bad", checks["cpu-temp"].State);
            Assert.Equal("86,2 °C", checks["cpu-temp"].Value);
            Assert.Equal("bad", checks["power"].State);                 // Unterspannung jetzt
            Assert.Equal("warn", checks["cert"].State);                 // Zusatzprüfung des Servers
            Assert.Equal("ok", checks["disk"].State);

            hub.TickSystemHealthForTest(now);
            hub.TickSystemHealthForTest(now.AddMinutes(5));            // erst nach 10 min erneut
            hub.TickSystemHealthForTest(now.AddMinutes(10));            // gleiche Probleme: Push höchstens einmal am Tag
            Assert.Equal(["system:memory", "system:cpu-temp", "system:power", "system:cert"], sent);

            // Wieder in Ordnung → Protokoll
            files["/sys/class/thermal/thermal_zone0/temp"] = "52000\n";
            hub.TickSystemHealthForTest(now.AddMinutes(20));
            var log = hub.History!.QueryEvents(now.AddDays(-1), now.AddDays(1)).Select(e => e.Message).ToList();
            Assert.Contains(log, m => m.Contains("CPU-Temperatur") && m.Contains("wieder in Ordnung"));
            Assert.Single(log, m => m.StartsWith("Systemzustand: Stromversorgung"));

            // E-Paper-Seite
            config.Display.Pages = new DisplayPages { Overview = false, Daily = false, Chart = false, Soak = false, System = true };
            var page = hub.ComposeDisplay(now, nextPage: true);
            Assert.Equal(DisplayScene.System, page.Scene);
            Assert.NotEmpty(page.System!);
            Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(page).Length);
            var preview = Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW");
            if (preview is not null) SixLabors.ImageSharp.ImageExtensions.SaveAsPng(StatusRenderer.RenderImage(page), Path.Combine(preview, "epaper-system.png"));
        }
        finally
        {
            hub.Dispose();
        }
    }
}
