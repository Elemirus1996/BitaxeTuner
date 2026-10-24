using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.25 Saisonaler Feinschliff: stündlich je Miner prüfen, ob die Chip-Temperatur im Wochenmittel (Einstellung seit 7 Tagen
/// unverändert) deutlich von ihrem Benchmark abweicht – dann eine geprüfte Einstellung vorschlagen (<see cref="SeasonalAdvisor"/>).
/// Höchstens ein Vorschlag je Miner und Woche, „nicht mehr fragen“ je Miner. Nie automatisch angewendet: Meldung, „Was ist
/// los?“, Ratgeber; angewendet wird nur nach Bestätigung mit alt → neu. Keine Außentemperatur.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan SeasonalCooldown = TimeSpan.FromDays(7);
    /// <summary>Mindestens so viele Minuten Messwerte in den letzten 7 Tagen (5 Tage).</summary>
    public const int SeasonalMinMinutes = 5 * 24 * 60;
    private DateTime _seasonalNext = DateTime.MinValue;

    /// <summary>Offener Vorschlag – nur solange der Miner noch mit der Einstellung läuft, für die er gedacht war.</summary>
    public SeasonalSuggestion? SeasonalOf(HubDevice d) =>
        d.Config.SeasonalPending is { } s && d.Info is { } i && i.FrequencyMhz == s.FromMhz && i.CoreVoltageMv == s.FromMv ? s : null;

    /// <param name="force">Nur für Tests: den stündlichen Takt übergehen.</param>
    internal void TickSeasonal(DateTime now, bool force = false)
    {
        if ((!force && now < _seasonalNext) || History is not { } db) return;
        _seasonalNext = now.AddHours(1);
        var sessions = Results.LoadAll();
        var demo = Devices.All(x => x.IsSimulated);
        foreach (var d in Devices)
        {
            var c = d.Config;
            // Vorschlag veraltet (Einstellung inzwischen anders) – verwerfen
            if (c.SeasonalPending is { } old && d.Info is { } now0 && (now0.FrequencyMhz != old.FromMhz || now0.CoreVoltageMv != old.FromMv))
            {
                c.SeasonalPending = null;
                Config.Save();
            }
            if ((d.IsSimulated && !demo) || d.MonitorOnly || !d.ProfileKnown || d.IsBenchmarkRunning || d.SoakActive || c.SeasonalMuted) continue;
            if (c.SeasonalPending is not null || (c.SeasonalSuggestedAt is { } at && now - at < SeasonalCooldown)) continue;
            if (d.Info is not { FrequencyMhz: > 0 } i) continue;
            if (db.QueryTuningEvents(d.Host, now.AddDays(-7), now).Count > 0) continue;   // Einstellung erst kürzlich geändert
            if (db.Average(d.Host, now.AddDays(-7), now) is not { Minutes: >= SeasonalMinMinutes, Temp: > 0 } avg) continue;
            (int, int)? from = c.SeasonalFromMhz is { } fm && c.SeasonalFromMv is { } fv ? (fm, fv) : null;
            var s = SeasonalAdvisor.Evaluate(d.Profile, i.FrequencyMhz, i.CoreVoltageMv, avg.Temp, Config.TempWarn,
                BenchmarkResultsOf(d, sessions), db.QuerySoakResults(d.Host), from, now);
            if (s is null || CheckLimits(d, s.ToMhz, s.ToMv) is not null) continue;
            c.SeasonalPending = s;
            c.SeasonalSuggestedAt = now;
            Config.Save();
            var title = SeasonalTitle(s);
            d.AddLog(L.T("Saisonaler Feinschliff: {0} – Vorschlag {1} MHz / {2} mV → {3} MHz / {4} mV (wartet auf Bestätigung)", title, s.FromMhz, s.FromMv, s.ToMhz, s.ToMv), EventCategories.Automation);
            if (Config.Notifications.Wants(NotifyCategory.Maintenance))
                SendAlert(new Alert($"seasonal:{d.Host}", L.T("{0}: {1}", d.Title, title), SeasonalWhy(s) + " " + SeasonalWhat(s),
                    NotifyPriority.Low, TimeSpan.FromDays(6), NotifyCategory.Maintenance, d.Host));
            RaiseDeviceChanged(d);
        }
    }

    public static string SeasonalTitle(SeasonalSuggestion s) => s.Direction == "down"
        ? L.T("Wärmer geworden – sparsamere Einstellung vorgeschlagen")
        : L.T("Wieder kühler – zurück zur stärkeren Einstellung möglich");

    public static string SeasonalWhy(SeasonalSuggestion s)
    {
        var c = L.Culture;
        return s.Direction == "down"
            ? L.T("ASIC im Mittel der letzten 7 Tage {0} °C – {1} °C wärmer als im Benchmark dieser Einstellung.", s.Avg7dC.ToString("0.0", c), s.ShiftC.ToString("0.0", c))
            : L.T("ASIC im Mittel der letzten 7 Tage {0} °C – {1} °C kühler als im Benchmark dieser Einstellung.", s.Avg7dC.ToString("0.0", c), (-s.ShiftC).ToString("0.0", c));
    }

    public static string SeasonalWhat(SeasonalSuggestion s)
    {
        var c = L.Culture;
        return L.T("Vorschlag {0} MHz / {1} mV (geprüft im Benchmark{2}): erwartet etwa {3} °C, {4} GH/s statt {5} GH/s, {6} W statt {7} W. Wird nur nach Bestätigung angewendet.",
            s.ToMhz, s.ToMv, s.SoakPassed ? ", " + L.T("Dauertest bestanden") : "", s.ExpectedC.ToString("0", c),
            s.ToGh.ToString("0", c), s.FromGh.ToString("0", c), s.ToW.ToString("0.0", c), s.FromW.ToString("0.0", c));
    }

    /// <summary>Aktueller Höchstwert der Temperaturfühler (Raum/Gehäuse) – nur als Zusatzinfo, null ohne Fühler.</summary>
    private double? SensorTempNow()
    {
        var now = DateTime.Now;
        lock (_sensorSeen)
        {
            var temps = _sensorSeen.Values.Where(v => now - v.Seen < SensorStale).Select(v => v.Temp).ToList();
            return temps.Count > 0 ? temps.Max() : null;
        }
    }

    /// <summary>„Was ist los?“: offener Vorschlag des saisonalen Feinschliffs.</summary>
    private MinerProblem? SeasonalProblemOf(HubDevice d)
    {
        if (SeasonalOf(d) is not { } s) return null;
        var why = SeasonalWhy(s);
        if (SensorTempNow() is { } room) why += " " + L.T("Temperaturfühler gerade {0} °C.", room.ToString("0.0", L.Culture));
        return new MinerProblem("seasonal", "warn", SeasonalTitle(s), why, SeasonalWhat(s), "compare");
    }

    /// <summary>Vorschlag anwenden – erst nach Bestätigung in der Oberfläche (Vorschau alt → neu über die normale Änderung).</summary>
    public async Task ApplySeasonalAsync(HubDevice d, string who)
    {
        var s = SeasonalOf(d) ?? throw new LocalizedException("Kein offener Vorschlag – die Einstellung hat sich inzwischen geändert.");
        await ApplyChangeAsync(d, s.ToMhz, s.ToMv);
        var c = d.Config;
        if (s.Direction == "down") (c.SeasonalFromMhz, c.SeasonalFromMv) = (s.FromMhz, s.FromMv);
        else (c.SeasonalFromMhz, c.SeasonalFromMv) = (null, null);
        c.SeasonalPending = null;
        Config.Save();
        d.AddLog(L.T("Saisonaler Feinschliff angewendet ({0}): {1} MHz / {2} mV → {3} MHz / {4} mV", who, s.FromMhz, s.FromMv, s.ToMhz, s.ToMv), EventCategories.Tuning);
        RaiseDeviceChanged(d);
    }

    /// <summary>Vorschlag verwerfen; <paramref name="never"/> = für diesen Miner nicht mehr fragen (in den Geräte-Einstellungen wieder einschaltbar).</summary>
    public void DismissSeasonal(HubDevice d, bool never, string who)
    {
        var c = d.Config;
        c.SeasonalPending = null;
        if (never) c.SeasonalMuted = true;
        Config.Save();
        d.AddLog(never ? L.T("Saisonaler Feinschliff: für diesen Miner ausgeschaltet ({0})", who) : L.T("Saisonaler Feinschliff: Vorschlag verworfen ({0})", who), EventCategories.Automation);
        RaiseDeviceChanged(d);
    }
}
