using System.Globalization;
using System.Net;
using System.Text;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Reports;

/// <summary>Eine Zeile im Vergleichsbericht: Text je Miner und der Bestwert (falls die Zeile einen hat).</summary>
public sealed record CompareReportRow(string Label, IReadOnlyList<string> Cells, IReadOnlyList<bool> Best);

/// <summary>Ein Diagramm: je Miner eine Linie über den Zeitraum.</summary>
public sealed record CompareReportChart(string Title, string Unit, IReadOnlyList<CompareReportSeries> Series);
public sealed record CompareReportSeries(string Name, IReadOnlyList<(DateTime Time, double Value)> Points);

public sealed record CompareReport(DateTime Created, DateTime From, DateTime To, string RangeLabel,
    IReadOnlyList<string> Miners, IReadOnlyList<CompareReportRow> Rows, IReadOnlyList<CompareReportChart> Charts);

/// <summary>
/// Vergleichsbericht zum Ausdrucken (Desktop und Server gleich): ausgewählte Miner nebeneinander, gewählte Werte
/// (aktuell, Mittel des Zeitraums, letzter Benchmark) und Diagramme mit je einer Linie pro Miner.
/// Die Schlüssel der Werte/Diagramme sind fest – die Browser-Oberfläche nutzt dieselben.
/// </summary>
public static class CompareReports
{
    public const int MaxMiners = 6;
    public static readonly string[] Ranges = ["24h", "7d", "30d"];

