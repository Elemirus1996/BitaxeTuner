using System.Globalization;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Eine Prüfung des Systemzustands.</summary>
/// <param name="State">"ok", "warn" oder "bad".</param>
/// <param name="Hint">Was zu tun ist (nur bei warn/bad).</param>
public sealed record SystemCheck(string Key, string Label, string Value, string State, string? Hint = null)
{
    public bool Problem => State is "warn" or "bad";
}

/// <summary>
/// 0.9.16: Systemzustand – Speicher, Temp-Ordner, Sicherung, USB-Stick, Arbeitsspeicher, CPU-Temperatur und Unterspannung
/// (Raspberry Pi), abgebrochenes Update; der Server ergänzt das HTTPS-Zertifikat (<see cref="MinerHubOptions.ExtraSystemChecks"/>).
/// Alle 10 min geprüft; neue Probleme per Push (Kategorie Wartung, je Punkt höchstens einmal am Tag) und ins Protokoll,
/// ebenso wenn ein Punkt wieder in Ordnung ist.
/// </summary>
public sealed partial class MinerHub
{
    private DateTime _systemCheckedAt = DateTime.MinValue;
    private readonly Dictionary<string, string> _systemStates = new();

    /// <summary>Für Tests: Werte, die sonst aus /proc und /sys kommen (null = echte Dateien).</summary>
    internal Func<string, string?>? SystemFileReader { get; set; }

