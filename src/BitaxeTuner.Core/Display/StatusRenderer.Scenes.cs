using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace BitaxeTuner.Core.Display;

/// <summary>Was das E-Paper zeigt: Seiten im Wechsel und Sonderanzeigen.</summary>
public enum DisplayScene { Overview, Daily, Chart, Soak, Network, BlockFound, Alarm, BestDiff }

public sealed record DisplayBlockFound(string Miner, DateTime Time, int Count, long? Height, bool Example = false);
public sealed record DisplayBestDiff(string Miner, string Coin, string Previous, string Current, DateTime Time);
public sealed record DisplayDailyRow(string Name, double? Gh, double? Jth, double? Temp, double? Availability);
public sealed record DisplayDaily(double Gh, double W, double Kwh, double Cost, string Currency, string? BestDiff, string? BestDiffMiner,
    IReadOnlyList<DisplayDailyRow> Rows);
public sealed record DisplayPoint(DateTime Time, double Gh, double Temp);
public sealed record DisplaySoak(string Name, string Status, DateTime Started, DateTime Until, int FrequencyMhz, int CoreVoltageMv);
public sealed record DisplayPool(string Name, bool Online, string Pool, bool Fallback, double Accepted, double Rejected, string? BestDiff);
public sealed record DisplayNetwork(IReadOnlyList<DisplayPool> Pools, long? Height, string? LastBlockPool, DateTime? LastBlockTime);

public static partial class StatusRenderer
{
    // ---------- gemeinsame Teile ----------

