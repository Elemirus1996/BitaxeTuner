using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: Systemzustand mit Push-Warnung.</summary>
public class SystemHealthTests
{
    [Fact]
    public void Sd_card_writes_are_counted_per_day_across_reboots_and_warn_when_high()
    {
        // 0.9.20: Speicherkarte überwachen
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 10, 12, 0, 0);
        var hub = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, Clock = () => now });
        var data = dir.Path.Replace('\\', '/');
        long sectors = 1_000_000;
        var files = new Dictionary<string, string>
        {
            ["/proc/sys/kernel/random/boot_id"] = "aaaa",
            ["/proc/self/mountinfo"] = $"25 1 179:2 / / rw,noatime shared:1 - ext4 /dev/root rw\n30 25 179:2 / {data} rw,noatime - ext4 /dev/root rw\n40 25 8:1 / /mnt/usb rw - vfat /dev/sda1 rw\n",
        };
        string Stats() => $" 179       0 mmcblk0 9 0 9 0 9 0 999999 0 0 0 0\n 179       2 mmcblk0p2 100 0 2000 50 300 0 {sectors} 900 0 0 0\n   8       1 sda1 1 0 1 0 1 0 77 0 0 0 0\n";
        hub.SystemFileReader = p => p == "/proc/diskstats" ? Stats() : files.TryGetValue(p, out var v) ? v : null;
        try
        {
            hub.SampleDiskWrites(now);                                   // erster Lauf: nur merken
            Assert.DoesNotContain(hub.SystemChecks(now), c => c.Key == "disk-writes");
            sectors += 2_048_000;                                         // ~1 GB
            hub.SampleDiskWrites(now.AddMinutes(10));
            var check = hub.SystemChecks(now).Single(c => c.Key == "disk-writes");
            Assert.Equal("Schreiblast Speicherkarte", check.Label);
            Assert.Equal("ok", check.State);
            Assert.Contains("heute", check.Value);

            // Neustart: Zähler beginnt bei 0, alles seit dem Start zählt
            sectors = 4_096_000;                                          // ~2 GB seit dem Neustart (mehr als vorher)
            files["/proc/sys/kernel/random/boot_id"] = "bbbb";
            hub.SampleDiskWrites(now.AddHours(1));
            var state = System.Text.Json.JsonSerializer.Deserialize<DiskWriteState>(File.ReadAllText(Path.Combine(dir.Path, "disk-writes.json")))!;
            Assert.Equal("mmcblk0p2", state.Device);
            Assert.Equal((2_048_000L + 4_096_000L) * 512, state.Days["2026-10-10"]);

            // Am Folgetag: Schnitt der vollen Tage > 5 GB → Warnung
            sectors += 20_000_000;
            hub.SampleDiskWrites(now.AddHours(10));                      // 22:00 – noch der 10.
            var next = now.AddDays(1);
            var warn = hub.SystemChecks(next).Single(c => c.Key == "disk-writes");
            Assert.Equal("warn", warn.State);
            Assert.StartsWith("Ø", warn.Value);
            Assert.NotNull(warn.Hint);

            // Ohne passende Partition (z. B. Docker-Overlay) keine Zählung
            files["/proc/self/mountinfo"] = "25 1 0:52 / / rw - overlay overlay rw\n";
            hub.SampleDiskWrites(next);
            Assert.Equal(state.Days.Count, System.Text.Json.JsonSerializer.Deserialize<DiskWriteState>(File.ReadAllText(Path.Combine(dir.Path, "disk-writes.json")))!.Days.Count);
        }
        finally
        {
            hub.Dispose();
        }
    }

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
            ["/proc/mounts"] = $"/dev/mmcblk0p2 / ext4 rw,noatime 0 0\n/dev/sda1 {dir.Path.Replace('\\', '/')} ext4 ro,relatime 0 0\n",
        };
        hub.SystemFileReader = p => files.TryGetValue(p, out var v) ? v : null;
        hub.SystemCommandRunner = (_, _) => "no";                          // Uhrzeit nicht synchronisiert
        try
        {
            var checks = hub.SystemChecks(now).ToDictionary(c => c.Key);
            Assert.Equal("warn", checks["memory"].State);
            Assert.Equal("bad", checks["cpu-temp"].State);
            Assert.Equal("86,2 °C", checks["cpu-temp"].Value);
            Assert.Equal("bad", checks["power"].State);                 // Unterspannung jetzt
            Assert.Equal("warn", checks["cert"].State);                 // Zusatzprüfung des Servers
            Assert.Equal("ok", checks["disk"].State);
            Assert.Equal("bad", checks["readonly"].State);                // Datenordner liegt auf einem „ro“-Einhängepunkt
            Assert.Equal("warn", checks["clock"].State);
            Assert.False(checks.ContainsKey("restarts"));
            // Drei Starts in 30 Minuten → Absturzschleife?
            File.WriteAllText(Path.Combine(dir.Path, "starts.json"), System.Text.Json.JsonSerializer.Serialize(new[] { now.AddMinutes(-25), now.AddMinutes(-12), now.AddMinutes(-1) }));
            Assert.Equal("warn", hub.SystemChecks(now).Single(c => c.Key == "restarts").State);
            File.Delete(Path.Combine(dir.Path, "starts.json"));

            hub.TickSystemHealthForTest(now);
            hub.TickSystemHealthForTest(now.AddMinutes(5));            // erst nach 10 min erneut
            hub.TickSystemHealthForTest(now.AddMinutes(10));            // gleiche Probleme: Push höchstens einmal am Tag
            Assert.Equal(["system:memory", "system:cpu-temp", "system:power", "system:readonly", "system:clock", "system:cert"], sent);

            // Wieder in Ordnung → Protokoll
            files["/sys/class/thermal/thermal_zone0/temp"] = "52000\n";
            hub.TickSystemHealthForTest(now.AddMinutes(20));
            // Protokoll trägt die echte Uhrzeit ein, der Test arbeitet mit einem festen Datum → Fenster bis heute
            var log = hub.History!.QueryEvents(now.AddDays(-1), DateTime.Now.AddDays(1)).Select(e => e.Message).ToList();
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
