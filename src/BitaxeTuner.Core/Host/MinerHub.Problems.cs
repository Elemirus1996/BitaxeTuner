using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.23 „Was ist los?“: ein erkanntes Problem eines Miners mit Erklärung.
/// </summary>
/// <param name="Key">Kennung (offline, chip-hot, vr-hot, fan-stalled, errors, rejects, low-hash, coinbase, fallback, upkeep).</param>
/// <param name="Severity">„bad“ (rot) oder „warn“ (gelb).</param>
/// <param name="Title">Kurz: was ist los.</param>
/// <param name="Why">Der gemessene Anlass (Werte).</param>
/// <param name="Check">Was zu prüfen bzw. zu tun ist.</param>
/// <param name="Link">Wohin es weitergeht: „device:automation“, „device:health“, „device:live“, „fans“, „pools“, „settings:miners“ – null = nirgends.</param>
public sealed record MinerProblem(string Key, string Severity, string Title, string Why, string Check, string? Link);

public sealed partial class MinerHub
{
    /// <summary>Ab diesem Anteil abgelehnter Shares (mit genügend Shares) gibt es einen Hinweis.</summary>
    public const double ProblemRejectShare = 0.02;
    /// <summary>Ab dieser Hardware-Fehlerrate (%) gibt es einen Hinweis.</summary>
    public const double ProblemErrorPercent = 3;
    /// <summary>Unter diesem Anteil der Soll-Hashrate (nach 15 min Laufzeit) gibt es einen Hinweis.</summary>
    public const double ProblemLowHashShare = 0.8;