    /// <summary>Kopf der Seiten: Seitentitel links, Seite x/y und Uhrzeit rechts.</summary>
    private static void PageHeader(IImageProcessingContext ctx, DisplayModel m, string title)
    {
        Text(ctx, title, Bold.Value, 32, 16, 14, Ink);
        TextRight(ctx, (m.PageLabel is { } p ? p + " · " : "") + m.Time.ToString("HH:mm", De), Regular.Value, 22, Width - 16, 22, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 62, Width - 32, 3));
    }

    private static void Tile(IImageProcessingContext ctx, float x, float y, float w, string label, string value, bool red = false)
    {
        Text(ctx, label, Regular.Value, 18, x, y, Ink);
        Text(ctx, Fit(value, Bold.Value.CreateFont(32), w - 10), Bold.Value.CreateFont(32), x, y + 24, red ? Red : Ink);
    }

    private static string N(double v, string format) => v.ToString(format, De);

    private static string Left(DateTime until, DateTime now)
    {
        var t = until - now;
        if (t <= TimeSpan.Zero) return "fertig";
        return t.TotalHours >= 2 ? $"noch {N(t.TotalHours, "0")} h" : $"noch {N(t.TotalMinutes, "0")} min";
    }

    // ---------- Seiten ----------

    private static void DrawDaily(IImageProcessingContext ctx, DisplayModel m)
    {
        var d = m.Daily!;
        PageHeader(ctx, m, "Tagesbilanz · letzte 24 h");
        var w = (Width - 32) / 4f;
        Tile(ctx, 16, 76, w, "Ø Hashrate", FormatHash(d.Gh));
        Tile(ctx, 16 + w, 76, w, "Ø Leistung", $"{N(d.W, "0.0")} W");
        Tile(ctx, 16 + 2 * w, 76, w, "Effizienz", d.Gh > 1 ? $"{N(d.W / (d.Gh / 1000), "0.0")} J/TH" : "–");
        Tile(ctx, 16 + 3 * w, 76, w, "Stromkosten", $"{N(d.Cost, "0.00")} {d.Currency}");
        if (d.BestDiff is { } bd)
            Text(ctx, Fit($"Best Diff (Rekord): {bd}" + (d.BestDiffMiner is { } bm ? $" · {bm}" : "") + $" · {N(d.Kwh, "0.00")} kWh",
                Regular.Value.CreateFont(21), Width - 32), Regular.Value.CreateFont(21), 16, 142, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 174, Width - 32, 1));

        var small = Regular.Value.CreateFont(17);
        TextRight(ctx, "Ø Hashrate", small, 470, 180, Ink);
        TextRight(ctx, "J/TH", small, 572, 180, Ink);
        TextRight(ctx, "Temp.", small, 668, 180, Ink);
        TextRight(ctx, "verfügbar", small, Width - 16, 180, Ink);
        var rows = d.Rows.Take(6).ToList();
        var rowH = rows.Count == 0 ? 0 : Math.Min(42, 236 / rows.Count);
        var font = (rowH >= 40 ? Regular.Value.CreateFont(26) : Regular.Value.CreateFont(22));
        var bold = (rowH >= 40 ? Bold.Value.CreateFont(26) : Bold.Value.CreateFont(22));
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var y = 204 + i * rowH;
            Text(ctx, Fit(r.Name, bold, 330), bold, 16, y, Ink);
            if (r.Gh is null)
            {
                TextRight(ctx, "keine Daten", font, Width - 16, y, Red);
                continue;
            }
            TextRight(ctx, FormatHash(r.Gh.Value), font, 470, y, Ink);
            TextRight(ctx, r.Jth is { } j ? N(j, "0.0") : "–", font, 572, y, Ink);
            TextRight(ctx, r.Temp is { } t ? $"{N(t, "0")} °C" : "–", font, 668, y, Ink);
            var low = r.Availability is < 0.98;
            TextRight(ctx, r.Availability is { } a ? $"{N(a * 100, "0.0")} %" : "–", font, Width - 16, y, low ? Red : Ink);
        }
        DrawFooter(ctx, m);
    }

    private static void DrawChart(IImageProcessingContext ctx, DisplayModel m)
    {
        var pts = m.Chart!.Where(p => p.Gh > 0).OrderBy(p => p.Time).ToList();
        PageHeader(ctx, m, "Hashrate · letzte 24 h");
        const float x0 = 96, x1 = Width - 20, y0 = 104, y1 = 400;
        var small = Regular.Value.CreateFont(17);
        if (pts.Count < 2)
        {
            TextCenter(ctx, "Noch zu wenige Messwerte.", Regular.Value.CreateFont(26), Width / 2f, 220, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var t0 = m.Time.AddHours(-24);
        var max = pts.Max(p => p.Gh) * 1.05;
        var min = Math.Max(0, pts.Min(p => p.Gh) * 0.9);
        if (max - min < 1) max = min + 1;
        float X(DateTime t) => x0 + (float)((t - t0).TotalHours / 24.0) * (x1 - x0);
        float Y(double gh) => y1 - (float)((gh - min) / (max - min)) * (y1 - y0);

        // Achsen und Hilfslinien
        ctx.DrawLine(Crisp, Ink, 2, new PointF(x0, y0), new PointF(x0, y1), new PointF(x1, y1));
        for (var k = 0; k <= 4; k++)
        {
            var v = min + (max - min) * k / 4;
            var y = Y(v);
            TextRight(ctx, max >= 1000 ? N(v / 1000, "0.00") : N(v, "0"), small, x0 - 8, y - 10, Ink);
            if (k > 0) for (var x = x0 + 4; x < x1; x += 12) ctx.Fill(Crisp, Ink, new RectangleF(x, y, 4, 1));
        }
        Text(ctx, max >= 1000 ? "TH/s" : "GH/s", small, 16, y0 - 26, Ink);
        for (var hAgo = 24; hAgo >= 0; hAgo -= 6)
        {
            var t = m.Time.AddHours(-hAgo);
            TextCenter(ctx, hAgo == 0 ? "jetzt" : t.ToString("HH:mm", De), small, X(t), y1 + 6, Ink);
        }

        // Linie (Lücken > 30 min nicht verbinden)
        var segment = new List<PointF>();
        void Flush()
        {
            if (segment.Count > 1) ctx.DrawLine(Crisp, Ink, 3, segment.ToArray());
            segment.Clear();
        }
        DateTime? last = null;
        foreach (var p in pts.Where(p => p.Time >= t0))
        {
            if (last is { } l && p.Time - l > TimeSpan.FromMinutes(30)) Flush();
            segment.Add(new PointF(X(p.Time), Y(p.Gh)));
            last = p.Time;
        }
        Flush();
        var avg = pts.Where(p => p.Time >= t0).Average(p => p.Gh);
        TextRight(ctx, $"Ø {FormatHash(avg)} · min {FormatHash(pts.Min(p => p.Gh))} · max {FormatHash(pts.Max(p => p.Gh))}",
            Regular.Value.CreateFont(19), x1, 72, Ink);
        DrawFooter(ctx, m);
    }

    private static void DrawSoak(IImageProcessingContext ctx, DisplayModel m)
    {
        PageHeader(ctx, m, "Dauertest");
        var list = m.Soaks!.Take(5).ToList();
        if (list.Count == 0)
        {
            TextCenter(ctx, "Kein Dauertest aktiv.", Regular.Value.CreateFont(26), Width / 2f, 220, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var rowH = Math.Min(92, 360 / list.Count);
        var bold = Bold.Value.CreateFont(rowH >= 70 ? 26 : 22);
        var font = Regular.Value.CreateFont(rowH >= 70 ? 20 : 18);
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var y = 76 + i * rowH;
            Text(ctx, Fit(s.Name, bold, 360), bold, 16, y, Ink);
            TextRight(ctx, $"{s.FrequencyMhz} MHz / {s.CoreVoltageMv} mV · {Left(s.Until, m.Time)}", font, Width - 16, y + 4, Ink);
            var total = (s.Until - s.Started).TotalSeconds;
            var done = total > 0 ? Math.Clamp((m.Time - s.Started).TotalSeconds / total, 0, 1) : 0;
            var barY = y + (rowH >= 70 ? 36 : 30);
            ctx.DrawPolygon(Crisp, Ink, 2, new PointF(16, barY), new PointF(Width - 16, barY), new PointF(Width - 16, barY + 16), new PointF(16, barY + 16));
            ctx.Fill(Crisp, Ink, new RectangleF(16, barY, (float)((Width - 32) * done), 16));
            if (rowH >= 70) Text(ctx, Fit(s.Status, font, Width - 32), font, 16, barY + 20, s.Status.Contains("fehl", StringComparison.OrdinalIgnoreCase) ? Red : Ink);
        }
        DrawFooter(ctx, m);
    }

    private static void DrawNetwork(IImageProcessingContext ctx, DisplayModel m)
    {
        var n = m.Network!;
        PageHeader(ctx, m, "Pool & Netzwerk");
        var small = Regular.Value.CreateFont(17);
        TextRight(ctx, "Shares ok", small, 560, 70, Ink);
        TextRight(ctx, "abgelehnt", small, 672, 70, Ink);
        TextRight(ctx, "Best Diff", small, Width - 16, 70, Ink);
        var rows = n.Pools.Take(6).ToList();
        var rowH = rows.Count == 0 ? 0 : Math.Min(54, 300 / rows.Count);
        var bold = Bold.Value.CreateFont(rowH >= 50 ? 24 : 20);
        var font = Regular.Value.CreateFont(rowH >= 50 ? 18 : 16);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var y = 94 + i * rowH;
            Text(ctx, Fit(r.Name, bold, 330), bold, 16, y, r.Online ? Ink : Red);
            if (r.Online) Text(ctx, Fit(r.Fallback ? "FALLBACK: " + r.Pool : r.Pool, font, 420), font, 16, y + (rowH >= 50 ? 28 : 22), r.Fallback ? Red : Ink);
            if (!r.Online)
            {
                TextRight(ctx, "offline", bold, Width - 16, y, Red);
                continue;
            }
            var sum = r.Accepted + r.Rejected;
            var rejectPct = sum > 0 ? r.Rejected / sum * 100 : 0;
            TextRight(ctx, N(r.Accepted, "N0"), bold, 560, y, Ink);
            TextRight(ctx, sum > 0 ? $"{N(rejectPct, "0.0")} %" : "–", bold, 672, y, rejectPct >= 5 ? Red : Ink);
            TextRight(ctx, r.BestDiff ?? "–", bold, Width - 16, y, Ink);
        }
        ctx.Fill(Crisp, Ink, new RectangleF(16, 400, Width - 32, 1));
        var net = n.Height is { } h
            ? $"Bitcoin-Netzwerk: Block {N(h, "N0")}" + (n.LastBlockPool is { } p ? $" · zuletzt von {p}" : "") +
              (n.LastBlockTime is { } lt ? $" · vor {N(Math.Max(0, (m.Time - lt).TotalMinutes), "0")} min" : "")
            : "Bitcoin-Netzwerk: keine Daten (mempool.space nicht erreichbar)";
        Text(ctx, Fit(net, Regular.Value.CreateFont(20), Width - 32), Regular.Value.CreateFont(20), 16, 410, Ink);
        DrawFooter(ctx, m);
    }

    // ---------- Sonderanzeigen ----------

    private static void DrawBlockFound(IImageProcessingContext ctx, DisplayModel m)
    {
        var b = m.BlockFound!;
        ctx.Fill(Crisp, Red, new RectangleF(0, 0, Width, 14));
        ctx.Fill(Crisp, Red, new RectangleF(0, Height - 14, Width, 14));
        Cube(ctx, 170, 215, 118);
        const float x = 330;
        Text(ctx, "BLOCK", Bold.Value, 72, x, 44, Red);
        Text(ctx, "GEFUNDEN!", Bold.Value, 72, x, 118, Red);
        Text(ctx, Fit(b.Miner, Bold.Value.CreateFont(40), Width - x - 16), Bold.Value.CreateFont(40), x, 214, Ink);
        Text(ctx, b.Time.ToString("dddd, dd.MM.yyyy · HH:mm 'Uhr'", De), Regular.Value, 24, x, 270, Ink);
        if (b.Height is { } h) Text(ctx, $"Block {N(h, "N0")} (Netzwerkstand beim Fund)", Regular.Value, 22, x, 306, Ink);
        Text(ctx, b.Count == 1 ? "Erster gefundener Block dieses Miners" : $"{b.Count} Blöcke insgesamt", Regular.Value, 22, x, 340, Ink);
        TextCenter(ctx, (b.Example ? "BEISPIEL · " : "") + "Taste 1: quittieren", Regular.Value.CreateFont(20), Width / 2f, 432, Ink);
    }

    private static void DrawAlarm(IImageProcessingContext ctx, DisplayModel m)
    {
        ctx.Fill(Crisp, Red, new RectangleF(0, 0, Width, 14));
        Warning(ctx, 130, 150, 100);
        Text(ctx, "ACHTUNG", Bold.Value, 64, 270, 60, Red);
        Text(ctx, $"{m.Alerts.Count} Warnung{(m.Alerts.Count == 1 ? "" : "en")} · {m.Time:HH:mm}", Regular.Value, 24, 272, 140, Ink);
        var font = Bold.Value.CreateFont(m.Alerts.Count <= 4 ? 30 : 24);
        var lineH = m.Alerts.Count <= 4 ? 44 : 34;
        var shown = m.Alerts.Take(m.Alerts.Count <= 4 ? 4 : 6).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            ctx.Fill(Crisp, Red, new RectangleF(30, 262 + i * lineH + 8, 10, 10));
            Text(ctx, Fit(shown[i], font, Width - 80), font, 54, 256 + i * lineH, Ink);
        }
        if (m.Alerts.Count > shown.Count) Text(ctx, $"+ {m.Alerts.Count - shown.Count} weitere", Regular.Value, 20, 54, 256 + shown.Count * lineH, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 442, Width - 32, 2));
        Text(ctx, "Taste 1: quittieren", Regular.Value, 21, 16, 450, Ink);
        TextRight(ctx, "Lüfter: " + m.FanMode, Regular.Value.CreateFont(21), Width - 16, 450, m.FanModeAlert ? Red : Ink);
    }

    private static void DrawBestDiff(IImageProcessingContext ctx, DisplayModel m)
    {
        var b = m.BestDiff!;
        Star(ctx, 160, 200, 110);
        const float x = 320;
        Text(ctx, "Neuer Rekord!", Bold.Value, 52, x, 40, Red);
        Text(ctx, Fit(b.Miner, Bold.Value.CreateFont(36), Width - x - 16), Bold.Value.CreateFont(36), x, 110, Ink);
        Text(ctx, $"Best Difficulty ({b.Coin})", Regular.Value, 22, x, 170, Ink);
        Text(ctx, b.Current, Bold.Value, 80, x, 196, Ink);
        Text(ctx, $"bisher {b.Previous} · {b.Time:dd.MM. HH:mm}", Regular.Value, 24, x, 300, Ink);
        DrawFooter(ctx, m);
    }

    // ---------- Symbole (nur Formen, pixelgenau ohne Kantenglättung) ----------

    /// <summary>Würfel für „Block“: Oberseite rot, links weiß, rechts schwarz.</summary>
    private static void Cube(IImageProcessingContext ctx, float cx, float cy, float s)
    {
        var dx = s * 0.87f;
        PointF top = new(cx, cy - s), right = new(cx + dx, cy - s / 2), mid = new(cx, cy), left = new(cx - dx, cy - s / 2);
        PointF rightLow = new(cx + dx, cy + s / 2), bottom = new(cx, cy + s), leftLow = new(cx - dx, cy + s / 2);
        ctx.FillPolygon(Crisp, Red, top, right, mid, left);
        ctx.FillPolygon(Crisp, Ink, mid, right, rightLow, bottom);
        ctx.DrawPolygon(Crisp, Ink, 5, top, right, mid, left);
        ctx.DrawPolygon(Crisp, Ink, 5, left, mid, bottom, leftLow);
        ctx.DrawPolygon(Crisp, Ink, 5, mid, right, rightLow, bottom);
    }

    /// <summary>Warndreieck: rot mit weißem Ausrufezeichen.</summary>
    private static void Warning(IImageProcessingContext ctx, float cx, float cy, float s)
    {
        PointF a = new(cx, cy - s), b = new(cx + s * 1.1f, cy + s * 0.9f), c = new(cx - s * 1.1f, cy + s * 0.9f);
        ctx.FillPolygon(Crisp, Red, a, b, c);
        ctx.DrawPolygon(Crisp, Ink, 5, a, b, c);
        ctx.Fill(Crisp, Color.White, new RectangleF(cx - 11, cy - s * 0.45f, 22, s * 0.8f));
        ctx.Fill(Crisp, Color.White, new RectangleF(cx - 11, cy + s * 0.48f, 22, 22));
    }

    /// <summary>Stern für Rekorde.</summary>
    private static void Star(IImageProcessingContext ctx, float cx, float cy, float r)
    {
        var pts = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? r : r * 0.42f;
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            pts[i] = new PointF(cx + (float)(radius * Math.Cos(angle)), cy + (float)(radius * Math.Sin(angle)));
        }
        ctx.FillPolygon(Crisp, Red, pts);
        ctx.DrawPolygon(Crisp, Ink, 5, pts);
    }
}
