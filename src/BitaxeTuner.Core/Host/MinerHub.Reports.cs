using BitaxeTuner.Core.Config;
using System.Globalization;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Reports;

namespace BitaxeTuner.Core.Host;

/// <summary>Monats- und Jahresberichte; optional am Monatsersten eine Zusammenfassung des Vormonats per Push.</summary>
public sealed partial class MinerHub
{
    private bool _monthlyBusy;

    /// <summary>Miner für Berichte: echte Miner, bei reinem Demo-Aufbau die simulierten.</summary>
    private List<(string Name, string Host)> ReportMiners() =>
        Devices.Where(d => !d.IsSimulated || Devices.All(x => x.IsSimulated)).Select(d => (d.Title, d.Host)).ToList();

    /// <summary>Bericht für „2026-09“ oder „2026“.</summary>
    public PeriodReport BuildReport(string period, DateTime now)
    {
        if (History is null) throw new LocalizedException("Verlaufsdatenbank nicht verfügbar.");
        List<Tax.Models.MinedReward> rewards;
        try { rewards = TaxRepository.LoadRewards(); } catch { rewards = []; }
        return PeriodReports.Build(History, Config, ReportMiners(), rewards, period, now);
    }

    /// <summary>Monate eines Jahres (Stromkosten je Monat im Steuer-Bereich).</summary>
    public List<PeriodReport> MonthReports(int year, DateTime now)
    {
        if (History is null) throw new LocalizedException("Verlaufsdatenbank nicht verfügbar.");
        List<Tax.Models.MinedReward> rewards;
        try { rewards = TaxRepository.LoadRewards(); } catch { rewards = []; }
        return PeriodReports.Months(History, Config, ReportMiners(), rewards, year, now);
    }

    /// <summary>Zeiträume für die Auswahl: Monate ab dem ältesten Messwert bzw. gespeicherten Bericht, neueste zuerst.</summary>
    public List<string> ReportPeriods(DateTime now)
    {
        if (History is null) return [];
        var first = History.StoredPeriods().FirstOrDefault() is { } p && PeriodReports.TryParse(p, out var f, out _, out _) ? f : now;
        if (History.EarliestSample() is { } e && e < first) first = e;
        var months = new List<string>();
        for (var m = new DateTime(now.Year, now.Month, 1); m >= new DateTime(first.Year, first.Month, 1); m = m.AddMonths(-1))
            months.Add(m.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var years = months.Select(x => x[..4]).Distinct();
        return [.. months, .. years];
    }

    /// <summary>Täglich prüfen: am Monatsersten (ab der Uhrzeit des Tagesberichts) den Vormonat melden.</summary>
    private void CheckMonthlyReport(DateTime now)
    {
        var s = Config.DailyReport;
        if (_monthlyBusy || History is null || !s.Monthly || !Notify.Enabled || now.Hour < Math.Clamp(s.Hour, 0, 23)) return;
        var previous = new DateTime(now.Year, now.Month, 1).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (s.LastMonthlySent == previous) return;
        _ = SendMonthlyReportAsync(previous, now, markSent: true);
    }

    /// <summary>Kurzfassung eines Monats per Push. Liefert eine Fehlermeldung oder null.</summary>
    public async Task<string?> SendMonthlyReportAsync(string period, DateTime now, bool markSent)
    {
        if (!Notify.Enabled) return L.T("Kein Push-Dienst eingerichtet (Einstellungen → Push-Benachrichtigungen).");
        _monthlyBusy = true;
        try
        {
            var r = BuildReport(period, now);
            var c = L.Culture;
            var lines = new List<string>
            {
                L.T("Ø {0} · {1} kWh ≈ {2} {3}", r.TotalAvgHashGh is { } gh ? (gh / 1000).ToString("0.00", c) + " TH/s" : "–",
                    r.Energy.Kwh.ToString("0.0", c), r.Energy.Cost.ToString("0.00", c), r.Currency),
            };
            if (r.Income.Count > 0) lines.Add(L.T("Zuflüsse: {0} € ({1})", r.IncomeEur.ToString("0.00", c), string.Join(", ", r.Income.Select(i => $"{i.Count}× {i.Coin}"))));
            lines.AddRange(r.Miners.Where(m => m.TotalMinutes > 0).Select(m =>
                L.T("{0}: verfügbar {1} % · {2} J/TH", m.Name, ((m.Availability ?? 0) * 100).ToString("0.0", c), m.Jth?.ToString("0.0", c) ?? "–")));
            lines.Add(L.T("Ausführlich: Berichte (Browser) bzw. Bericht … (Desktop)"));
            var key = markSent ? $"monthly:{period}" : $"monthly-test:{now:O}";
            await Notify.SendAsync(key, ReportRenderer.Title(r), string.Join("\n", lines), NotifyPriority.Low, TimeSpan.FromDays(20), NotifyCategory.MonthlyReport);
            if (Notify.LastError is { } error) return L.T("Senden fehlgeschlagen: ") + error;
            if (markSent)
            {
                Config.DailyReport.LastMonthlySent = period;
                Config.Save();
            }
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        finally { _monthlyBusy = false; }
    }
}
