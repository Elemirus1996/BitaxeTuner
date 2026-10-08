using System.Globalization;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.I18n;

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

    /// <summary>0.9.11: Einheit des Strompreises in der gewählten Währung (z. B. „ct/kWh“, „p/kWh“).</summary>
    public Func<string> CentPerKwh { get; set; } = () => "ct/kWh";

    /// <summary>Ist der Temperaturschutz für diesen Miner gerade aktiv abgesenkt?</summary>
    public int? GuardOriginalFrequency(string host) => _hosts.TryGetValue(host, out var s) ? s.OriginalFrequency : null;

    /// <param name="scheduleBlocked">Zeitplan/Preis-Regel pausieren (z. B. während eines Dauertests); Temperaturschutz bleibt aktiv.</param>
    /// <param name="groupSchedule">Freigegebene Regel einer Gruppe des Miners – gilt nur, wenn er keine eigene Regel hat.</param>
    public AutomationResult Evaluate(DeviceConfig device, MinerInfo info, DeviceProfile? profile, DateTime now,
                                     bool busy, bool maintenance, bool scheduleBlocked = false,
                                     (PresetScheduleRule Rule, string Group)? groupSchedule = null)
    {
        var result = new AutomationResult();
        var host = device.Host.Trim();
        if (!_hosts.TryGetValue(host, out var st)) _hosts[host] = st = new HostState();

        var guard = device.ThermalGuard;
        var schedule = device.Schedule;
        var guardOn = guard.IsApproved(host);
        var scheduleOn = schedule.IsApproved(host);
        string? group = null;
        if (!schedule.Enabled && groupSchedule is { } gs)
        {
            // Gruppenregel (vom Aufrufer nur übergeben, wenn freigegeben); die eigene Regel hat immer Vorrang
            schedule = gs.Rule;
            scheduleOn = true;
            group = gs.Group;
        }
        result.Status = StatusText(device, schedule, group, st, guardOn, scheduleOn);

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
                        result.Action = new AutomationAction(host, L.T("Temperaturschutz"), target, info.CoreVoltageMv,
                            L.T("Temperaturschutz: Chip {0} °C / VR {1} °C seit {2} min über der Grenze", F(chip), F(vr), guard.Minutes));
                        return result;
                    }
                    Notice(result, st, host, L.T("Temperaturschutz"),
                        L.T("Minimum {0} MHz erreicht, trotzdem Chip {1} °C / VR {2} °C – bitte Kühlung prüfen.", guard.MinFrequencyMhz, F(chip), F(vr)));
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
                        result.Action = new AutomationAction(host, L.T("Temperaturschutz"), target, info.CoreVoltageMv,
                            L.T("Temperaturschutz: seit {0} min kühl, schrittweise zurück Richtung {1} MHz", guard.RecoverMinutes, original));
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
            result.Status += L.T(" · pausiert (Dauertest)");
            return result;
        }

        var ruleName = RuleName(schedule, group);
        string? presetName;
        if (schedule.Mode == "price")
        {
            var price = _prices?.PriceAt(now.ToUniversalTime());
            if (price is null)
            {
                Notice(result, st, host, ruleName, L.T("Kein aktueller Strompreis verfügbar – Voreinstellung bleibt unverändert."));
                return result;
            }
            presetName = price <= schedule.ThresholdCt ? schedule.CheapPreset : schedule.ExpensivePreset;
            result.Status += L.T(" · Preis {0} ct/kWh", F(price.Value, "0.0")).Replace("ct/kWh", CentPerKwh());
        }
        else
        {
            presetName = schedule.Entries.FirstOrDefault(e => e.Matches(now))?.Preset ?? schedule.DefaultPreset;
        }
        if (string.IsNullOrWhiteSpace(presetName)) return result;

        var preset = device.Presets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            Notice(result, st, host, ruleName, group is null ? L.T("Voreinstellung „{0}“ gibt es nicht.", presetName)
                : L.T("Voreinstellung „{0}“ gibt es bei diesem Miner nicht – wird übersprungen.", presetName));
            return result;
        }
        if (profile is not null && (preset.FrequencyMhz < profile.MinFrequencyMhz || preset.FrequencyMhz > profile.MaxFrequencyMhz ||
                                    preset.CoreVoltageMv < profile.MinVoltageMv || preset.CoreVoltageMv > profile.MaxVoltageMv))
        {
            Notice(result, st, host, ruleName, L.T("„{0}“ liegt außerhalb der Grenzen für {1} – wird nicht gesetzt.", preset, profile.Name));
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
            Notice(result, st, host, ruleName, L.T("„{0}“ wurde {1}× gesetzt, der Miner übernimmt sie nicht – pausiert bis zum nächsten Wechsel.", preset.Name, MaxAttemptsPerTarget));
            return result;
        }

        st.Attempts++;
        st.LastAction = now;
        result.Action = new AutomationAction(host, ruleName, preset.FrequencyMhz, preset.CoreVoltageMv,
            group is not null ? L.T("Gruppe „{0}“: „{1}“", group, preset.Name)
            : schedule.Mode == "price" ? L.T("Strompreis: „{0}“", preset.Name) : L.T("Zeitplan: „{0}“", preset.Name));
        return result;
    }

    private static string RuleName(PresetScheduleRule schedule, string? group)
    {
        var kind = schedule.Mode == "price" ? L.T("Strompreis") : L.T("Zeitplan");
        return group is null ? kind : L.T("Gruppe „{0}“ ({1})", group, kind);
    }

    private static void Notice(AutomationResult r, HostState st, string host, string rule, string message)
    {
        // Denselben Hinweis nicht bei jeder Abfrage wiederholen
        if (st.LastNotice == message) return;
        st.LastNotice = message;
        r.Notices.Add(new AutomationNotice(host, rule, message));
    }

    private static string StatusText(DeviceConfig d, PresetScheduleRule schedule, string? group, HostState st, bool guardOn, bool scheduleOn)
    {
        var parts = new List<string>();
        if (d.ThermalGuard.Enabled)
            parts.Add(guardOn
                ? st.OriginalFrequency is { } o ? L.T("Temperaturschutz aktiv (abgesenkt, ursprünglich {0} MHz)", o) : L.T("Temperaturschutz bereit")
                : L.T("Temperaturschutz: Freigabe fehlt"));
        var kind = group is not null ? L.T("Gruppen-Automatik „{0}“", group) : schedule.Mode == "price" ? L.T("Strompreis-Regel") : L.T("Zeitplan");
        if (schedule.Enabled)
            parts.Add(scheduleOn
                ? L.T("{0} aktiv{1}", kind, (st.ScheduleTarget is { } t ? L.T(" (Ziel „{0}“)", t) : ""))
                : L.T("{0}: Freigabe fehlt", kind));
        return string.Join(" · ", parts); // leer = keine Automatik
    }

    private static string F(double v, string format = "0.0") => v.ToString(format, L.Culture);
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
                L.T("Dauertest bestanden: {0} MHz / {1} mV lief {2:0} h stabil", soak.FrequencyMhz, soak.CoreVoltageMv, (soak.Until - soak.StartedAt).TotalHours) +
                (CurrentRatio is { } r ? L.T(" (zuletzt {0:P1} der Soll-Hashrate).", r) : "."));

        if (info is null)
        {
            if (maintenance) return Running(soak, now);
            _offlineSince ??= now;
            return now - _offlineSince > TimeSpan.FromMinutes(10)
                ? new SoakResult(SoakOutcome.Failed, L.T("Miner seit über 10 min nicht erreichbar."))
                : Running(soak, now);
        }
        _offlineSince = null;

        if (info.FrequencyMhz != soak.FrequencyMhz || info.CoreVoltageMv != soak.CoreVoltageMv)
            return new SoakResult(SoakOutcome.Aborted,
                L.T("Einstellung wurde geändert ({0} MHz / {1} mV) – Dauertest beendet.", info.FrequencyMhz, info.CoreVoltageMv));

        if (profile is not null)
        {
            var hot = info.MaxChipTempC > profile.MaxChipTempC || info.VrTempC > profile.MaxVrTempC;
            _hotSamples = hot ? _hotSamples + 1 : 0;
            if (_hotSamples >= 3)
                return new SoakResult(SoakOutcome.Failed,
                    L.T("Temperatur über der Grenze (Chip {0:0.0} °C / VR {1:0} °C, Grenzen {2:0}/{3:0} °C).", info.MaxChipTempC, info.VrTempC, profile.MaxChipTempC, profile.MaxVrTempC));
        }

        if (now - soak.StartedAt < Warmup) return Running(soak, now);

        var expected = info.ExpectedHashRateGh ?? 0;
        _samples.Enqueue((now, info.HashRateGh, expected, info.ErrorPercent));
        while (_samples.Count > 0 && now - _samples.Peek().Time > Window) _samples.Dequeue();
        var windowFull = _samples.Count >= 3 && now - _samples.Peek().Time >= Window - TimeSpan.FromMinutes(1);

        var exp = _samples.Where(s => s.Expected > 0).ToList();
        CurrentRatio = exp.Count > 0 ? exp.Average(s => s.Hash) / exp.Average(s => s.Expected) : null;

        if (windowFull && CurrentRatio is { } ratio && ratio < RatioThreshold)
            return new SoakResult(SoakOutcome.Failed, L.T("Hashrate nur {0:P1} der Soll-Hashrate (Ø 15 min, Grenze {1:P0}).", ratio, RatioThreshold));

        var errors = _samples.Where(s => s.Error.HasValue).Select(s => s.Error!.Value).ToList();
        if (windowFull && errors.Count > 0 && errors.Average() > MaxErrorPercent)
            return new SoakResult(SoakOutcome.Failed, L.T("Fehlerrate Ø {0:0.00} % (Grenze {1:0} %).", errors.Average(), MaxErrorPercent));

        return Running(soak, now);
    }

    private SoakResult Running(SoakTestState soak, DateTime now)
    {
        var done = (now - soak.StartedAt).TotalMinutes / Math.Max(1, (soak.Until - soak.StartedAt).TotalMinutes);
        var phase = now - soak.StartedAt < Warmup ? L.T("Anlaufphase") : CurrentRatio is { } r ? L.T("{0:P1} der Soll-Hashrate", r) : L.T("misst");
        return new SoakResult(SoakOutcome.Running,
            L.T("Dauertest {0} MHz / {1} mV · {2:P0} · {3} · bis {4}", soak.FrequencyMhz, soak.CoreVoltageMv, Math.Clamp(done, 0, 1), phase, L.Short(soak.Until)));
    }

    /// <summary>Nächstniedrigere stabile Einstellung aus Benchmark-Ergebnissen (höchste Frequenz darunter, niedrigste Spannung).</summary>
    public static (int Frequency, int Voltage)? SuggestLower(IEnumerable<Benchmark.StepResult> results, int currentFrequency) =>
        results.Where(r => r.IsStable && r.FrequencyMhz < currentFrequency)
               .OrderByDescending(r => r.FrequencyMhz).ThenBy(r => r.CoreVoltageMv)
               .Select(r => ((int, int)?)(r.FrequencyMhz, r.CoreVoltageMv))
               .FirstOrDefault();
}