    private string? ReadSystemFile(string path)
    {
        if (SystemFileReader is { } f) return f(path);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Mb(long bytes) => bytes >= 1024L * 1048576
        ? L.T("{0} GB", (bytes / 1073741824.0).ToString("0.0", L.Culture))
        : L.T("{0} MB", (bytes / 1048576).ToString("N0", L.Culture));

    private static long? FreeBytesAt(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, cmp))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault()?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<SystemCheck> SystemChecks(DateTime now)
    {
        var list = new List<SystemCheck>();

        // Speicher im Datenordner
        if (FreeBytesAt(DataDirectory) is { } free)
        {
            var low = free < 500L * 1048576;
            list.Add(new SystemCheck("disk", L.T("Freier Speicher"), Mb(free), low ? "bad" : free < 1024L * 1048576 ? "warn" : "ok",
                free < 1024L * 1048576 ? L.T("Alte Sicherungen oder vorherige Stände löschen (Einstellungen → System → Speicherplatz).") : null));
        }

        // Temp-Ordner (Pi: /tmp ist oft eine kleine RAM-Disk)
        var temp = Path.GetTempPath();
        if (FreeBytesAt(temp) is { } tempFree)
            list.Add(new SystemCheck("temp", L.T("Temp-Ordner"), Mb(tempFree), tempFree < 256L * 1048576 ? "warn" : "ok",
                tempFree < 256L * 1048576 ? temp.TrimEnd('/', '\\') + ": " + L.T("Wenig Platz für Zwischendateien – auf dem Pi TMPDIR auf den Datenordner legen (/etc/default/bitaxetuner).") : null));

        // Sicherung
        var b = Config.Backup;
        if (b.Enabled)
        {
            var st = BackupStatus;
            if (st.LastRun is not { } last)
                list.Add(new SystemCheck("backup", L.T("Sicherung"), L.T("noch keine"), "ok"));
            else if (st.LastFile is null)
                list.Add(new SystemCheck("backup", L.T("Sicherung"), L.T("fehlgeschlagen ({0:g})", last), "bad",
                    string.Join("; ", st.Targets.Where(t => !t.Ok).Select(t => $"{t.Target}: {t.Message}"))));
            else if (now - last > TimeSpan.FromDays(2))
                list.Add(new SystemCheck("backup", L.T("Sicherung"), L.T("zuletzt {0:g}", last), "warn", L.T("Seit über zwei Tagen keine Sicherung – läuft der Server durchgehend?")));
            else if (st.LastOk != true)
                list.Add(new SystemCheck("backup", L.T("Sicherung"), L.T("{0:g}, nicht alle Ziele", last), "warn",
                    string.Join("; ", st.Targets.Where(t => !t.Ok).Select(t => $"{t.Target}: {t.Message}"))));
            else
                list.Add(new SystemCheck("backup", L.T("Sicherung"), L.T("zuletzt {0:g}", last), "ok"));
        }

        // USB-Stick als Sicherungsziel (Pi)
        if (OperatingSystem.IsLinux() && b.Enabled && b.Folder.Enabled && b.Folder.Path.StartsWith(FolderBackupTarget.PiUsbMount + "/", StringComparison.Ordinal))
        {
            var mounted = FolderBackupTarget.IsMounted(FolderBackupTarget.PiUsbMount);
            list.Add(new SystemCheck("usb", L.T("USB-Stick"), mounted ? L.T("eingehängt") : L.T("nicht eingehängt"), mounted ? "ok" : "bad",
                mounted ? null : L.T("Stick einstecken (FAT32, exFAT oder ext4, nicht NTFS). Wird er nicht erkannt, im Protokoll nach „USB-Stick“ suchen.")));
        }

        // Raspberry Pi / Linux: Arbeitsspeicher, CPU-Temperatur, Unterspannung
        if (ReadSystemFile("/proc/meminfo") is { } meminfo
            && meminfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal)) is { } line
            && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var availKb))
        {
            var avail = availKb * 1024;
            list.Add(new SystemCheck("memory", L.T("Arbeitsspeicher frei"), Mb(avail), avail < 100L * 1048576 ? "warn" : "ok",
                avail < 100L * 1048576 ? L.T("Sehr wenig Arbeitsspeicher – andere Programme auf dem Gerät beenden oder neu starten.") : null));
        }
        if (ReadSystemFile("/sys/class/thermal/thermal_zone0/temp") is { } tz && long.TryParse(tz.Trim(), out var milli))
        {
            var c = milli / 1000.0;
            list.Add(new SystemCheck("cpu-temp", L.T("CPU-Temperatur"), c.ToString("0.0", L.Culture) + " °C", c >= 85 ? "bad" : c >= 80 ? "warn" : "ok",
                c >= 80 ? L.T("Der Rechner wird heiß – Kühlkörper/Lüfter prüfen, nicht direkt neben die Miner stellen.") : null));
        }
        if (ReadSystemFile("/sys/devices/platform/soc/soc:firmware/get_throttled") is { } thr
            && int.TryParse(thr.Trim().Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
        {
            // Bit 0: Unterspannung jetzt, Bit 2: gedrosselt jetzt, Bit 16/18: seit dem Start vorgekommen
            var now0 = (flags & 0x1) != 0;
            var throttledNow = (flags & 0x4) != 0;
            var earlier = (flags & 0x50000) != 0;
            var value = now0 ? L.T("Unterspannung") : throttledNow ? L.T("gedrosselt") : earlier ? L.T("seit dem Start vorgekommen") : L.T("in Ordnung");
            list.Add(new SystemCheck("power", L.T("Stromversorgung (Pi)"), value, now0 || throttledNow ? "bad" : earlier ? "warn" : "ok",
                now0 || throttledNow || earlier ? L.T("Original-Netzteil (5 V, 3 A bzw. 5 A beim Pi 5) und kurzes Kabel verwenden – Unterspannung kann die Speicherkarte beschädigen.") : null));
        }

        // Abgebrochenes Update
        if (LastUpdateError is { } ue)
            list.Add(new SystemCheck("update", L.T("Server-Update"), L.T("auf {0} abgebrochen", ue.Version), "warn", ue.Error));

        try { if (Options.ExtraSystemChecks?.Invoke(this) is { } extra) list.AddRange(extra); }
        catch { /* Zusatzprüfungen sind optional */ }
        return list;
    }

    /// <summary>Alle 10 min prüfen; Änderungen ins Protokoll, Probleme per Push.</summary>
    private void TickSystemHealth(DateTime now)
    {
        if (now - _systemCheckedAt < TimeSpan.FromMinutes(10)) return;
        _systemCheckedAt = now;
        IReadOnlyList<SystemCheck> checks;
        try { checks = SystemChecks(now); }
        catch { return; }
        foreach (var c in checks)
        {
            var before = _systemStates.TryGetValue(c.Key, out var s) ? s : null;
            _systemStates[c.Key] = c.State;
            if (c.Problem)
            {
                if (before != c.State)
                    LogEvent(null, EventCategories.System, L.T("Systemzustand: {0} – {1}", c.Label, c.Value) + (c.Hint is { Length: > 0 } h ? " (" + h + ")" : ""));
                SendAlert(new Alert($"system:{c.Key}", L.T("Systemzustand: {0}", c.Label), c.Value + (c.Hint is { Length: > 0 } hint ? "\n" + hint : ""),
                    c.State == "bad" ? NotifyPriority.High : NotifyPriority.Normal, TimeSpan.FromHours(24), NotifyCategory.Maintenance));
            }
            else if (before is "warn" or "bad")
                LogEvent(null, EventCategories.System, L.T("Systemzustand: {0} wieder in Ordnung ({1}).", c.Label, c.Value));
        }
    }

    internal void TickSystemHealthForTest(DateTime now) => TickSystemHealth(now);
}
