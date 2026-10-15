using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Eine Wartungsaufgabe eines Miners mit Fälligkeit (in Betriebstagen; negativ = überfällig).</summary>
public sealed record UpkeepTask(string Key, string Label, int IntervalDays, double DueInDays, DateTime? LastDone)
{
    public bool Due => DueInDays <= 0;
}

/// <summary>
/// 0.9.16 Wartungs-Erinnerungen: Betriebsstunden je Miner (eigene Zählung, Startwert aus der Firmware-Gesamtlaufzeit),
/// Aufgaben „Staub/Kühlkörper reinigen“, „Lüfter prüfen“, „Wärmeleitpaste erneuern“ mit Abstand in Betriebstagen.
/// Fällig → Push (Kategorie Wartung, je Aufgabe höchstens einmal pro Woche) und Hinweis an der Miner-Karte; „erledigt“
/// setzt die Aufgabe zurück. Gespeichert in config.json (DeviceConfig.Upkeep, additiv), höchstens alle 30 min.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly string[] UpkeepKeys = ["dust", "fan", "paste"];
    private readonly Dictionary<string, DateTime> _upkeepLastTick = new();
    private DateTime _upkeepSavedAt = DateTime.MinValue, _upkeepCheckedAt = DateTime.MinValue;
    private bool _upkeepDirty;

    public static string UpkeepLabel(string key) => key switch
    {
        "dust" => L.T("Staub entfernen, Kühlkörper reinigen"),
        "fan" => L.T("Lüfter prüfen (Geräusch, Drehzahl, Lager)"),
        "paste" => L.T("Wärmeleitpaste/-pad erneuern"),
        _ => key,
    };

    private int UpkeepInterval(string key) => key switch
    {
        "dust" => Config.Upkeep.DustDays,
        "fan" => Config.Upkeep.FanDays,
        _ => Config.Upkeep.PasteDays,
    };

    /// <summary>Aufgaben eines Miners (leer = noch nie online gesehen oder Erinnerungen aus).</summary>
    public List<UpkeepTask> UpkeepOf(HubDevice d)
    {
        if (!Config.Upkeep.Enabled || d.Config.Upkeep is not { } u) return [];
        return UpkeepKeys.Select(k =>
        {
            var done = u.Done.TryGetValue(k, out var x) ? x : null;
            var sinceDays = (u.Hours - (done?.Hours ?? 0)) / 24;
            return new UpkeepTask(k, UpkeepLabel(k), UpkeepInterval(k), UpkeepInterval(k) - sinceDays, done?.At);
        }).ToList();
    }

    /// <summary>Aufgabe erledigt: ab jetzt neu zählen.</summary>
    public void UpkeepDone(HubDevice d, string key, string by)
    {
        if (!UpkeepKeys.Contains(key)) throw new LocalizedException("Unbekannte Wartungsaufgabe.");
        var u = d.Config.Upkeep ??= new DeviceUpkeep();
        u.Done[key] = new UpkeepDone { At = DateTime.Now, Hours = u.Hours };
        Config.Save();
        _upkeepSavedAt = DateTime.Now;
        d.AddLog(L.T("Wartung erledigt: {0} (bei {1} Betriebsstunden) – {2}", UpkeepLabel(key), u.Hours.ToString("N0", L.Culture), by), EventCategories.Settings);
    }

    public void SetUpkeepSettings(UpkeepSettings s, string by)
    {
        Config.Upkeep = new UpkeepSettings
        {
            Enabled = s.Enabled,
            DustDays = Math.Clamp(s.DustDays, 7, 3650),
            FanDays = Math.Clamp(s.FanDays, 7, 3650),
            PasteDays = Math.Clamp(s.PasteDays, 7, 3650),
        };
        Config.Save();
        LogEvent(null, EventCategories.Settings, L.T("Wartungs-Erinnerungen gespeichert – {0}", by));
    }

    /// <summary>Nach jeder Abfrage: Betriebsstunden zählen; stündlich Fälligkeiten prüfen.</summary>
    private void TickUpkeep(DateTime now)
    {
        foreach (var d in Devices)
        {
            if (IsSimulated(d.Host) || !d.State.Online || d.State.Info is not { } i)
            {
                _upkeepLastTick.Remove(d.Host);
                continue;
            }
            var u = d.Config.Upkeep;
            if (u is null)
            {
                // Erster Kontakt: Startwert aus der Firmware (falls vorhanden), Aufgaben zählen ab jetzt
                var start = i.totalUptimeSeconds is > 0 and var t ? t / 3600.0 : 0;
                u = d.Config.Upkeep = new DeviceUpkeep { Hours = start };
                foreach (var k in UpkeepKeys) u.Done[k] = new UpkeepDone { At = now, Hours = start };
                _upkeepDirty = true;
                _upkeepLastTick[d.Host] = now;   // ab jetzt zählen
                continue;
            }
            if (_upkeepLastTick.TryGetValue(d.Host, out var last) && now > last)
            {
                var step = now - last;
                if (step <= TimeSpan.FromMinutes(10)) { u.Hours += step.TotalHours; _upkeepDirty = true; }   // längere Lücke: nicht zählen
            }
            _upkeepLastTick[d.Host] = now;
        }
        if (_upkeepDirty && now - _upkeepSavedAt >= TimeSpan.FromMinutes(30))
        {
            _upkeepDirty = false;
            _upkeepSavedAt = now;
            try { Config.Save(); } catch { /* nächstes Mal */ }
        }
        if (now - _upkeepCheckedAt < TimeSpan.FromHours(1)) return;
        _upkeepCheckedAt = now;
        foreach (var d in Devices)
            foreach (var task in UpkeepOf(d).Where(t => t.Due))
                SendAlert(new Alert($"upkeep:{d.Host}:{task.Key}", L.T("{0}: Wartung fällig", d.Title),
                    L.T("{0} – alle {1} Betriebstage. Danach unter Gerät → Gesundheit „erledigt“ drücken.", task.Label, task.IntervalDays),
                    NotifyPriority.Low, TimeSpan.FromDays(7), NotifyCategory.Maintenance, d.Host));
    }

    internal void TickUpkeepForTest(DateTime now) => TickUpkeep(now);
}
