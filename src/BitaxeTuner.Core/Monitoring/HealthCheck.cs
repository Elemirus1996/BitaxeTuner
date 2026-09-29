using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Ein Hinweis der Frühwarnung. Code = stabile Kennung (Sperrzeit je Hinweis), Recent/Base = verglichene Werte.</summary>
public sealed record HealthFinding(string Code, string Title, string Text, double Recent, double Base);

/// <summary>Werte eines Zeitraums für die Frühwarnung; null = zu wenig Daten.</summary>
public sealed record HealthWindow(double? TempPerWatt, double? Temp, double? Jth, double? RpmPerPercent, double? RejectShare, double? Availability);

/// <summary>
/// Gesundheits-Frühwarnung je Miner: letzte 7 Tage gegen die 4 Wochen davor. Nur deutliche Veränderungen
/// (konservative Schwellen), damit normale Schwankungen und Raumtemperatur keinen Alarm auslösen.
/// </summary>
public static class HealthCheck
{
    public static readonly TimeSpan Recent = TimeSpan.FromDays(7), Base = TimeSpan.FromDays(28);

    /// <summary>Mindestens so viele Online-Minuten je Zeitraum (3 bzw. 7 Tage), sonst keine Aussage.</summary>
    public const int MinRecentMinutes = 3 * 1440, MinBaseMinutes = 7 * 1440;

    public static (HealthWindow Recent, HealthWindow Base) Windows(HistoryStore db, string host, DateTime now)
    {
        var recentFrom = now - Recent;
        var baseFrom = recentFrom - Base;
        return (Window(db, host, recentFrom, now, MinRecentMinutes), Window(db, host, baseFrom, recentFrom, MinBaseMinutes));
    }

    private static HealthWindow Window(HistoryStore db, string host, DateTime from, DateTime to, int minMinutes)
    {
        var avg = db.Average(host, from, to);
        var (online, total) = db.MinuteCounts(host, from, to);
        var enough = avg is not null && avg.Minutes >= minMinutes;
        var health = db.QueryHealth(host, from, to);

        // Lüfter: Drehzahl je Prozent, nur bei nennenswerter Ansteuerung
        var fan = health.Where(h => h.FanPercent >= 20 && h.FanRpm > 0).ToList();
        double? rpmPerPct = fan.Count >= minMinutes / 10 / 2 ? fan.Average(h => h.FanRpm / h.FanPercent) : null;

        // Abgelehnte Shares aus den Zuwächsen der Zähler (Neustart setzt sie zurück – solche Schritte zählen nicht)
        double acc = 0, rej = 0;
        for (var i = 1; i < health.Count; i++)
        {
            var da = health[i].Accepted - health[i - 1].Accepted;
            var dr = health[i].Rejected - health[i - 1].Rejected;
            if (da < 0 || dr < 0) continue;
            acc += da;
            rej += dr;
        }
        double? rejectShare = acc + rej >= 200 ? rej / (acc + rej) : null;

        return new HealthWindow(
            enough && avg!.Power > 1 ? avg.Temp / avg.Power : null,
            enough ? avg!.Temp : null,
            enough && avg!.HashRateGh > 1 ? avg.Power / (avg.HashRateGh / 1000) : null,
            rpmPerPct, rejectShare,
            total >= minMinutes ? (double)online / total : null);
    }

    /// <summary>Hinweise aus den beiden Zeiträumen. tuningChanged: Frequenz/Spannung im Vergleichszeitraum geändert.</summary>
    public static List<HealthFinding> Evaluate(HealthWindow recent, HealthWindow @base, bool tuningChanged)
    {
        var c = L.Culture;
        var list = new List<HealthFinding>();

        // Wärmer bei gleicher Leistung → Kühlung (Staub, Lüfter, Wärmeleitpaste); Raumtemperatur schwankt, daher +10 % und +3 °C
        if (recent.TempPerWatt is { } tr && @base.TempPerWatt is { } tb && tr >= tb * 1.10 && recent.Temp - @base.Temp >= 3)
            list.Add(new HealthFinding("cooling", L.T("Kühlung prüfen"),
                L.T("ASIC {0} °C statt {1} °C bei vergleichbarer Leistung – Staub, Lüfter oder Wärmeleitpaste prüfen.",
                    recent.Temp!.Value.ToString("0.0", c), @base.Temp!.Value.ToString("0.0", c)), tr, tb));

        // Effizienz schlechter ohne Tuning-Änderung → Hardware lässt nach
        if (!tuningChanged && recent.Jth is { } jr && @base.Jth is { } jb && jr >= jb * 1.05)
            list.Add(new HealthFinding("efficiency", L.T("Effizienz lässt nach"),
                L.T("{0} J/TH statt {1} J/TH bei unveränderter Einstellung.", jr.ToString("0.00", c), jb.ToString("0.00", c)), jr, jb));

        // Lüfter dreht bei gleicher Ansteuerung langsamer → Verschleiß
        if (recent.RpmPerPercent is { } fr && @base.RpmPerPercent is { } fb && fr <= fb * 0.85)
            list.Add(new HealthFinding("fan", L.T("Lüfter verliert Drehzahl"),
                L.T("{0} U/min je % statt {1} – Lüfter reinigen oder tauschen.", fr.ToString("0", c), fb.ToString("0", c)), fr, fb));

        // Deutlich mehr abgelehnte Shares
        if (recent.RejectShare is { } rr && @base.RejectShare is { } rb && rr >= rb + 0.01 && rr >= rb * 2)
            list.Add(new HealthFinding("rejects", L.T("Mehr abgelehnte Shares"),
                L.T("{0} % statt {1} % abgelehnt – Pool, WLAN oder Stabilität der Einstellung prüfen.",
                    (rr * 100).ToString("0.0", c), (rb * 100).ToString("0.0", c)), rr, rb));

        // Häufiger offline
        if (recent.Availability is { } ar && @base.Availability is { } ab && ar < 0.97 && ab >= 0.99)
            list.Add(new HealthFinding("availability", L.T("Häufiger offline"),
                L.T("Verfügbar {0} % statt {1} % – Stromversorgung, WLAN und Neustarts prüfen.",
                    (ar * 100).ToString("0.0", c), (ab * 100).ToString("0.0", c)), ar, ab));
        return list;
    }
}