    /// <summary>Wählbare Werte: Schlüssel, Beschriftung, Gruppe.</summary>
    public static IReadOnlyList<(string Key, string Label, string Group)> Values() =>
    [
        ("model", L.T("Modell / Profil"), "now"),
        ("hashrate", L.T("Hashrate"), "now"),
        ("expected", L.T("Soll erreicht"), "now"),
        ("power", L.T("Leistung"), "now"),
        ("efficiency", L.T("Effizienz"), "now"),
        ("clock", L.T("Frequenz / Spannung"), "now"),
        ("temp", L.T("ASIC-Temperatur"), "now"),
        ("vrTemp", L.T("VR-Temperatur"), "now"),
        ("fan", L.T("Lüfter"), "now"),
        ("errorPercent", L.T("Fehlerrate"), "now"),
        ("shares", L.T("Shares"), "now"),
        ("bestDiff", L.T("Best Diff"), "now"),
        ("poolDifficulty", L.T("Pool-Difficulty"), "now"),
        ("uptime", L.T("Laufzeit"), "now"),
        ("firmware", L.T("Firmware"), "now"),
        ("avgHash", L.T("Ø Hashrate"), "range"),
        ("avgPower", L.T("Ø Leistung"), "range"),
        ("avgEff", L.T("Ø Effizienz"), "range"),
        ("avgTemp", L.T("Ø Temperatur"), "range"),
        ("tempPerWatt", L.T("Ø Temperatur je Watt"), "range"),
        ("avgVrTemp", L.T("Ø VR-Temperatur"), "range"),
        ("avgFan", L.T("Ø Lüfter"), "range"),
        ("availability", L.T("Verfügbarkeit"), "range"),
        ("tuning", L.T("Tuning-Änderungen"), "range"),
        ("benchHash", L.T("Beste Hashrate (Benchmark)"), "bench"),
        ("benchEff", L.T("Beste Effizienz (Benchmark)"), "bench"),
        ("maxStable", L.T("Stabil bis"), "bench"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Charts() =>
    [
        ("hashrate", L.T("Hashrate")),
        ("efficiency", L.T("Effizienz")),
        ("power", L.T("Leistung")),
        ("temp", L.T("ASIC-Temperatur")),
        ("vrTemp", L.T("VR-Temperatur")),
        ("fan", L.T("Lüfter")),
    ];

    public static readonly string[] DefaultValues = ["model", "hashrate", "efficiency", "temp", "avgHash", "avgEff", "availability", "benchHash", "benchEff"];
    public static readonly string[] DefaultCharts = ["hashrate", "efficiency", "temp"];

    /// <summary>Vorauswahl „Kühlung“: Warum läuft ein Miner heißer als ein gleicher? (z. B. für eine Frage in der Community)</summary>
    public static readonly string[] CoolingValues = ["model", "clock", "power", "temp", "vrTemp", "fan", "avgTemp", "tempPerWatt", "avgVrTemp", "avgFan", "avgPower", "firmware"];
    public static readonly string[] CoolingCharts = ["temp", "vrTemp", "fan", "power"];

    public static TimeSpan Span(string range) => range switch { "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };

    public static string RangeLabel(string range) => range switch { "7d" => L.T("7 Tage"), "30d" => L.T("30 Tage"), _ => L.T("24 h") };

    private static CultureInfo C => L.Culture;

    private static string Hash(double gh) => gh >= 1000 ? (gh / 1000).ToString("0.00", C) + " TH/s" : gh.ToString("0", C) + " GH/s";

    /// <summary>Daten für den Bericht sammeln. Unbekannte Schlüssel werden ignoriert, die Reihenfolge folgt dem Katalog.</summary>
    public static CompareReport Build(MinerHub hub, IReadOnlyList<HubDevice> devices, string range, IEnumerable<string> values, IEnumerable<string> charts, DateTime now)
    {
        if (devices.Count == 0) throw new LocalizedException("Bitte mindestens einen Miner auswählen.");
        if (devices.Count > MaxMiners) throw new LocalizedException("Höchstens {0} Miner je Bericht.", MaxMiners);
        if (!Ranges.Contains(range)) range = "24h";
        var from = now - Span(range);
        var wanted = values.ToHashSet(StringComparer.Ordinal);
        var wantedCharts = charts.ToHashSet(StringComparer.Ordinal);

        var history = hub.History;
        var averages = devices.Select(d => history?.Average(d.Host, from, now)).ToList();
        var bench = devices.Select(d => hub.Results.LoadLatest(d.Host, d.State.Normalized?.Hostname)?.Results ?? []).ToList();
        var needHealth = wanted.Overlaps(["avgVrTemp", "avgFan"]) || wantedCharts.Overlaps(["vrTemp", "fan"]);
        var health = devices.Select(d => needHealth && history is not null ? history.QueryHealth(d.Host, from, now) : []).ToList();

        var rows = new List<CompareReportRow>();
        foreach (var (key, label, _) in Values())
        {
            if (!wanted.Contains(key)) continue;
            var cells = new List<string>();
            var nums = new List<double?>();
            for (var i = 0; i < devices.Count; i++)
            {
                var (text, num) = Cell(key, hub, devices[i], averages[i], bench[i], health[i], from, now);
                cells.Add(text);
                nums.Add(num);
            }
            rows.Add(new CompareReportRow(label, cells, BestFlags(key, nums)));
        }

        var chartList = new List<CompareReportChart>();
        if (history is not null)
        {
            var maxPoints = range == "24h" ? 288 : 400;
            var samples = devices.Select(d => history.Query(d.Host, from, now, maxPoints)).ToList();
            foreach (var (key, label) in Charts())
            {
                if (!wantedCharts.Contains(key)) continue;
                if (key is "vrTemp" or "fan")
                {
                    var hs = devices.Select((d, i) => new CompareReportSeries(d.Title, health[i]
                        .Select(x => (x.Time, Value: key == "fan" ? (double)x.FanRpm : x.VrTemp)).Where(p => p.Value > 0).ToList())).ToList();
                    chartList.Add(new CompareReportChart(label, key == "fan" ? "rpm" : "°C", hs));
                    continue;
                }
                var series = devices.Select((d, i) => new CompareReportSeries(d.Title, samples[i]
                    .Select(s => (s.Time, Value: key switch
                    {
                        "efficiency" => s.HashRateGh > 1 ? s.Power / (s.HashRateGh / 1000) : double.NaN,
                        "power" => s.Power,
                        "temp" => s.Temp,
                        _ => s.HashRateGh,
                    }))
                    .Where(p => !double.IsNaN(p.Value) && p.Value > 0)
                    .ToList())).ToList();
                var unit = key switch { "efficiency" => "J/TH", "power" => "W", "temp" => "°C", _ => "GH/s" };
                chartList.Add(new CompareReportChart(label, unit, series));
            }
        }
        return new CompareReport(now, from, now, RangeLabel(range), devices.Select(d => d.Title).ToList(), rows, chartList);
    }

    private static (string Text, double? Num) Cell(string key, MinerHub hub, HubDevice d, WindowAverage? avg, IReadOnlyList<StepResult> results,
        IReadOnlyList<HistoryStore.HealthSample> health, DateTime from, DateTime now)
    {
        var n = d.Info;          // nur wenn online
        var raw = d.State.Online ? d.State.Info : null;
        const string none = "–";
        switch (key)
        {
            case "model": return ($"{d.State.Normalized?.DeviceModel ?? d.State.Normalized?.AsicModel ?? none} / {d.Profile.Name}", null);
            case "hashrate": return n is null ? ("offline", null) : (Hash(n.HashRateGh), n.HashRateGh);
            case "expected":
                return n is { ExpectedHashRateGh: > 0 } e ? ((e.HashRateGh / e.ExpectedHashRateGh!.Value * 100).ToString("0", C) + " %", e.HashRateGh / e.ExpectedHashRateGh.Value) : (none, null);
            case "power": return n is null ? (none, null) : (n.PowerW.ToString("0.0", C) + " W", null);
            case "efficiency": return n?.EfficiencyJth is { } j ? (j.ToString("0.00", C) + " J/TH", j) : (none, null);
            case "clock": return n is null ? (none, null) : ($"{n.FrequencyMhz} MHz / {n.CoreVoltageMv} mV", null);
            case "temp": return n?.MaxChipTempC is { } t ? (t.ToString("0.0", C) + " °C", t) : (none, null);
            case "vrTemp": return n?.VrTempC is { } vr ? (vr.ToString("0", C) + " °C", vr) : (none, null);
            case "fan": return n is null ? (none, null) : ($"{(n.FanRpm is { } rpm ? rpm.ToString(C) + " rpm" : none)} ({n.FanPercent?.ToString(C) ?? none} %)", null);
            case "errorPercent": return n?.ErrorPercent is { } ep ? (ep.ToString("0.00", C) + " %", ep) : (none, null);
            case "shares": return n is null ? (none, null) : (L.T("{0} / {1} abgelehnt", n.SharesAccepted, n.SharesRejected), null);
            case "bestDiff": return raw?.bestDiff is { Length: > 0 } bd ? (bd, Difficulty.Parse(bd)) : (none, null);
            case "poolDifficulty": return n?.PoolDifficulty is { } pd ? (Difficulty.Format(pd), null) : (none, null);
            case "uptime": return n is null ? (none, null) : (TimeSpan.FromSeconds(n.UptimeSeconds) is var u && u.TotalDays >= 1 ? L.T("{0} d {1} h", (int)u.TotalDays, u.Hours) : L.T("{0} h {1} min", u.Hours, u.Minutes), null);
            case "firmware": return (hub.FirmwareText(d.State) is { Length: > 0 } fw ? fw : none, null);
            case "avgHash": return avg is null ? (none, null) : (Hash(avg.HashRateGh), avg.HashRateGh);
            case "avgPower": return avg is null ? (none, null) : (avg.Power.ToString("0.0", C) + " W", null);
            case "avgEff": return avg?.EfficiencyJth is { } ae ? (ae.ToString("0.00", C) + " J/TH", ae) : (none, null);
            case "avgTemp": return avg is null ? (none, null) : (avg.Temp.ToString("0.0", C) + " °C", avg.Temp);
            case "tempPerWatt":
                return avg is { Power: > 0 } tw ? ((tw.Temp / tw.Power).ToString("0.00", C) + " °C/W", tw.Temp / tw.Power) : (none, null);
            case "avgVrTemp":
                return health.Where(x => x.VrTemp > 0).ToList() is { Count: > 0 } vh ? (vh.Average(x => x.VrTemp).ToString("0.0", C) + " °C", vh.Average(x => x.VrTemp)) : (none, null);
            case "avgFan":
                return health.Where(x => x.FanRpm > 0).ToList() is { Count: > 0 } fh
                    ? ($"{fh.Average(x => x.FanRpm).ToString("0", C)} rpm ({fh.Average(x => x.FanPercent).ToString("0", C)} %)", null) : (none, null);
            case "availability":
                return hub.History?.Availability(d.Host, from) is { } a ? ((a * 100).ToString("0.0", C) + " %", a) : (none, null);
            case "tuning": return hub.History is { } h ? (h.QueryTuningEvents(d.Host, from, now).Count.ToString(C), null) : (none, null);
            case "benchHash":
                return ResultRanking.Best(results, RankingMode.MaxHashrate) is { } bh
                    ? ($"{Hash(bh.AvgHashRateGh)} · {bh.FrequencyMhz} MHz / {bh.CoreVoltageMv} mV", bh.AvgHashRateGh) : (L.T("kein Benchmark"), null);
            case "benchEff":
                return ResultRanking.Best(results, RankingMode.Efficiency) is { } be && be.EfficiencyJth is { } bj
                    ? ($"{bj.ToString("0.00", C)} J/TH · {be.FrequencyMhz} MHz / {be.CoreVoltageMv} mV", bj) : (L.T("kein Benchmark"), null);
            case "maxStable":
                return results.Where(r => r.IsStable).Select(r => (int?)r.FrequencyMhz).Max() is { } ms ? ($"{ms} MHz", ms) : (none, null);
            default: return (none, null);
        }
    }

    /// <summary>Bestwert je Zeile – nur, wenn sich die Werte unterscheiden (sonst wären alle „am besten“).</summary>
    private static IReadOnlyList<bool> BestFlags(string key, IReadOnlyList<double?> nums)
    {
        bool? higherIsBetter = key switch
        {
            "hashrate" or "expected" or "avgHash" or "availability" or "bestDiff" or "benchHash" or "maxStable" => true,
            "efficiency" or "avgEff" or "temp" or "vrTemp" or "avgTemp" or "tempPerWatt" or "avgVrTemp" or "errorPercent" or "benchEff" => false,
            _ => null,
        };
        var present = nums.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (higherIsBetter is null || present.Count < 2 || present.Max() - present.Min() < 1e-9)
            return nums.Select(_ => false).ToList();
        var best = higherIsBetter.Value ? present.Max() : present.Min();
        return nums.Select(x => x is { } v && Math.Abs(v - best) < 1e-9).ToList();
    }

    // ---------- Druckbare Seite ----------

    private static readonly string[] Colors = ["#E07A00", "#2563EB", "#16A34A", "#DC2626", "#7C3AED", "#0891B2"];

    public static string Html(CompareReport r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var title = L.T("Miner-Vergleich");
        sb.Append($"<!doctype html><html lang=\"{Loc.Current.Language}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(title)} – BitaxeTuner</title><style>");
        sb.Append("""
            body{font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif;color:#111;background:#fff;margin:32px auto;max-width:1000px;padding:0 16px}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 8px}.muted{color:#666}
            table{border-collapse:collapse;width:100%}th,td{padding:6px 8px;border-bottom:1px solid #e5e5e5;text-align:left;vertical-align:top}
            th{font-weight:600;background:#f6f6f6}td.best{color:#15803d;font-weight:600}
            .sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:baseline}
            .chart{break-inside:avoid;page-break-inside:avoid;margin:8px 0 18px}.legend{font-size:12px;color:#444}.legend span{margin-right:14px}
            svg text{font:11px system-ui,sans-serif;fill:#555}
            .print{margin:16px 0}@media print{.print{display:none}body{margin:0;max-width:none}h2{break-after:avoid}}
            """);
        sb.Append("</style></head><body>");
        sb.Append($"<h1>{E(title)}</h1><div class=\"muted\">{E(string.Join(" · ", r.Miners))}<br>");
        sb.Append($"{E(L.T("Zeitraum: {0} ({1} – {2})", r.RangeLabel, r.From.ToString("g", C), r.To.ToString("g", C)))}</div>");
        sb.Append($"<div class=\"print muted\">{E(L.T("Als PDF speichern: Drucken (Strg+P) → „Als PDF speichern“."))}</div>");

        if (r.Rows.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Werte"))}</h2><table><tr><th></th>");
            for (var i = 0; i < r.Miners.Count; i++)
                sb.Append($"<th><span class=\"sw\" style=\"background:{Colors[i % Colors.Length]}\"></span>{E(r.Miners[i])}</th>");
            sb.Append("</tr>");
            foreach (var row in r.Rows)
            {
                sb.Append($"<tr><th>{E(row.Label)}</th>");
                for (var i = 0; i < row.Cells.Count; i++)
                    sb.Append(row.Best[i] ? $"<td class=\"best\">{E(row.Cells[i])} ★</td>" : $"<td>{E(row.Cells[i])}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            sb.Append($"<p class=\"muted\">{E(L.T("„Aktuell“ = Stand beim Erstellen; Ø-Werte und Verfügbarkeit über den Zeitraum (nur Online-Minuten); Benchmark = letzter Lauf je Miner. ★ = bester Wert."))}</p>");
        }

        foreach (var chart in r.Charts)
        {
            sb.Append($"<div class=\"chart\"><h2>{E(chart.Title)} ({E(chart.Unit)})</h2>");
            sb.Append(Svg(chart, r.From, r.To));
            sb.Append("<div class=\"legend\">");
            for (var i = 0; i < chart.Series.Count; i++)
                sb.Append($"<span><span class=\"sw\" style=\"background:{Colors[i % Colors.Length]}\"></span>{E(chart.Series[i].Name)}{(chart.Series[i].Points.Count == 0 ? E(L.T(" (keine Daten)")) : "")}</span>");
            sb.Append("</div></div>");
        }
        sb.Append($"<p class=\"muted\">{E(L.T("Erstellt mit BitaxeTuner am {0}. Ohne IP- und Wallet-Adressen – zum Teilen geeignet.", r.Created.ToString("g", C)))}</p></body></html>");
        return sb.ToString();
    }

    private static string Svg(CompareReportChart chart, DateTime from, DateTime to)
    {
        const int w = 960, h = 220, left = 56, right = 10, top = 10, bottom = 24;
        var all = chart.Series.SelectMany(s => s.Points).Select(p => p.Value).ToList();
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(chart.Title)}\">");
        if (all.Count == 0)   // keine große leere Fläche drucken
            return $"<p class=\"muted\">{WebUtility.HtmlEncode(L.T("Keine Messwerte im Zeitraum."))}</p>";
        double min = all.Min(), max = all.Max();
        var pad = Math.Max((max - min) * 0.08, Math.Abs(max) * 0.02 + 0.01);
        min = chart.Unit == "°C" ? min - pad : Math.Max(0, min - pad);
        max += pad;
        double X(DateTime t) => left + (t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds) * (w - left - right);
        double Y(double v) => top + (1 - (v - min) / (max - min)) * (h - top - bottom);
        for (var g = 0; g <= 4; g++)
        {
            var v = min + (max - min) * g / 4;
            var y = Y(v);
            sb.Append($"<line x1=\"{left}\" x2=\"{w - right}\" y1=\"{y.ToString("0.#", inv)}\" y2=\"{y.ToString("0.#", inv)}\" stroke=\"#e5e5e5\"/>");
            var label = chart.Unit == "GH/s" && max >= 1000 ? (v / 1000).ToString("0.00", C) + " TH/s" : v.ToString(max - min < 10 ? "0.0" : "0", C);
            sb.Append($"<text x=\"{left - 6}\" y=\"{(y + 4).ToString("0.#", inv)}\" text-anchor=\"end\">{WebUtility.HtmlEncode(label)}</text>");
        }
        var fmt = (to - from).TotalDays > 2 ? "dd.MM." : "HH:mm";
        foreach (var t in new[] { from, from + (to - from) / 2, to })
            sb.Append($"<text x=\"{X(t).ToString("0.#", inv)}\" y=\"{h - 6}\" text-anchor=\"{(t == from ? "start" : t == to ? "end" : "middle")}\">{t.ToString(fmt, C)}</text>");
        for (var i = 0; i < chart.Series.Count; i++)
        {
            var pts = chart.Series[i].Points;
            if (pts.Count == 0) continue;
            // Lücken (Miner offline) nicht verbinden: neue Linie bei mehr als dem 3-fachen Abstand
            var gap = pts.Count > 1 ? (pts[^1].Time - pts[0].Time).TotalSeconds / pts.Count * 3 : double.MaxValue;
            var segment = new List<string>();
            void Flush()
            {
                if (segment.Count > 0)
                    sb.Append($"<polyline fill=\"none\" stroke=\"{Colors[i % Colors.Length]}\" stroke-width=\"1.6\" points=\"{string.Join(" ", segment)}\"/>");
                segment.Clear();
            }
            for (var k = 0; k < pts.Count; k++)
            {
                if (k > 0 && (pts[k].Time - pts[k - 1].Time).TotalSeconds > gap) Flush();
                segment.Add($"{X(pts[k].Time).ToString("0.#", inv)},{Y(pts[k].Value).ToString("0.#", inv)}");
            }
            Flush();
        }
        return sb.Append("</svg>").ToString();
    }
}
