using System.Globalization;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Schreibmenge je Tag auf dem Datenträger des Datenordners (disk-writes.json, nur ergänzt).</summary>
public sealed class DiskWriteState
{
    /// <summary>Partition laut /proc/diskstats, z. B. "mmcblk0p2" oder "nvme0n1p2".</summary>
    public string Device { get; set; } = "";
    /// <summary>Zuletzt gelesener Zähler „Sektoren geschrieben“ (512 Byte, seit dem Systemstart).</summary>
    public long LastSectors { get; set; }
    public DateTime LastAt { get; set; }
    /// <summary>Kennung des Systemstarts (/proc/sys/kernel/random/boot_id) – anderer Wert: Zähler begann neu.</summary>
    public string BootId { get; set; } = "";
    /// <summary>Geschriebene Bytes je Tag ("yyyy-MM-dd"), die letzten 31 Tage.</summary>
    public Dictionary<string, long> Days { get; set; } = new();
}

/// <summary>
/// 0.9.20: Speicherkarte überwachen. Linux zählt in /proc/diskstats die geschriebenen Sektoren je Partition; alle 10 min
/// (mit dem Systemzustand) wird die Differenz dem Tag gutgeschrieben. Der Systemzustand zeigt den Schnitt je Tag und warnt,
/// wenn auf einer SD-Karte ungewöhnlich viel geschrieben wird (verschleißt sie). eMMC-Module melden zusätzlich ihren
/// Verschleiß (life_time).
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Ab dieser Menge je Tag warnt der Systemzustand bei einer SD-Karte.</summary>
    internal const long SdWarnBytesPerDay = 5L * 1024 * 1048576;

    private DiskWriteState? _diskWrites;
    private string DiskWritesFile => Path.Combine(DataDirectory, "disk-writes.json");

    /// <summary>Partition des Datenordners: Gerätenummer aus /proc/self/mountinfo, Name aus /proc/diskstats.</summary>
    private string? DataPartition()
    {
        if (ReadSystemFile("/proc/self/mountinfo") is not { } info || ReadSystemFile("/proc/diskstats") is not { } stats) return null;
        var full = Path.GetFullPath(DataDirectory).Replace('\\', '/').TrimEnd('/') + "/";
        string? majorMinor = null;
        var best = -1;
        foreach (var line in info.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // 36 25 179:2 / / rw,noatime shared:1 - ext4 /dev/root rw
            var parts = line.Split(' ');
            if (parts.Length < 5) continue;
            var mp = parts[4].TrimEnd('/') + "/";
            if (full.StartsWith(mp, StringComparison.Ordinal) && mp.Length > best) { best = mp.Length; majorMinor = parts[2]; }
        }
        if (majorMinor is null) return null;
        foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 9 && $"{p[0]}:{p[1]}" == majorMinor) return p[2];
        }
        return null;
    }

    /// <summary>Zähler „Sektoren geschrieben“ (Feld 10 in /proc/diskstats) der Partition.</summary>
    private long? SectorsWritten(string device)
    {
        if (ReadSystemFile("/proc/diskstats") is not { } stats) return null;
        foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 9 && p[2] == device && long.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        }
        return null;
    }

    private DiskWriteState LoadDiskWrites()
    {
        if (_diskWrites is not null) return _diskWrites;
        try { _diskWrites = File.Exists(DiskWritesFile) ? JsonSerializer.Deserialize<DiskWriteState>(File.ReadAllText(DiskWritesFile)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _diskWrites = null; }
        return _diskWrites ??= new DiskWriteState();
    }

    /// <summary>Zähler lesen und die Differenz seit dem letzten Mal dem heutigen Tag gutschreiben.</summary>
    internal void SampleDiskWrites(DateTime now)
    {
        if (DataPartition() is not { } device || SectorsWritten(device) is not { } sectors) return;
        var bootId = ReadSystemFile("/proc/sys/kernel/random/boot_id")?.Trim() ?? "";
        var s = LoadDiskWrites();
        if (s.Device != device || s.LastAt == default)
        {
            // Erster Lauf bzw. anderer Datenträger: nur den Stand merken
            s.Device = device;
        }
        else
        {
            // Neustart dazwischen (andere boot_id oder kleinerer Zähler) → alles seit dem Start zählt
            var rebooted = (bootId.Length > 0 && s.BootId.Length > 0 && bootId != s.BootId) || sectors < s.LastSectors;
            var delta = rebooted ? sectors : sectors - s.LastSectors;
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            s.Days[day] = s.Days.GetValueOrDefault(day) + delta * 512;
            foreach (var old in s.Days.Keys.Where(k => string.CompareOrdinal(k, now.AddDays(-31).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0).ToList())
                s.Days.Remove(old);
        }
        s.LastSectors = sectors;
        s.BootId = bootId;
        s.LastAt = now;
        try { File.WriteAllText(DiskWritesFile, JsonSerializer.Serialize(s)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nur Statistik */ }
    }

    /// <summary>Prüfung „Schreiblast“ (und bei eMMC „Verschleiß“) für den Systemzustand.</summary>
    private IEnumerable<SystemCheck> DiskWriteChecks(DateTime now)
    {
        var s = LoadDiskWrites();
        if (s.Device.Length == 0 || s.Days.Count == 0) yield break;
        var sd = s.Device.StartsWith("mmcblk", StringComparison.Ordinal);
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var full = s.Days.Where(d => d.Key != today && string.CompareOrdinal(d.Key, now.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0).ToList();
        var todayBytes = s.Days.GetValueOrDefault(today);
        long? avg = full.Count > 0 ? full.Sum(d => d.Value) / full.Count : null;
        var value = avg is { } a
            ? L.T("Ø {0} je Tag · heute {1}", Mb(a), Mb(todayBytes))
            : L.T("heute {0}", Mb(todayBytes));
        var high = (avg ?? 0) > SdWarnBytesPerDay;
        yield return new SystemCheck("disk-writes", sd ? L.T("Schreiblast Speicherkarte") : L.T("Schreiblast Datenträger"), value,
            sd && high ? "warn" : "ok",
            sd && high ? L.T("Auf die SD-Karte wird ungewöhnlich viel geschrieben – das verschleißt sie. Abfrageintervall und Verlauf prüfen oder den Server von einer USB-SSD starten.") : null);

        // eMMC (z. B. Compute Module): Verschleiß in 10-%-Schritten, 0x0B = Lebensdauer überschritten
        var disk = sd ? s.Device[..(s.Device.IndexOf('p') is var i && i > 0 ? i : s.Device.Length)] : null;
        if (disk is not null && ReadSystemFile($"/sys/block/{disk}/device/life_time") is { } life)
        {
            var worst = life.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0).DefaultIfEmpty(0).Max();
            if (worst > 0)
            {
                var used = Math.Min(worst * 10, 100);
                yield return new SystemCheck("disk-wear", L.T("Verschleiß Speicher"), L.T("bis {0} % verbraucht", used),
                    worst >= 11 ? "bad" : worst >= 9 ? "warn" : "ok",
                    worst >= 9 ? L.T("Der eingebaute Speicher ist weitgehend verbraucht – Sicherung bereithalten und auf eine SSD umziehen.") : null);
            }
        }
    }
}
