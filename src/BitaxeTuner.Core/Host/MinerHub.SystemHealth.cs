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

    /// <summary>Für Tests: Ausgabe von Systembefehlen (z. B. timedatectl); null = Befehl wirklich ausführen.</summary>
    internal Func<string, string[], string?>? SystemCommandRunner { get; set; }

    /// <summary>Kurzer Systembefehl (höchstens 3 s), Ausgabe oder null.</summary>
    private string? RunSystemCommand(string file, params string[] args)
    {
        if (SystemCommandRunner is { } f) return f(file, args);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { /* egal */ } return null; }
            return output.Result.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    private string StartsFile => Path.Combine(DataDirectory, "starts.json");

    /// <summary>0.9.16: Start merken (letzte 20) – mehrere Starts kurz hintereinander deuten auf eine Absturzschleife.</summary>
    private void RecordStart(DateTime now)
    {
        try
        {
            var list = File.Exists(StartsFile) ? System.Text.Json.JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(StartsFile)) ?? [] : [];
            list.Add(now);
            File.WriteAllText(StartsFile, System.Text.Json.JsonSerializer.Serialize(list.TakeLast(20).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { /* nur Hinweis */ }
    }

    private int RecentStarts(DateTime now, TimeSpan window)
    {
        try
        {
            return File.Exists(StartsFile)
                ? (System.Text.Json.JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(StartsFile)) ?? []).Count(t => now - t < window && t <= now)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return 0; }
    }

    /// <summary>Einhängepunkt des Datenordners schreibgeschützt? (Linux: /proc/mounts, längster passender Einhängepunkt)</summary>
    private bool? DataMountReadOnly()
    {
        if (ReadSystemFile("/proc/mounts") is not { } mounts) return null;
        var full = Path.GetFullPath(DataDirectory).Replace('\\', '/').TrimEnd('/') + "/";
        string? options = null;
        var best = -1;
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length < 4) continue;
            var mp = parts[1].TrimEnd('/') + "/";
            if (full.StartsWith(mp, StringComparison.Ordinal) && mp.Length > best) { best = mp.Length; options = parts[3]; }
        }
        return options?.Split(',').Contains("ro");
    }

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
        // 0.9.20: Lüfter des Servers (Raspberry Pi 5) – steht er, obwohl er laufen soll?
        if (PiFanSysRoot.Length > 0 || OperatingSystem.IsLinux())
        {
            var pf = PiFan;
            if (pf.Present)
            {
                var stalled = pf.Level is > 0 && pf.Rpm == 0;
                // 0.9.20: wer regelt – auch auf der E-Paper-Seite „Systemzustand“ sichtbar
                var by = pf.Firmware ? L.T("Firmware") : Config.PiFan.Mode == "miners" ? L.T("Miner") : L.T("Kurve");
                var value = (pf.Rpm is { } rpm ? L.T("{0} U/min", rpm.ToString("N0", L.Culture)) : "–") + " · " + L.T("Stufe {0}/{1}", pf.Level?.ToString(L.Culture) ?? "–", pf.MaxLevel)
                            + " · " + by;
                list.Add(new SystemCheck("server-fan", L.T("Server-Lüfter"), value, stalled ? "bad" : pf.Error is not null ? "warn" : "ok",
                    stalled ? L.T("Der Lüfter des Servers soll laufen, dreht aber nicht – Stecker und Lüfter prüfen.") : pf.Error));
            }
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

        // 0.9.16: Speicherkarte schreibgeschützt (stirbt eine SD-Karte, hängt Linux sie oft nur noch lesend ein)
        if (DataMountReadOnly() is true)
            list.Add(new SystemCheck("readonly", L.T("Speicherkarte"), L.T("schreibgeschützt"), "bad",
                L.T("Das Dateisystem wurde nur noch lesend eingehängt – oft ein Zeichen, dass die Speicherkarte ausfällt. Sicherung vom USB-Stick/NAS bereithalten und die Karte ersetzen.")));

        // Uhrzeit (Linux mit systemd): ohne Zeitabgleich scheitern Zertifikate und Kursabrufe
        if (OperatingSystem.IsLinux() || SystemCommandRunner is not null)
        {
            var synced = ReadSystemFile("/run/systemd/timesync/synchronized") is not null
                ? "yes"
                : RunSystemCommand("timedatectl", "show", "-p", "NTPSynchronized", "--value");
            if (synced is "yes" or "no")
                list.Add(new SystemCheck("clock", L.T("Uhrzeit"), synced == "yes" ? L.T("synchronisiert") : L.T("nicht synchronisiert"), synced == "yes" ? "ok" : "warn",
                    synced == "yes" ? null : L.T("Zeitabgleich (NTP) prüfen: „timedatectl“ im SSH-Terminal. Eine falsche Uhrzeit lässt Zertifikate und Kursabrufe scheitern.")));
        }

        // Gehäufte Neustarts (Absturzschleife)
        var starts = RecentStarts(now, TimeSpan.FromMinutes(30));
        if (starts >= 3)
            list.Add(new SystemCheck("restarts", L.T("Neustarts"), L.T("{0} in 30 Minuten", starts), "warn",
                L.T("Der Dienst startet auffällig oft neu – im Protokoll bzw. mit „journalctl -u bitaxetuner -n 100“ nach der Ursache sehen.")));

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
