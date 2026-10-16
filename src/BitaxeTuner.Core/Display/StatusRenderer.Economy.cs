using BitaxeTuner.Core.I18n;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace BitaxeTuner.Core.Display;

/// <summary>0.9.16, Seite „Ertrag“: ein Zeitraum (24 h, 7 Tage, 30 Tage, laufender Monat). Income/Net null = Ertrag unbekannt.</summary>
public sealed record DisplayProfitRow(string Label, decimal? Income, decimal Cost, decimal? Net, double Kwh, bool Partial);

/// <param name="Source">"pool" (Gutschriften beim Pool, Kurs beim Abruf) oder "tax" (dokumentierte Zuflüsse).</param>
public sealed record DisplayProfit(string Currency, string Source, IReadOnlyList<DisplayProfitRow> Rows);

/// <summary>0.9.16, Seite „Solo-Chance“ je Coin (Difficulty null = noch nicht geladen, PerDay null = kein Miner darauf).</summary>
public sealed record DisplaySoloCoin(string Symbol, int Miners, double Gh, double? Difficulty, double? PerDay, double? PerYear, double? ExpectedDays);

public static partial class StatusRenderer
{
    // ---------- Ertrag (0.9.16) ----------

    private static void DrawProfit(IImageProcessingContext ctx, DisplayModel m)
    {
        var p = m.Profit;
        PageHeader(ctx, m, L.T("Ertrag & Strom"));
        if (p is null || p.Rows.Count == 0)
        {
            TextCenter(ctx, L.T("Noch keine Verlaufsdaten."), Regular.Value.CreateFont(26), Width / 2f, 200, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var head = Regular.Value.CreateFont(19);
        var value = Bold.Value.CreateFont(30);
        var label = Regular.Value.CreateFont(26);
        // Spalten: Zeitraum | Ertrag | Strom | Saldo (rechtsbündig)
        float[] right = [0, 400, 590, Width - 16];
        Text(ctx, L.T("Zeitraum"), head, 16, 78, Ink);
        TextRight(ctx, L.T("Ertrag"), head, right[1], 78, Ink);
        TextRight(ctx, L.T("Strom"), head, right[2], 78, Ink);
        TextRight(ctx, L.T("Saldo"), head, right[3], 78, Ink);
        string Money(decimal? v, bool signed = false) => v is not { } x ? "–"
            : (signed && x > 0 ? "+" : "") + x.ToString("N2", De) + " " + p.Currency;
        var y = 110f;
        foreach (var r in p.Rows)
        {
            ctx.Fill(Crisp, Rule, new RectangleF(16, y, Width - 32, 1));
            Text(ctx, r.Label, label, 16, y + 18, Ink);
            TextRight(ctx, Money(r.Income) + (r.Partial ? "*" : ""), value, right[1], y + 14, Ink);
            TextRight(ctx, Money(r.Cost), value, right[2], y + 14, Ink);
            TextRight(ctx, Money(r.Net, signed: true), value, right[3], y + 14, r.Net < 0 ? Red : r.Net > 0 ? Good : Ink);
            Text(ctx, L.T("{0} kWh", N(r.Kwh, "0.0")), Regular.Value.CreateFont(17), 16, y + 52, Ink);
            y += 76;
        }
        var note = p.Rows.All(r => r.Income is null)
            ? L.T("Ertrag unbekannt: Pool-Konto einrichten oder Zuflüsse in der Steuer erfassen.")
            : p.Source == "pool" ? L.T("Ertrag: Gutschriften beim Pool zum aktuellen Kurs (Schätzung).") : L.T("Ertrag: dokumentierte Zuflüsse (Steuer).");
        if (p.Rows.Any(r => r.Partial)) note += " " + L.T("* ohne Coins ohne Kurs");
        var small = Regular.Value.CreateFont(18);
        Text(ctx, Fit(note, small, Width - 32), small, 16, y + 6, Ink);
        DrawFooter(ctx, m);
    }

    // ---------- Solo-Chance (0.9.16) ----------

    private static void DrawSolo(IImageProcessingContext ctx, DisplayModel m)
    {
        PageHeader(ctx, m, L.T("Solo-Chance"));
        var coins = m.Solo ?? [];
        var shown = coins.Where(c => c.Miners > 0).ToList();
        if (shown.Count == 0) shown = coins.Take(1).ToList();
        if (shown.Count == 0)
        {
            TextCenter(ctx, L.T("Netzwerk-Difficulty wird geladen …"), Regular.Value.CreateFont(26), Width / 2f, 200, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var height = (430f - 74) / shown.Count;
        var y = 74f;
        foreach (var c in shown)
        {
            DrawSoloCoin(ctx, c, y, height, shown.Count == 1);
            y += height;
            if (c != shown[^1]) ctx.Fill(Crisp, Rule, new RectangleF(16, y - 4, Width - 32, 1));
        }
        DrawFooter(ctx, m);
    }

    private static void DrawSoloCoin(IImageProcessingContext ctx, DisplaySoloCoin c, float y, float height, bool large)
    {
        var big = Bold.Value.CreateFont(large ? 64 : 46);
        var small = Regular.Value.CreateFont(20);
        Text(ctx, c.Symbol, Bold.Value.CreateFont(large ? 44 : 34), 16, y + 8, Ink);
        Text(ctx, c.Miners > 0 ? L.T("{0} Miner · {1}", c.Miners, FormatHash(c.Gh)) : L.T("kein Miner auf {0}", c.Symbol), small, 16, y + (large ? 64 : 52), Ink);
        if (c.Difficulty is not { } diff)
        {
            Text(ctx, L.T("Netzwerk-Difficulty wird geladen …"), Regular.Value.CreateFont(24), 260, y + 20, Ink);
            return;
        }
        Text(ctx, L.T("Difficulty {0}", Monitoring.Difficulty.Format(diff)), small, 16, y + (large ? 92 : 78), Ink);
        if (c.PerYear is not { } year || c.PerDay is not { } day || c.ExpectedDays is not { } days) return;
        const float x = 260;
        Text(ctx, L.T("pro Jahr"), small, x, y + 4, Ink);
        Text(ctx, SoloPercent(year), big, x, y + 26, Red);
        var font = Regular.Value.CreateFont(large ? 24 : 20);
        var month = 1 - Math.Pow(1 - day, 30.44);
        var lineY = y + (large ? 104 : 82);
        Text(ctx, Fit(L.T("{0} pro Monat · {1} pro Tag", SoloPercent(month), SoloPercent(day)), font, Width - x - 16), font, x, lineY, Ink);
        var every = days < 730 ? L.T("{0} Tage", days.ToString("N0", De)) : L.T("{0} Jahre", (days / 365).ToString("N0", De));
        Text(ctx, Fit(L.T("im Mittel ein Block alle {0}", every), font, Width - x - 16), font, x, lineY + (large ? 34 : 28), Ink);
        if (large)
        {
            Text(ctx, Fit(L.T("Jeder Hash ist ein Los: Die Chance hängt nur von Hashrate und Difficulty ab."), small, Width - 32), small, 16, y + 220, Ink);
            Text(ctx, Fit(L.T("Ein Fund kann morgen kommen – oder nie. Der Mittelwert ist keine Wartezeit."), small, Width - 32), small, 16, y + 248, Ink);
        }
    }

    // ---------- Systemzustand (0.9.16) ----------

    private static void DrawSystem(IImageProcessingContext ctx, DisplayModel m)
    {
        var checks = m.System ?? [];
        var problems = checks.Count(c => c.Problem);
        PageHeader(ctx, m, L.T("Systemzustand"));
        var label = Regular.Value.CreateFont(checks.Count > 8 ? 21 : 24);
        var value = Bold.Value.CreateFont(checks.Count > 8 ? 21 : 24);
        var row = Math.Min(40f, 330f / Math.Max(1, checks.Count));
        var y = 80f;
        // 0.9.20: mehr als 9 Punkte → Probleme zuerst, damit keines unten abgeschnitten wird
        foreach (var c in (checks.Count > 9 ? checks.OrderBy(c => c.Problem ? 0 : 1).ToList() : checks).Take(9))
        {
            var color = c.Problem ? Red : Ink;
            var dot = new SixLabors.ImageSharp.Drawing.EllipsePolygon(28, y + row / 2 - 2, 9);
            if (c.Problem) ctx.Fill(Crisp, Red, dot);
            else if (_colors) ctx.Fill(Crisp, Good, dot);   // Spectra 6: grüner Punkt = in Ordnung
            else ctx.Draw(Crisp, Ink, 2, dot);
            Text(ctx, Fit(c.Label, label, 300), label, 48, y, Ink);
            TextRight(ctx, Fit(c.Value, value, Width - 380), value, Width - 16, y, color);
            y += row;
        }
        var summary = problems == 0 ? L.T("Alles in Ordnung.") : L.T("{0} Punkt(e) brauchen Aufmerksamkeit – Details in den Einstellungen → System.", problems);
        var small = Regular.Value.CreateFont(19);
        Text(ctx, Fit(summary, small, Width - 32), small, 16, 414, problems == 0 ? Good : Red);
        DrawFooter(ctx, m);
    }

    /// <summary>Prozent mit drei gültigen Stellen, sehr kleine Werte als „1 : N“.</summary>
    private static string SoloPercent(double p)
    {
        var v = p * 100;
        if (v >= 10) return v.ToString("0.0", De) + " %";
        if (v >= 0.001)
        {
            var digits = Math.Clamp(2 - (int)Math.Floor(Math.Log10(v)), 0, 5);
            return v.ToString("0." + new string('0', digits), De).TrimEnd('0').TrimEnd(De.NumberFormat.NumberDecimalSeparator[0]) + " %";
        }
        return "1 : " + (1 / p).ToString("N0", De);
    }
}
