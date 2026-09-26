using System.Globalization;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Core.Automation;

/// <summary>Von einer Regel beschlossene Änderung.</summary>
public sealed record AutomationAction(string Host, string Rule, int FrequencyMhz, int CoreVoltageMv, string Reason);

/// <summary>Hinweis ohne Änderung (z. B. Minimum erreicht, Voreinstellung fehlt).</summary>
public sealed record AutomationNotice(string Host, string Rule, string Message);

public sealed class AutomationResult
{
    public AutomationAction? Action { get; set; }
    public List<AutomationNotice> Notices { get; } = [];
    /// <summary>Kurzstatus für die Anzeige.</summary>
    public string Status { get; set; } = "";
}

/// <summary>
/// Temperaturschutz und Zeitplan/Strompreis. Regeln handeln nur, wenn sie freigegeben und unverändert sind
/// (<see cref="AutomationRule.IsApproved"/>). Nie während Benchmark, Dauertest-Start oder Wartung;
/// zwischen zwei automatischen Änderungen desselben Miners liegen mindestens <see cref="MinGap"/>.
/// </summary>
public sealed class AutomationEngine
{
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);
    private const int MaxAttemptsPerTarget = 3;

    private sealed class HostState
    {
        public DateTime? OverSince;
        public DateTime? CoolSince;
        /// <summary>Frequenz vor dem ersten Eingriff des Temperaturschutzes (null = nicht abgesenkt).</summary>
        public int? OriginalFrequency;
        public DateTime LastAction = DateTime.MinValue;
        public string? ScheduleTarget;
        public int Attempts;
        public string? LastNotice;
    }

    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriceService? _prices;

    public AutomationEngine(PriceService? prices) => _prices = prices;

    /// <summary>Ist der Temperaturschutz für diesen Miner gerade aktiv abgesenkt?</summary>
    public int? GuardOriginalFrequency(string host) => _hosts.TryGetValue(host, out var s) ? s.OriginalFrequency : null;

    /// <param name="scheduleBlocked">Zeitplan/Preis-Regel pausieren (z. B. während eines Dauertests); Temperaturschutz bleibt aktiv.</param>
    public AutomationResult Evaluate(DeviceConfig device, MinerInfo info, DeviceProfile? profile, DateTime now,
                                     bool busy, bool maintenance, bool scheduleBlocked = false)
    {
        var result = new AutomationResult();
        var host = device.Host.Trim();
        if (!_hosts.TryGetValue(host, out var st)) _hosts[host] = st = new HostState();

        var guard = device.ThermalGuard;
        var schedule = device.Schedule;
        var guardOn = guard.IsApproved(host);
        var scheduleOn = schedule.IsApproved(host);
        result.Status = StatusText(device, host, st, guardOn, scheduleOn);

        if (maintenance)
        {
            // Werte direkt nach einem Neustart sind nicht aussagekräftig
            st.OverSince = null;
            st.CoolSince = null;
            return result;
        }
        if (busy) return result;
        var sinceLast = now - st.LastAction;

        // ---- 1. Temperaturschutz (hat Vorrang) ----
        if (guardOn)
        {
            var chip = info.MaxChipTempC ?? 0;
            var vr = info.VrTempC ?? 0;
            var over = chip > guard.MaxChipTempC || vr > guard.MaxVrTempC;
            var cool = chip <= guard.MaxChipTempC - 5 && vr <= guard.MaxVrTempC - 5;
            var hold = TimeSpan.FromMinutes(Math.Max(1, guard.Minutes));

            if (over)
            {
                st.CoolSince = null;
                st.OverSince ??= now;
                if (now - st.OverSince >= hold && sinceLast >= hold)
                {
                    var target = Math.Max(guard.MinFrequencyMhz, info.FrequencyMhz - Math.Max(1, guard.StepMhz));
                    if (target < info.FrequencyMhz)
                    {
                        st.OriginalFrequency ??= info.FrequencyMhz;
                        st.OverSince = now;
                        st.LastAction = now;
                        result.Action = new AutomationAction(host, "Temperaturschutz", target, info.CoreVoltageMv,
                            $"Temperaturschutz: Chip {F(chip)} °C / VR {F(vr)} °C seit {guard.Minutes} min über der Grenze");
                        return result;
                    }
                    Notice(result, st, host, "Temperaturschutz",
                        $"Minimum {guard.MinFrequencyMhz} MHz erreicht, trotzdem Chip {F(chip)} °C / VR {F(vr)} °C – bitte Kühlung prüfen.");
                }
                return result; // solange zu heiß, nichts anderes tun
            }

            st.OverSince = null;
            if (st.OriginalFrequency is { } original)
            {
                if (!guard.Recover || info.FrequencyMhz >= original)
                {
                    if (info.FrequencyMhz >= original) st.OriginalFrequency = null;
                    else return result; // bleibt abgesenkt, Zeitplan pausiert
                }
                else if (cool)
                {
                    st.CoolSince ??= now;
                    if (now - st.CoolSince >= TimeSpan.FromMinutes(Math.Max(5, guard.RecoverMinutes)) && sinceLast >= MinGap)
                    {
                        var target = Math.Min(original, info.FrequencyMhz + Math.Max(1, guard.StepMhz));
                        st.CoolSince = now;
                        st.LastAction = now;
                        if (target >= original) st.OriginalFrequency = null;
                        result.Action = new AutomationAction(host, "Temperaturschutz", target, info.CoreVoltageMv,
                            $"Temperaturschutz: seit {guard.RecoverMinutes} min kühl, schrittweise zurück Richtung {original} MHz");
                    }
                    return result;
                }
                else
                {
                    st.CoolSince = null;
                    return result;
                }
            }
        }
        else
        {
            st.OriginalFrequency = null;
            st.OverSince = null;
            st.CoolSince = null;
        }

        // ---- 2. Zeitplan / Strompreis ----
        if (!scheduleOn) return result;
        if (scheduleBlocked)
        {
            result.Status += " · pausiert (Dauertest)";
            return result;
        }

        string? presetName;
        if (schedule.Mode == "price")
        {
            var price = _prices?.PriceAt(now.ToUniversalTime());
            if (price is null)
            {
                Notice(result, st, host, "Strompreis", "Kein aktueller Strompreis verfügbar – Voreinstellung bleibt unverändert.");
                return result;
            }
            presetName = price <= schedule.ThresholdCt ? schedule.CheapPreset : schedule.ExpensivePreset;
            result.Status += $" · Preis {F(price.Value, "0.0")} ct/kWh";
        }
        else
        {
            presetName = schedule.Entries.FirstOrDefault(e => e.Matches(now))?.Preset ?? schedule.DefaultPreset;
        }
        if (string.IsNullOrWhiteSpace(presetName)) return result;

        var preset = device.Presets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            Notice(result, st, host, "Zeitplan", $"Voreinstellung „{presetName}“ gibt es nicht.");
            return result;
        }
        if (profile is not null && (preset.FrequencyMhz < profile.MinFrequencyMhz || preset.FrequencyMhz > profile.MaxFrequencyMhz ||
                                    preset.CoreVoltageMv < profile.MinVoltageMv || preset.CoreVoltageMv > profile.MaxVoltageMv))
        {
            Notice(result, st, host, "Zeitplan", $"„{preset}“ liegt außerhalb der Grenzen für {profile.Name} – wird nicht gesetzt.");
            return result;
        }

        if (info.FrequencyMhz == preset.FrequencyMhz && info.CoreVoltageMv == preset.CoreVoltageMv)
        {
            st.ScheduleTarget = preset.Name;
            st.Attempts = 0;
            return result;
        }
        if (st.ScheduleTarget != preset.Name) { st.ScheduleTarget = preset.Name; st.Attempts = 0; }
        if (sinceLast < MinGap) return result;
        if (st.Attempts >= MaxAttemptsPerTarget)
        {
            Notice(result, st, host, "Zeitplan", $"„{preset.Name}“ wurde {MaxAttemptsPerTarget}× gesetzt, der Miner übernimmt sie nicht – pausiert bis zum nächsten Wechsel.");
            return result;
        }

        st.Attempts++;
        st.LastAction = now;
        result.Action = new AutomationAction(host, schedule.Mode == "price" ? "Strompreis" : "Zeitplan",
            preset.FrequencyMhz, preset.CoreVoltageMv,
            schedule.Mode == "price" ? $"Strompreis: „{preset.Name}“" : $"Zeitplan: „{preset.Name}“");
        return result;
    }

    private static void Notice(AutomationResult r, HostState st, string host, string rule, string message)
    {
        // Denselben Hinweis nicht bei jeder Abfrage wiederholen
        if (st.LastNotice == message) return;
        st.LastNotice = message;
        r.Notices.Add(new AutomationNotice(host, rule, message));
    }

    private static string StatusText(DeviceConfig d, string host, HostState st, bool guardOn, bool scheduleOn)
    {
        var parts = new List<string>();
        if (d.ThermalGuard.Enabled)
            parts.Add(guardOn
                ? st.OriginalFrequency is { } o ? $"Temperaturschutz aktiv (abgesenkt, ursprünglich {o} MHz)" : "Temperaturschutz bereit"
                : "Temperaturschutz: Freigabe fehlt");
        if (d.Schedule.Enabled)
            parts.Add(scheduleOn
                ? $"{(d.Schedule.Mode == "price" ? "Strompreis-Regel" : "Zeitplan")} aktiv{(st.ScheduleTarget is { } t ? $" (Ziel „{t}“)" : "")}"
                : $"{(d.Schedule.Mode == "price" ? "Strompreis-Regel" : "Zeitplan")}: Freigabe fehlt");
        return parts.Count == 0 ? "keine Automatik" : string.Join(" · ", parts);
    }

    private static string F(double v, string format = "0.0") => v.ToString(format, CultureInfo.GetCultureInfo("de-DE"));
}

