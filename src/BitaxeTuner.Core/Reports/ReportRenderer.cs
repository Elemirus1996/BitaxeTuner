using System.Globalization;
using System.Net;
using System.Text;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Reports;

/// <summary>Bericht als CSV (Semikolon, UTF-8 mit BOM – Excel) und als druckbare HTML-Seite (PDF über „Drucken“).</summary>
public static class ReportRenderer
{
    private static CultureInfo C => L.Culture;

    public static string Title(PeriodReport r) => r.Period.Length == 4
        ? L.T("Jahresbericht {0}", r.Period)
        : L.T("Monatsbericht {0}", r.From.ToString("MMMM yyyy", C));

    private static string F(double? v, string format) => v is { } x ? x.ToString(format, C) : "";

    public static byte[] Csv(PeriodReport r)
    {
        var sb = new StringBuilder();
        void Line(params string?[] cells) => sb.AppendLine(string.Join(';', cells.Select(Escape)));
        Line(Title(r));
        Line(L.T("Zeitraum"), r.From.ToString("d", C), r.To.AddDays(-1).ToString("d", C), r.Partial ? L.T("läuft noch") : "");
        sb.AppendLine();
        Line(L.T("Miner"), L.T("Verfügbarkeit (%)"), L.T("Ø Hashrate (GH/s)"), L.T("Ø Temperatur (°C)"), L.T("Ø Leistung (W)"),
            "J/TH", "kWh (AxeOS)", L.T("Tuning-Änderungen"));
        foreach (var m in r.Miners)
            Line(m.Name, F(m.Availability * 100, "0.0"), F(m.AvgHashGh, "0"), F(m.AvgTemp, "0.0"), F(m.AvgPowerW, "0.0"),
                F(m.Jth, "0.00"), F(m.Kwh, "0.00"), m.TuningChanges.ToString(C));
        if (r.Plugs.Count > 0)
        {
            sb.AppendLine();
            Line(L.T("Smart Plug"), L.T("Rolle"), "kWh", L.T("Messstunden"));
            foreach (var p in r.Plugs) Line(p.Name, p.Role, F(p.Kwh, "0.00"), F(p.MeasuredHours, "0"));
        }
        sb.AppendLine();
        Line(L.T("Energie gesamt (kWh)"), F(r.Energy.Kwh, "0.00"));
        Line(L.T("Stromkosten ({0})", r.Currency), F(r.Energy.Cost, "0.00"));
        Line(L.T("Ø Strompreis (ct/kWh)"), F(r.Energy.AvgCt, "0.0"));
        Line(L.T("Stunden mit Stundenpreis"), r.Energy.DynamicHours.ToString(C), L.T("von {0}", r.Energy.Hours));
        if (r.Income.Count > 0)
        {
            sb.AppendLine();
            Line(L.T("Zuflüsse"), L.T("Anzahl"), L.T("Menge"), L.T("Wert ({0})", "EUR"), L.T("ohne Kurs"));
            foreach (var i in r.Income)
                Line(i.Coin, i.Count.ToString(C), i.Amount.ToString("0.00000000", C), i.Eur.ToString("0.00", C), i.EurMissing.ToString(C));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Escape(string? s) =>
        s is null ? "" : s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Html(PeriodReport r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var lang = Loc.Current.Language;
        sb.Append($"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(Title(r))} – BitaxeTuner</title><style>");
        sb.Append("""
            body{font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif;color:#111;background:#fff;margin:32px auto;max-width:960px;padding:0 16px}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 8px}.muted{color:#666}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px;margin:16px 0}
            .tile{border:1px solid #ddd;border-radius:8px;padding:10px 12px}.tile b{display:block;font-size:20px}
            table{border-collapse:collapse;width:100%}th,td{padding:6px 8px;border-bottom:1px solid #e5e5e5;text-align:right}
            th:first-child,td:first-child{text-align:left}th{font-weight:600;background:#f6f6f6}
            .note{background:#fff7e6;border:1px solid #f0d9a8;border-radius:6px;padding:8px 10px;margin:12px 0}
            .print{margin:16px 0}@media print{.print{display:none}body{margin:0}}
            """);
        sb.Append("</style></head><body>");
        sb.Append($"<h1>{E(Title(r))}</h1><div class=\"muted\">{E(r.From.ToString("D", C))} – {E(r.To.AddDays(-1).ToString("D", C))}");
        if (r.Partial) sb.Append($" · {E(L.T("läuft noch – Stand {0}", DateTime.Now.ToString("g", C)))}");
        sb.Append("</div>");
        // Kein Inline-Skript (Content-Security-Policy des Servers): Hinweis statt Knopf, beim Drucken ausgeblendet
        sb.Append($"<div class=\"print muted\">{E(L.T("Als PDF speichern: Drucken (Strg+P) → „Als PDF speichern“."))}</div>");
        if (r.DataFrom is { } df)
            sb.Append($"<div class=\"note\">{E(L.T("Messwerte liegen erst ab {0} vor – der Zeitraum davor fehlt im Bericht.", df.ToString("g", C)))}</div>");

        sb.Append("<div class=\"tiles\">");
        void Tile(string label, string value) => sb.Append($"<div class=\"tile\"><span class=\"muted\">{E(label)}</span><b>{E(value)}</b></div>");
        Tile(L.T("Ø Hashrate gesamt"), r.TotalAvgHashGh is { } gh ? (gh >= 1000 ? (gh / 1000).ToString("0.00", C) + " TH/s" : gh.ToString("0", C) + " GH/s") : "–");
        Tile(L.T("Energie"), r.Energy.Kwh.ToString("0.00", C) + " kWh");
        Tile(L.T("Stromkosten"), r.Energy.Cost.ToString("0.00", C) + " " + r.Currency);
        Tile(L.T("Ø Strompreis"), r.Energy.AvgCt is { } ct ? ct.ToString("0.0", C) + " ct/kWh" : "–");
        if (r.Income.Count > 0) Tile(L.T("Zuflüsse"), r.IncomeEur.ToString("0.00", C) + " €");
        sb.Append("</div>");
        if (r.Energy.DynamicHours > 0)
            sb.Append($"<div class=\"muted\">{E(L.T("{0} von {1} Stunden mit Stundenpreis der Strompreis-Quelle, sonst fester Preis.", r.Energy.DynamicHours, r.Energy.Hours))}</div>");

        sb.Append($"<h2>{E(L.T("Miner"))}</h2><table><tr><th>{E(L.T("Miner"))}</th><th>{E(L.T("Verfügbarkeit"))}</th><th>{E(L.T("Ø Hashrate"))}</th>");
        sb.Append($"<th>{E(L.T("Ø Temperatur"))}</th><th>{E(L.T("Ø Leistung"))}</th><th>J/TH</th><th>kWh</th><th>{E(L.T("Tuning"))}</th></tr>");
        foreach (var m in r.Miners)
            sb.Append($"<tr><td>{E(m.Name)}</td><td>{E(m.Availability is { } a ? (a * 100).ToString("0.0", C) + " %" : "–")}</td>" +
                      $"<td>{E(m.AvgHashGh is { } h ? h.ToString("0", C) + " GH/s" : "–")}</td><td>{E(m.AvgTemp is { } t ? t.ToString("0.0", C) + " °C" : "–")}</td>" +
                      $"<td>{E(m.AvgPowerW is { } w ? w.ToString("0.0", C) + " W" : "–")}</td><td>{E(m.Jth is { } j ? j.ToString("0.00", C) : "–")}</td>" +
                      $"<td>{E(m.Kwh.ToString("0.00", C))}</td><td>{m.TuningChanges}</td></tr>");
        sb.Append("</table>");

        if (r.Plugs.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Smart Plugs"))}</h2><table><tr><th>{E(L.T("Smart Plug"))}</th><th>kWh</th><th>{E(L.T("Messstunden"))}</th></tr>");
            foreach (var p in r.Plugs)
                sb.Append($"<tr><td>{E(p.Name)}</td><td>{E(p.Kwh.ToString("0.00", C))}</td><td>{E(p.MeasuredHours.ToString("0", C))}</td></tr>");
            sb.Append("</table>");
        }

        if (r.Income.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Zuflüsse"))}</h2><table><tr><th>Coin</th><th>{E(L.T("Anzahl"))}</th><th>{E(L.T("Menge"))}</th><th>EUR</th></tr>");
            foreach (var i in r.Income)
                sb.Append($"<tr><td>{E(i.Coin)}</td><td>{i.Count}</td><td>{E(i.Amount.ToString("0.00000000", C))}</td>" +
                          $"<td>{E(i.Eur.ToString("0.00", C))}{(i.EurMissing > 0 ? E(L.T(" ({0} ohne Kurs)", i.EurMissing)) : "")}</td></tr>");
            sb.Append("</table>");
            sb.Append($"<p class=\"muted\">{E(L.T("Zuflüsse aus dem Steuer-Bereich (Euro-Kurs zum Zeitpunkt des Zuflusses). Keine Steuerberatung."))}</p>");
        }
        sb.Append($"<p class=\"muted\">{E(L.T("Erstellt mit BitaxeTuner am {0}.", DateTime.Now.ToString("g", C)))}</p></body></html>");
        return sb.ToString();
    }
}