    /// <summary>Aktuelle Probleme eines Miners mit Ursache und nächstem Schritt – schlimmste zuerst. Leer = alles in Ordnung.</summary>
    public List<MinerProblem> ProblemsOf(HubDevice d)
    {
        var list = new List<MinerProblem>();
        var s = d.State;
        var c = L.Culture;
        if (!s.Online)
        {
            if (Maintenance.IsActive(d.Host)) return list;   // Wartung/Neustart/Tuning: kein Problem
            list.Add(new MinerProblem("offline", "bad", L.T("Miner antwortet nicht"),
                s.Error is { Length: > 0 } err ? err : L.T("Keine Antwort auf die Abfrage."),
                L.T("Strom und Netzteil prüfen (LED an?). Im Router nachsehen, ob der Miner eine neue IP-Adresse bekommen hat – dann eine feste Adresse vergeben und hier eintragen. WLAN-Empfang am Aufstellort prüfen und AxeOS im Browser öffnen."),
                "settings:miners"));
            return list;
        }
        if (s.Info is not { } i) return list;

        var warn = Config.TempWarn;
        if (i.temp > 0 && i.temp >= warn)
            list.Add(new MinerProblem("chip-hot", i.temp >= d.Profile.MaxChipTempC ? "bad" : "warn", L.T("ASIC zu warm"),
                L.T("ASIC {0} °C – Warnschwelle {1} °C, Grenze des Modells {2} °C.", i.temp.ToString("0.0", c), warn.ToString("0", c), d.Profile.MaxChipTempC.ToString("0", c)),
                L.T("Läuft der Lüfter (Drehzahl)? Staub im Kühlkörper, Wärmeleitpaste, Raumtemperatur und Luftstrom prüfen. Ist die Frequenz bzw. Spannung hoch eingestellt, eine sparsamere Voreinstellung wählen oder den Temperaturschutz der Automatik einschalten."),
                "device:automation"));
        if (i.vrTemp > 0 && i.vrTemp >= d.Profile.MaxVrTempC - 5)
            list.Add(new MinerProblem("vr-hot", i.vrTemp >= d.Profile.MaxVrTempC ? "bad" : "warn", L.T("Spannungsregler zu warm"),
                L.T("VR {0} °C – Grenze des Modells {1} °C.", i.vrTemp.ToString("0", c), d.Profile.MaxVrTempC.ToString("0", c)),
                L.T("Der Spannungsregler sitzt neben dem ASIC und wird oft nur schwach gekühlt: Kühlkörper bzw. Zusatzlüfter für die VR-Seite, Luftstrom über die Platine, Spannung nicht höher als nötig."),
                "fans"));
        if (FanStatus.Channels.FirstOrDefault(f => f.Role == "miner" && string.Equals(f.MinerHost?.Trim(), d.Host.Trim(), StringComparison.OrdinalIgnoreCase)) is { Stalled: true } fan)
            list.Add(new MinerProblem("fan-stalled", "bad", L.T("Zusatzlüfter steht"),
                L.T("Kanal K{0} soll {1} % laufen, meldet aber keine Drehzahl.", fan.Channel, fan.Percent),
                L.T("Stecker am Pico bzw. an der Lüfterplatine prüfen, Lüfter von Hand anstoßen, bei Geräusch tauschen."),
                "fans"));
        if (s.Normalized?.ErrorPercent is { } errPct && errPct >= ProblemErrorPercent)
            list.Add(new MinerProblem("errors", errPct >= 2 * ProblemErrorPercent ? "bad" : "warn", L.T("Viele Hardware-Fehler"),
                L.T("Fehlerrate {0} % (ab {1} % auffällig).", errPct.ToString("0.0", c), ProblemErrorPercent.ToString("0", c)),
                L.T("Meist ist die Frequenz für die Spannung zu hoch oder der Chip zu warm. Eine stabilere Voreinstellung wählen oder einen Dauertest laufen lassen – er schlägt bei Problemen eine sichere Einstellung vor."),
                "device:health"));
        var shares = i.sharesAccepted + i.sharesRejected;
        if (shares >= 50 && i.sharesRejected / shares >= ProblemRejectShare)
            list.Add(new MinerProblem("rejects", "warn", L.T("Viele abgelehnte Shares"),
                L.T("{0} % abgelehnt ({1} von {2}).", (i.sharesRejected / shares * 100).ToString("0.0", c), i.sharesRejected.ToString("0", c), shares.ToString("0", c)),
                L.T("Oft eine langsame Verbindung zum Pool (WLAN-Empfang, Pool weit weg) oder ein instabiler Miner. Im Pool-Vergleich den zuverlässigsten Pool ansehen und den WLAN-Empfang prüfen."),
                "pools"));
        if (i.expectedHashrate > 0 && i.uptimeSeconds > 15 * 60 && i.hashRate < i.expectedHashrate * ProblemLowHashShare)
            list.Add(new MinerProblem("low-hash", "warn", L.T("Hashrate unter dem Soll"),
                L.T("{0} statt erwartet {1} ({2} %).", Display.StatusRenderer.FormatHash(i.hashRate), Display.StatusRenderer.FormatHash(i.expectedHashrate),
                    (i.hashRate / i.expectedHashrate * 100).ToString("0", c)),
                L.T("Wird der Chip warm und drosselt, oder ist die Einstellung instabil? Temperaturen und Fehlerrate ansehen; nach Änderungen ein paar Minuten warten – die Hashrate schwankt kurz nach dem Start."),
                "device:live"));
        if (i.coinbasePayoutStatus == "not_found")
            list.Add(new MinerProblem("coinbase", "bad", L.T("Coinbase zahlt nicht an dich"),
                L.T("In der Block-Vorlage des Pools steht deine Adresse nicht als Empfänger."),
                L.T("Bei einem Solo-Pool bekämst du einen gefundenen Block nicht. Benutzer bzw. Wallet-Adresse in der Pool-Vorlage prüfen und neu übertragen."),
                "pools"));
        if (i.isUsingFallbackStratum != 0)
            list.Add(new MinerProblem("fallback", "warn", L.T("Läuft auf dem Ersatz-Pool"),
                L.T("Der Haupt-Pool war nicht erreichbar."),
                L.T("Ist der Haupt-Pool gestört? Die Pool-Automatik kann von selbst zurückschalten, sobald er wieder antwortet."),
                "pools"));
        // 0.9.24: einzelner Chip dauerhaft schwach oder ausgefallen (Mehrchip-Boards)
        foreach (var chip in ChipStatesOf(d).Where(x => x.State != "ok"))
            list.Add(new MinerProblem($"chip-{chip.Index}", chip.State == "dead" ? "bad" : "warn",
                chip.State == "dead" ? L.T("Chip {0} ausgefallen", chip.Index) : L.T("Chip {0} schwach", chip.Index),
                L.T("Chip {0} liefert seit 30 Minuten nur {1} % der übrigen Chips ({2} GH/s).", chip.Index, chip.PercentOfMedian.ToString("0", c), chip.HashGh.ToString("0", c)),
                ChipAdvice, "device:live"));
        foreach (var t in UpkeepOf(d).Where(t => t.Due))
            list.Add(new MinerProblem("upkeep", "warn", L.T("Wartung fällig"), t.Label,
                L.T("Nach der Arbeit auf der Gesundheitsseite „erledigt“ wählen – dann zählt die Erinnerung neu."),
                "device:health"));
        return list.OrderBy(p => p.Severity == "bad" ? 0 : 1).ToList();
    }
}