// ---------------------------------------------------------------------------------------------
// Dauertest
// ---------------------------------------------------------------------------------------------

public enum SoakOutcome
{
    Running,
    Passed,
    Failed,
    Aborted,
}

public sealed record SoakResult(SoakOutcome Outcome, string Message);

/// <summary>
/// Beobachtet eine Einstellung über Stunden: Hashrate-Anteil (gleitend 15 min), Fehlerrate, Temperaturen,
/// Erreichbarkeit. Die ersten 10 min zählen als Anlaufphase.
/// </summary>
public sealed class SoakMonitor
{
    public static readonly TimeSpan Warmup = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const double RatioThreshold = 0.93;
    public const double MaxErrorPercent = 3;

    private readonly Queue<(DateTime Time, double Hash, double Expected, double? Error)> _samples = new();
    private int _hotSamples;
    private DateTime? _offlineSince;

    public double? CurrentRatio { get; private set; }

    public SoakResult Feed(SoakTestState soak, MinerInfo? info, DeviceProfile? profile, DateTime now, bool maintenance)
    {
        if (now >= soak.Until)
            return new SoakResult(SoakOutcome.Passed,
                $"Dauertest bestanden: {soak.FrequencyMhz} MHz / {soak.CoreVoltageMv} mV lief {(soak.Until - soak.StartedAt).TotalHours:0} h stabil" +
                (CurrentRatio is { } r ? $" (zuletzt {r:P1} der Soll-Hashrate)." : "."));

        if (info is null)
        {
            if (maintenance) return Running(soak, now);
            _offlineSince ??= now;
            return now - _offlineSince > TimeSpan.FromMinutes(10)
                ? new SoakResult(SoakOutcome.Failed, "Miner seit über 10 min nicht erreichbar.")
                : Running(soak, now);
        }
        _offlineSince = null;

        if (info.FrequencyMhz != soak.FrequencyMhz || info.CoreVoltageMv != soak.CoreVoltageMv)
            return new SoakResult(SoakOutcome.Aborted,
                $"Einstellung wurde geändert ({info.FrequencyMhz} MHz / {info.CoreVoltageMv} mV) – Dauertest beendet.");

        if (profile is not null)
        {
            var hot = info.MaxChipTempC > profile.MaxChipTempC || info.VrTempC > profile.MaxVrTempC;
            _hotSamples = hot ? _hotSamples + 1 : 0;
            if (_hotSamples >= 3)
                return new SoakResult(SoakOutcome.Failed,
                    $"Temperatur über der Grenze (Chip {info.MaxChipTempC:0.0} °C / VR {info.VrTempC:0} °C, Grenzen {profile.MaxChipTempC:0}/{profile.MaxVrTempC:0} °C).");
        }

        if (now - soak.StartedAt < Warmup) return Running(soak, now);

        var expected = info.ExpectedHashRateGh ?? 0;
        _samples.Enqueue((now, info.HashRateGh, expected, info.ErrorPercent));
        while (_samples.Count > 0 && now - _samples.Peek().Time > Window) _samples.Dequeue();
        var windowFull = _samples.Count >= 3 && now - _samples.Peek().Time >= Window - TimeSpan.FromMinutes(1);

        var exp = _samples.Where(s => s.Expected > 0).ToList();
        CurrentRatio = exp.Count > 0 ? exp.Average(s => s.Hash) / exp.Average(s => s.Expected) : null;

        if (windowFull && CurrentRatio is { } ratio && ratio < RatioThreshold)
            return new SoakResult(SoakOutcome.Failed, $"Hashrate nur {ratio:P1} der Soll-Hashrate (Ø 15 min, Grenze {RatioThreshold:P0}).");

        var errors = _samples.Where(s => s.Error.HasValue).Select(s => s.Error!.Value).ToList();
        if (windowFull && errors.Count > 0 && errors.Average() > MaxErrorPercent)
            return new SoakResult(SoakOutcome.Failed, $"Fehlerrate Ø {errors.Average():0.00} % (Grenze {MaxErrorPercent:0} %).");

        return Running(soak, now);
    }

