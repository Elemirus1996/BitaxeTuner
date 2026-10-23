using System.Globalization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Stand des Server-Lüfters (Raspberry Pi 5).</summary>
/// <param name="Present">Lüfter am Board erkannt (Kühlgerät „pwm-fan“).</param>
/// <param name="Level">Aktuelle Stufe (0 … <paramref name="MaxLevel"/>).</param>
/// <param name="Firmware">true = die Firmware regelt (Thermal-Regler „step_wise“), false = BitaxeTuner (user_space).</param>
/// <param name="Wanted">Von BitaxeTuner gewünschte Stufe (null = Firmware).</param>
public sealed record PiFanStatus(bool Present, int? Rpm, double? CpuTemp, int? Level, int MaxLevel, bool Firmware, int? Wanted, string Reason, string? Error,
    bool CanControl = false);

/// <summary>
/// 0.9.20: Lüfter des Servers (Raspberry Pi 5). Lesen ohne Sonderrechte aus sysfs (Drehzahl, CPU-Temperatur, Stufe);
/// Steuern über den Root-Dienst: BitaxeTuner schreibt &lt;Datenordner&gt;/pifan-request („auto“ oder „level N“), die Pfad-Unit
/// bitaxetuner-pifan.path startet /usr/local/lib/bitaxetuner/pifan-set. Solange BitaxeTuner regelt, frischt er
/// pifan-heartbeat auf; der Wächter (Timer, jede Minute) gibt nach 3 Minuten ohne Lebenszeichen an die Firmware zurück.
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Für Tests: Wurzel statt „/“ für /sys (z. B. ein Testordner).</summary>
    internal string PiFanSysRoot { get; set; } = "";

    /// <summary>Für Tests: Steuerung möglich, ohne dass die Systemdateien installiert sind.</summary>
    internal bool? PiFanControlOverride { get; set; }

    /// <summary>Kann BitaxeTuner den Lüfter steuern? (Linux mit eingerichteter Pfad-Unit bitaxetuner-pifan.path)</summary>
    public bool PiFanCanControl => PiFanControlOverride ?? (OperatingSystem.IsLinux() && File.Exists("/etc/systemd/system/bitaxetuner-pifan.path"));

    private int? _piFanWanted;
    private DateTime _piFanRequestedAt = DateTime.MinValue;
    private string _piFanReason = "";

    /// <summary>Stufen der CPU-Kurven (°C ab Stufe 1, 2, 3, 4) – „balanced“ entspricht etwa der Pi-Firmware.</summary>
    internal static double[] PresetSteps(string preset) => preset switch
    {
        "quiet" => [60, 67, 74, 80],
        "cool" => [-1, 40, 50, 60],      // nie ganz aus
        _ => [50, 60, 67, 75],
    };

    private string Sys(string path) => PiFanSysRoot.Length == 0 ? path : PiFanSysRoot.TrimEnd('/', '\\') + path;

    private string? ReadSys(string path)
    {
        try { var f = Sys(path); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> SysDirs(string dir, string prefix)
    {
        try
        {
            var d = Sys(dir);
            return Directory.Exists(d) ? Directory.GetDirectories(d, prefix + "*").Concat(Directory.GetFiles(d, prefix + "*")).OrderBy(x => x, StringComparer.Ordinal) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private string? Rel(string full) => PiFanSysRoot.Length == 0 ? full : full[PiFanSysRoot.TrimEnd('/', '\\').Length..].Replace('\\', '/');

    /// <summary>Lüfter-Kühlgerät (type = pwm-fan) und CPU-Thermalzone suchen.</summary>
    private (string? Cooling, string? Zone, string? Hwmon) PiFanPaths()
    {
        string? cooling = null, zone = null, hwmon = null;
        foreach (var d in SysDirs("/sys/class/thermal", "cooling_device"))
            if (ReadSys(Rel(d) + "/type") == "pwm-fan") { cooling = Rel(d); break; }
        foreach (var d in SysDirs("/sys/class/thermal", "thermal_zone"))
            if (ReadSys(Rel(d) + "/type") is "cpu-thermal" or "cpu_thermal") { zone = Rel(d); break; }
        foreach (var d in SysDirs("/sys/class/hwmon", "hwmon"))
            if (ReadSys(Rel(d) + "/name") == "pwmfan") { hwmon = Rel(d); break; }
        return (cooling, zone ?? (ReadSys("/sys/class/thermal/thermal_zone0/temp") is not null ? "/sys/class/thermal/thermal_zone0" : null), hwmon);
    }

    public PiFanStatus PiFan
    {
        get
        {
            var (cooling, zone, hwmon) = PiFanPaths();
            double? cpu = zone is not null && long.TryParse(ReadSys(zone + "/temp"), out var milli) ? milli / 1000.0 : null;
            if (cooling is null) return new PiFanStatus(false, null, cpu, null, 0, true, null, "", null);
            int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            var policy = zone is not null ? ReadSys(zone + "/policy") : null;
            // 0.9.24: BitaxeTuner regelt auch, solange die Werks-Schaltpunkte gesichert sind (Steuerung über Schaltpunkte)
            var byTrips = File.Exists(Path.Combine(DataDirectory, "pifan-trips"));
            return new PiFanStatus(true, hwmon is not null ? Int(ReadSys(hwmon + "/fan1_input")) : null, cpu,
                Int(ReadSys(cooling + "/cur_state")), Int(ReadSys(cooling + "/max_state")) ?? 4, policy != "user_space" && !byTrips,
                _piFanWanted, _piFanReason, ReadPiFanError(), PiFanCanControl);
        }
    }

    private string? ReadPiFanError()
    {
        string? code;
        try { var f = Path.Combine(DataDirectory, "pifan-error"); code = File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
        catch (IOException) { return null; }
        return code switch
        {
            null or "" => null,
            "guard" => L.T("Der Wächter hat die Regelung an die Firmware zurückgegeben – BitaxeTuner hatte sich zu lange nicht gemeldet."),
            "nouserspace" => L.T("Der Kernel erlaubt keine eigene Lüftersteuerung (Schaltpunkte nur lesbar, Regler „user_space“ fehlt) – die Firmware regelt weiter."),
            _ => L.T("Lüfterstufe konnte nicht gesetzt werden ({0}) – die Firmware regelt weiter.", code),
        };
    }

    /// <summary>Stufe für eine CPU-Kurve mit 3 °C Abstand beim Herunterschalten.</summary>
    internal static int CpuLevel(string preset, double cpu, int? last, int maxLevel)
    {
        var steps = PresetSteps(preset);
        int Level(double t) => steps.Count(s => t >= s);
        var up = Level(cpu);
        var down = Level(cpu + 3);
        var next = last is { } l ? (up > l ? up : down < l ? down : l) : up;
        return Math.Clamp(next, 0, maxLevel);
    }

    /// <summary>Prozent (Gehäuse-Kurve) → Stufe 0–4.</summary>
    internal static int PercentToLevel(int percent, int maxLevel) => Math.Clamp(percent switch
    {
        <= 0 => 0,
        <= 30 => 1,
        <= 55 => 2,
        <= 80 => 3,
        _ => 4,
    }, 0, maxLevel);

    /// <summary>Aus dem Lüfter-Takt: gewünschte Stufe berechnen und bei Änderung an den Root-Dienst geben.</summary>
    internal void TickPiFan(DateTime now, IReadOnlyList<MinerTemps> miners)
    {
        var s = Config.PiFan;
        var st = PiFan;
        if (!st.Present || !st.CanControl) return;
        if (s.Mode == "auto")
        {
            if (_piFanWanted is not null || !st.Firmware) RequestPiFan("auto", now);
            _piFanWanted = null;
            _piFanReason = L.T("Firmware regelt (Automatisch)");
            return;
        }
        if (st.CpuTemp is not { } cpu)
        {
            RequestPiFan("level " + st.MaxLevel, now);   // ohne CPU-Temperatur: sicherheitshalber voll
            _piFanWanted = st.MaxLevel;
            _piFanReason = L.T("CPU-Temperatur unbekannt → volle Stufe");
            return;
        }
        int level;
        if (s.Mode == "curve")
        {
            level = CpuLevel(s.Preset, cpu, _piFanWanted, st.MaxLevel);
            _piFanReason = L.T("Kurve „{0}“: CPU {1} °C → Stufe {2}", PresetName(s.Preset), cpu.ToString("0.0", L.Culture), level);
        }
        else
        {
            var cpuMin = CpuLevel("balanced", cpu, _piFanWanted, st.MaxLevel);
            var temps = miners.Where(m => s.Miners.Count == 0 || s.Miners.Contains(m.Host, StringComparer.OrdinalIgnoreCase))
                .Where(m => m.Online && m.LastOk is { } ok && now - ok <= FanController.StaleAfter)
                .Select(m => s.Sensor == "asic" ? m.AsicTemp ?? m.VrTemp : m.VrTemp ?? m.AsicTemp)
                .Where(t => t is > 0).Select(t => t!.Value).ToList();
            var minerLevel = temps.Count == 0 ? st.MaxLevel : PercentToLevel(FanController.Evaluate(s.Curve, temps.Max()), st.MaxLevel);
            level = Math.Max(cpuMin, minerLevel);
            _piFanReason = temps.Count == 0
                ? L.T("keine aktuellen Miner-Temperaturen → volle Stufe")
                : L.T("Miner {0} °C, CPU {1} °C → Stufe {2}", temps.Max().ToString("0.0", L.Culture), cpu.ToString("0.0", L.Culture), level);
        }
        // 0.9.21 Ruhezeit: höchste Stufe begrenzen – nie ab 75 °C CPU
        if (Config.Quiet.IsQuiet(now) && Config.Quiet.ServerFanMaxLevel is { } cap && cpu < QuietHoursSettings.ServerFanSafetyCpu && level > cap)
        {
            level = cap;
            _piFanReason += L.T(", Ruhezeit max. Stufe {0}", cap);
        }
        var changed = level != _piFanWanted || st.Firmware || st.Level != level;
        _piFanWanted = level;
        if (changed && now - _piFanRequestedAt > TimeSpan.FromSeconds(20)) RequestPiFan("level " + level, now);
        TouchPiFanHeartbeat();
    }

    internal static string PresetName(string preset) => preset switch
    {
        "quiet" => L.T("leise"),
        "cool" => L.T("kühl"),
        _ => L.T("ausgewogen"),
    };

    private void RequestPiFan(string request, DateTime now)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDirectory, "pifan-request"), request + "\n");
            _piFanRequestedAt = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nächster Takt */ }
    }

    private void TouchPiFanHeartbeat()
    {
        // Inhalt = zulässiges Alter in Sekunden (mind. 3 min bzw. 3 Abfrage-Abstände) – danach gibt der Wächter an die Firmware zurück
        var maxAge = Math.Max(180, 3 * Math.Clamp(Config.IntervalSeconds, 1, 300));
        try { File.WriteAllText(Path.Combine(DataDirectory, "pifan-heartbeat"), maxAge.ToString(CultureInfo.InvariantCulture) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Einstellungen prüfen und übernehmen.</summary>
    public void SavePiFan(PiFanSettings s, string by)
    {
        if (!PiFanSettings.Modes.Contains(s.Mode)) throw new LocalizedException("Unbekannte Art: {0}", s.Mode);
        s.Preset = PiFanSettings.Presets.Contains(s.Preset) ? s.Preset : "balanced";
        s.Sensor = s.Sensor == "asic" ? "asic" : "vr";
        s.Miners = (s.Miners ?? []).Where(h => Device(h) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Curve ??= new FanCurve();
        s.Curve.StartTemp = Math.Clamp(s.Curve.StartTemp, 20, 95);
        s.Curve.FullTemp = Math.Clamp(s.Curve.FullTemp, s.Curve.StartTemp + 1, 100);
        s.Curve.StartPercent = Math.Clamp(s.Curve.StartPercent, 0, 100);
        s.Curve.MinPercent = Math.Clamp(s.Curve.MinPercent, 0, 100);
        Config.PiFan = s;
        Config.Save();
        _piFanWanted = null;
        _piFanRequestedAt = DateTime.MinValue;
        // Zurück auf „Automatisch“: sofort und ausdrücklich an die Firmware geben (nicht erst, wenn der Zustand es verrät)
        if (s.Mode == "auto" && PiFan is { Present: true, CanControl: true }) RequestPiFan("auto", Options.Clock?.Invoke() ?? DateTime.Now);
        LogEvent(null, EventCategories.Fans, L.T("Server-Lüfter: {0} – {1}", s.Mode switch
        {
            "curve" => L.T("Kurve „{0}“", PresetName(s.Preset)),
            "miners" => L.T("nach Miner-Temperatur"),
            _ => L.T("Automatisch (Firmware)"),
        }, by));
    }
}
