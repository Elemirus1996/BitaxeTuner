using System.Globalization;
using System.Net;
using System.Text;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;

namespace BitaxeTuner.Core.Reports;

/// <summary>0.9.25: ein Monat im Jahresrückblick.</summary>
public sealed record YearReviewMonth(DateTime Month, double? AvgHashGh, double Kwh, double Cost, decimal Income);

/// <summary>0.9.25: ein Miner im Jahresrückblick – nur Anzeigename (oder „Miner 1“), nie Adresse oder Wallet.</summary>
public sealed record YearReviewMiner(string Name, double OnlineHours, double? Availability, double? AvgHashGh, double? Jth, double Kwh, string? BestDiff);

/// <summary>
/// 0.9.25 „Dein Mining-Jahr“: Zusammenfassung der letzten 12 Monate oder eines Kalenderjahres – zum Teilen gedacht, daher ohne
/// IP-Adressen, Wallets und (wenn nicht gewünscht) ohne Minernamen.
/// </summary>
public sealed record YearReview(string Title, DateTime From, DateTime To, bool Partial, DateTime? DataFrom,
    double? AvgHashGh, double OnlineHours, double Kwh, double Cost, string Currency,
    decimal Income, string IncomeSymbol, int Payouts, string? BestDiff, string? BestDiffMiner, DateTime? BestDiffAt,
    YearReviewMonth? BestMonth, IReadOnlyList<string> Milestones, IReadOnlyList<YearReviewMonth> Months, IReadOnlyList<YearReviewMiner> Miners)
{
    public decimal Balance => Income - (decimal)Cost;

    /// <summary>„last12“ (die letzten 12 vollen Monate plus laufender Monat) oder ein Jahr „2026“.</summary>
    public static bool TryParse(string? period, DateTime now, out DateTime from, out DateTime to, out string title)
    {
        var p = (period ?? "last12").Trim();
        if (p == "last12")
        {
            to = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            from = to.AddMonths(-12);
            title = L.T("Dein Mining-Jahr {0} – {1}", from.ToString("MMM yyyy", L.Culture), to.AddDays(-1).ToString("MMM yyyy", L.Culture));
            return true;
        }
        if (p.Length == 4 && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2020 and <= 2100)
        {
            from = new DateTime(y, 1, 1);
            to = from.AddYears(1);
            title = L.T("Dein Mining-Jahr {0}", y);
            return true;
        }
        from = to = default;
        title = "";
        return false;
    }

    /// <summary>
    /// Aus den Monatsberichten des Zeitraums. <paramref name="names"/>: Anzeigename je Adresse (bei „Namen ausblenden“ schon
    /// durch „Miner 1“ … ersetzt). Best Diff und Meilensteine nur aus dem Zeitraum.
    /// </summary>
    public static YearReview Build(string title, DateTime from, DateTime to, DateTime now, IReadOnlyList<PeriodReport> months,
        IReadOnlyDictionary<string, string> names, IEnumerable<BestDiffRecord> bestDiffs, IEnumerable<NewsItem> milestones)
    {
        var monthRows = months.Select(m => new YearReviewMonth(m.From, m.TotalAvgHashGh, m.Energy.Kwh, m.Energy.Cost, m.IncomeEur)).ToList();
        var minerRows = new List<YearReviewMiner>();
        var diffs = bestDiffs.Where(b => b.AchievedAt >= from && b.AchievedAt < to).ToList();
        foreach (var (host, name) in names)
        {
            var rows = months.SelectMany(m => m.Miners).Where(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var online = rows.Sum(r => r.OnlineMinutes);
            var total = rows.Sum(r => r.TotalMinutes);
            double? Weighted(Func<PeriodMinerRow, double?> f)
            {
                var w = rows.Where(r => f(r) is not null && r.OnlineMinutes > 0).ToList();
                var sum = w.Sum(r => r.OnlineMinutes);
                return sum > 0 ? w.Sum(r => f(r)!.Value * r.OnlineMinutes) / sum : null;
            }
            var hash = Weighted(r => r.AvgHashGh);
            var power = Weighted(r => r.AvgPowerW);
            var best = diffs.Where(b => string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)).MaxBy(b => b.Value);
            minerRows.Add(new YearReviewMiner(name, online / 60.0, total > 0 ? (double)online / total : null, hash,
                hash is > 0 && power is { } w ? w / (hash.Value / 1000) : null, rows.Sum(r => r.Kwh), best?.Raw));
        }
        var top = diffs.MaxBy(b => b.Value);
        // Bester Monat: höchster Ertrag; ohne Zuflüsse die höchste Hashrate
        var bestMonth = monthRows.Any(m => m.Income > 0) ? monthRows.MaxBy(m => m.Income) : monthRows.Where(m => m.AvgHashGh is > 0).MaxBy(m => m.AvgHashGh);
        var stones = milestones.Where(n => n.Kind == "milestone" && n.DateUtc.ToLocalTime() >= from && n.DateUtc.ToLocalTime() < to)
            .OrderBy(n => n.DateUtc).Select(n => n.Title).ToList();
        var hashMonths = monthRows.Where(m => m.AvgHashGh is not null).ToList();
        var first = months.FirstOrDefault();
        return new YearReview(title, from, to, to > now, months.Select(m => m.DataFrom).FirstOrDefault(d => d is not null),
            hashMonths.Count > 0 ? hashMonths.Average(m => m.AvgHashGh!.Value) : null, minerRows.Sum(m => m.OnlineHours),
            monthRows.Sum(m => m.Kwh), monthRows.Sum(m => m.Cost), first?.Currency ?? "€",
            monthRows.Sum(m => m.Income), first?.IncomeSymbol ?? "€", months.Sum(m => m.Income.Sum(i => i.Count)),
            top?.Raw, top is null ? null : names.TryGetValue(top.Host, out var n) ? n : null, top?.AchievedAt,
            bestMonth, stones, monthRows, minerRows.OrderByDescending(m => m.OnlineHours).ToList());
    }

    public static string FormatHash(double? gh) => gh is not { } v ? "–"
        : v >= 1000 ? (v / 1000).ToString("0.00", L.Culture) + " TH/s" : v.ToString("0", L.Culture) + " GH/s";

    /// <summary>Druckbare Seite (Strg+P → „Als PDF speichern“), ohne Skript – passt zur Content-Security-Policy des Servers.</summary>
    public string Html()
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var c = L.Culture;
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{Loc.Current.Language}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(Title)} – BitaxeTuner</title><style>");
        sb.Append("""
            body{font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;color:#111;background:#fff;margin:32px auto;max-width:900px;padding:0 16px}
            h1{font-size:26px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}.muted{color:#555}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:18px 0}
            .tile{border:1px solid #ddd;border-radius:10px;padding:12px 14px}.tile b{display:block;font-size:22px;margin-top:2px}
            .bars{display:flex;gap:6px;align-items:flex-end;height:140px;border-bottom:2px solid #111;margin:10px 0 4px}
            .bars div{flex:1;background:#E07F05;border-radius:3px 3px 0 0;min-height:2px}.labels{display:flex;gap:6px}.labels span{flex:1;text-align:center;font-size:12px;color:#555}
            table{border-collapse:collapse;width:100%}th,td{padding:6px 8px;border-bottom:1px solid #e5e5e5;text-align:right}
            th:first-child,td:first-child{text-align:left}th{font-weight:600;background:#f6f6f6}
            ul{margin:6px 0 0 18px;padding:0}.print{margin:12px 0}@media print{.print{display:none}body{margin:0}}
            """);
        sb.Append("</style></head><body>");
        sb.Append($"<h1>{E(Title)}</h1><div class=\"muted\">{E(From.ToString("D", c))} – {E(To.AddDays(-1).ToString("D", c))}");
        if (Partial) sb.Append($" · {E(L.T("läuft noch – Stand {0}", DateTime.Now.ToString("g", c)))}");
        sb.Append("</div>");
        sb.Append($"<div class=\"print muted\">{E(L.T("Als PDF speichern: Drucken (Strg+P) → „Als PDF speichern“."))}</div>");
        if (DataFrom is { } df) sb.Append($"<p class=\"muted\">{E(L.T("Messwerte liegen erst ab {0} vor – der Zeitraum davor fehlt im Bericht.", df.ToString("g", c)))}</p>");

        sb.Append("<div class=\"tiles\">");
        void Tile(string label, string value) => sb.Append($"<div class=\"tile\"><span class=\"muted\">{E(label)}</span><b>{E(value)}</b></div>");
        Tile(L.T("Beste Difficulty"), BestDiff is { } bd ? bd + (BestDiffMiner is { } bm ? " · " + bm : "") : "–");
        Tile(L.T("Ø Hashrate gesamt"), FormatHash(AvgHashGh));
        Tile(L.T("Laufzeit"), L.T("{0} h", OnlineHours.ToString("N0", c)));
        Tile(L.T("Energie"), Kwh.ToString("N0", c) + " kWh");
        Tile(L.T("Stromkosten"), Cost.ToString("N2", c) + " " + Currency);
        if (Income > 0 || Payouts > 0)
        {
            Tile(L.T("Ertrag"), Income.ToString("N2", c) + " " + IncomeSymbol);
            Tile(L.T("Saldo"), Balance.ToString("+#,##0.00;-#,##0.00", c) + " " + IncomeSymbol);
            Tile(L.T("Auszahlungen"), Payouts.ToString(c));
        }
        if (BestMonth is { } best)
            Tile(L.T("Bester Monat"), best.Month.ToString("MMMM yyyy", c));
        sb.Append("</div>");

        if (Months.Count > 0)
        {
            var useIncome = Months.Any(m => m.Income > 0);
            var max = Months.Max(m => useIncome ? (double)m.Income : m.AvgHashGh ?? 0);
            sb.Append($"<h2>{E(useIncome ? L.T("Ertrag je Monat") : L.T("Ø Hashrate je Monat"))}</h2><div class=\"bars\">");
            foreach (var m in Months)
            {
                var v = useIncome ? (double)m.Income : m.AvgHashGh ?? 0;
                var pct = max > 0 ? Math.Round(v / max * 100, 1) : 0;
                var label = useIncome ? m.Income.ToString("N2", c) + " " + IncomeSymbol : FormatHash(m.AvgHashGh);
                sb.Append($"<div style=\"height:{pct.ToString(CultureInfo.InvariantCulture)}%\" title=\"{E(label)}\"></div>");
            }
            sb.Append("</div><div class=\"labels\">");
            foreach (var m in Months) sb.Append($"<span>{E(m.Month.ToString("MMM", c))}</span>");
            sb.Append("</div>");
        }
        if (Milestones.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Meilensteine"))}</h2><ul>");
            foreach (var s in Milestones) sb.Append($"<li>{E(s)}</li>");
            sb.Append("</ul>");
        }
        if (Miners.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Miner"))}</h2><table><tr><th>{E(L.T("Miner"))}</th><th>{E(L.T("Laufzeit"))}</th><th>{E(L.T("Verfügbarkeit"))}</th>");
            sb.Append($"<th>{E(L.T("Ø Hashrate"))}</th><th>J/TH</th><th>kWh</th><th>{E(L.T("Beste Difficulty"))}</th></tr>");
            foreach (var m in Miners)
                sb.Append($"<tr><td>{E(m.Name)}</td><td>{E(L.T("{0} h", m.OnlineHours.ToString("N0", c)))}</td>" +
                          $"<td>{E(m.Availability is { } a ? (a * 100).ToString("0.0", c) + " %" : "–")}</td><td>{E(FormatHash(m.AvgHashGh))}</td>" +
                          $"<td>{E(m.Jth is { } j ? j.ToString("0.0", c) : "–")}</td><td>{E(m.Kwh.ToString("N0", c))}</td><td>{E(m.BestDiff ?? "–")}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<p class=\"muted\">{E(L.T("Erstellt mit BitaxeTuner am {0} – ohne Adressen und Wallets.", DateTime.Now.ToString("g", c)))}</p></body></html>");
        return sb.ToString();
    }
}