    private SoakResult Running(SoakTestState soak, DateTime now)
    {
        var done = (now - soak.StartedAt).TotalMinutes / Math.Max(1, (soak.Until - soak.StartedAt).TotalMinutes);
        var phase = now - soak.StartedAt < Warmup ? "Anlaufphase" : CurrentRatio is { } r ? $"{r:P1} der Soll-Hashrate" : "misst";
        return new SoakResult(SoakOutcome.Running,
            $"Dauertest {soak.FrequencyMhz} MHz / {soak.CoreVoltageMv} mV · {Math.Clamp(done, 0, 1):P0} · {phase} · bis {soak.Until:dd.MM. HH:mm}");
    }

    /// <summary>Nächstniedrigere stabile Einstellung aus Benchmark-Ergebnissen (höchste Frequenz darunter, niedrigste Spannung).</summary>
    public static (int Frequency, int Voltage)? SuggestLower(IEnumerable<Benchmark.StepResult> results, int currentFrequency) =>
        results.Where(r => r.IsStable && r.FrequencyMhz < currentFrequency)
               .OrderByDescending(r => r.FrequencyMhz).ThenBy(r => r.CoreVoltageMv)
               .Select(r => ((int, int)?)(r.FrequencyMhz, r.CoreVoltageMv))
               .FirstOrDefault();
}
