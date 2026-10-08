using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Display;

/// <summary>Was das E-Paper zeigt: Seiten im Wechsel und Sonderanzeigen.</summary>
public enum DisplayScene { Overview, Daily, Chart, Soak, Network, BlockFound, Alarm, BestDiff, Prices, Monthly, Group, Power, Qr, Sensors }

public sealed record DisplayBlockFound(string Miner, DateTime Time, int Count, long? Height, bool Example = false);
public sealed record DisplayBestDiff(string Miner, string Coin, string Previous, string Current, DateTime Time);
public sealed record DisplayDailyRow(string Name, double? Gh, double? Jth, double? Temp, double? Availability);
public sealed record DisplayDaily(double Gh, double W, double Kwh, double Cost, string Currency, string? BestDiff, string? BestDiffMiner,
    IReadOnlyList<DisplayDailyRow> Rows);
public sealed record DisplayPoint(DateTime Time, double Gh, double Temp);
public sealed record DisplaySoak(string Name, string Status, DateTime Started, DateTime Until, int FrequencyMhz, int CoreVoltageMv);
/// <param name="PoolDifficulty">0.9.11: vom Pool verlangte Share-Difficulty.</param>
public sealed record DisplayPool(string Name, bool Online, string Pool, bool Fallback, double Accepted, double Rejected, string? BestDiff,
    double? PoolDifficulty = null);
/// <param name="BchHeight">0.9.11: letzter Block im Bitcoin-Cash-Netzwerk (Blockchair), sonst null.</param>
public sealed record DisplayNetwork(IReadOnlyList<DisplayPool> Pools, long? Height, string? LastBlockPool, DateTime? LastBlockTime,
    long? BchHeight = null, string? BchPool = null, DateTime? BchTime = null, bool ShowBch = false);

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
        if (t <= TimeSpan.Zero) return L.T("fertig");
        return t.TotalHours >= 2 ? L.T("noch {0} h", N(t.TotalHours, "0")) : L.T("noch {0} min", N(t.TotalMinutes, "0"));
    }

    // ---------- Seiten ----------

    private static void DrawDaily(IImageProcessingContext ctx, DisplayModel m)
    {
        var d = m.Daily!;
        PageHeader(ctx, m, L.T("Tagesbilanz · letzte 24 h"));
        var w = (Width - 32) / 4f;
        Tile(ctx, 16, 76, w, L.T("Ø Hashrate"), FormatHash(d.Gh));
        Tile(ctx, 16 + w, 76, w, L.T("Ø Leistung"), $"{N(d.W, "0.0")} W");
        Tile(ctx, 16 + 2 * w, 76, w, L.T("Effizienz"), d.Gh > 1 ? L.T("{0} J/TH", N(d.W / (d.Gh / 1000), "0.0")) : "–");
        Tile(ctx, 16 + 3 * w, 76, w, L.T("Stromkosten"), $"{N(d.Cost, "0.00")} {d.Currency}");
        if (d.BestDiff is { } bd)
            Text(ctx, Fit(L.T("Best Diff (Rekord): {0}", bd) + (d.BestDiffMiner is { } bm ? $" · {bm}" : "") + L.T(" · {0} kWh", N(d.Kwh, "0.00")),
                Regular.Value.CreateFont(21), Width - 32), Regular.Value.CreateFont(21), 16, 142, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 174, Width - 32, 1));

        var small = Regular.Value.CreateFont(17);
        TextRight(ctx, L.T("Ø Hashrate"), small, 470, 180, Ink);
        TextRight(ctx, "J/TH", small, 572, 180, Ink);
        TextRight(ctx, L.T("Temp."), small, 668, 180, Ink);
        TextRight(ctx, L.T("verfügbar"), small, Width - 16, 180, Ink);
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
                TextRight(ctx, L.T("keine Daten"), font, Width - 16, y, Red);
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
        var kind = m.ChartKind;
        PageHeader(ctx, m, kind switch
        {
            "temp" => L.T("ASIC-Temperatur · letzte 24 h"),
            "power" => L.T("Leistung · letzte 24 h"),
            "efficiency" => L.T("Effizienz · letzte 24 h"),
            _ => L.T("Hashrate · letzte 24 h"),
        });
        string Value(double v) => kind switch
        {
            "temp" => $"{N(v, "0.0")} °C",
            "power" => $"{N(v, "0.0")} W",
            "efficiency" => $"{N(v, "0.0")} J/TH",
            _ => FormatHash(v),
        };
        const float x0 = 96, x1 = Width - 20, y0 = 104, y1 = 400;
        var small = Regular.Value.CreateFont(17);
        if (pts.Count < 2)
        {
            TextCenter(ctx, L.T("Noch zu wenige Messwerte."), Regular.Value.CreateFont(26), Width / 2f, 220, Ink);
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
            TextRight(ctx, kind != "hashrate" ? N(v, "0.0") : max >= 1000 ? N(v / 1000, "0.00") : N(v, "0"), small, x0 - 8, y - 10, Ink);
            if (k > 0) for (var x = x0 + 4; x < x1; x += 12) ctx.Fill(Crisp, Ink, new RectangleF(x, y, 4, 1));
        }
        Text(ctx, kind switch { "temp" => "°C", "power" => "W", "efficiency" => "J/TH", _ => max >= 1000 ? "TH/s" : "GH/s" }, small, 16, y0 - 26, Ink);
        for (var hAgo = 24; hAgo >= 0; hAgo -= 6)
        {
            var t = m.Time.AddHours(-hAgo);
            TextCenter(ctx, hAgo == 0 ? L.T("jetzt") : t.ToString("HH:mm", De), small, X(t), y1 + 6, Ink);
        }

        // Linie (Lücken > 30 min nicht verbinden)
        var segment = new List<PointF>();
        void Flush()
        {
            if (segment.Count > 1) ctx.DrawLine(Crisp, Red, 3, segment.ToArray());   // 0.9.11: Graph in Rot
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
        TextRight(ctx, L.T("Ø {0} · min {1} · max {2}", Value(avg), Value(pts.Min(p => p.Gh)), Value(pts.Max(p => p.Gh))),
            Regular.Value.CreateFont(19), x1, 72, Ink);
        DrawFooter(ctx, m);
    }

    private static void DrawSoak(IImageProcessingContext ctx, DisplayModel m)
    {
        PageHeader(ctx, m, L.T("Dauertest"));
        var list = m.Soaks!.Take(5).ToList();
        if (list.Count == 0)
        {
            TextCenter(ctx, L.T("Kein Dauertest aktiv."), Regular.Value.CreateFont(26), Width / 2f, 220, Ink);
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
            TextRight(ctx, L.T("{0} MHz / {1} mV · {2}", s.FrequencyMhz, s.CoreVoltageMv, Left(s.Until, m.Time)), font, Width - 16, y + 4, Ink);
            var total = (s.Until - s.Started).TotalSeconds;
            var done = total > 0 ? Math.Clamp((m.Time - s.Started).TotalSeconds / total, 0, 1) : 0;
            var barY = y + (rowH >= 70 ? 36 : 30);
            ctx.DrawPolygon(Crisp, Ink, 2, new PointF(16, barY), new PointF(Width - 16, barY), new PointF(Width - 16, barY + 16), new PointF(16, barY + 16));
            ctx.Fill(Crisp, Red, new RectangleF(16, barY, (float)((Width - 32) * done), 16));   // 0.9.11: Fortschritt in Rot
            if (rowH >= 70) Text(ctx, Fit(s.Status, font, Width - 32), font, 16, barY + 20, s.Status.Contains("fehl", StringComparison.OrdinalIgnoreCase) ? Red : Ink);
        }
        DrawFooter(ctx, m);
    }

    /// <summary>Difficulty kurz: 512, 1,02k, 4,1M, 2,3G, 1,1T.</summary>
    private static string ShortDiff(double d) => d switch
    {
        >= 1e12 => N(d / 1e12, "0.##") + "T",
        >= 1e9 => N(d / 1e9, "0.##") + "G",
        >= 1e6 => N(d / 1e6, "0.##") + "M",
        >= 1e3 => N(d / 1e3, "0.##") + "k",
        _ => N(d, "0"),
    };

    private static void DrawNetwork(IImageProcessingContext ctx, DisplayModel m)
    {
        var n = m.Network!;
        PageHeader(ctx, m, L.T("Pool & Netzwerk"));
        var small = Regular.Value.CreateFont(17);
        TextRight(ctx, L.T("Shares ok"), small, 560, 70, Ink);
        TextRight(ctx, L.T("abgelehnt"), small, 672, 70, Ink);
        TextRight(ctx, L.T("Best Diff"), small, Width - 16, 70, Ink);
        var rows = n.Pools.Take(6).ToList();
        var rowH = rows.Count == 0 ? 0 : Math.Min(54, 300 / rows.Count);
        var bold = Bold.Value.CreateFont(rowH >= 50 ? 24 : 20);
        var font = Regular.Value.CreateFont(rowH >= 50 ? 18 : 16);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var y = 94 + i * rowH;
            Text(ctx, Fit(r.Name, bold, 330), bold, 16, y, r.Online ? Ink : Red);
            if (r.Online) Text(ctx, Fit(r.Fallback ? L.T("FALLBACK: ") + r.Pool : r.Pool, font, 420), font, 16, y + (rowH >= 50 ? 28 : 22), r.Fallback ? Red : Ink);
            // 0.9.11: vom Pool verlangte Share-Difficulty
            if (r.Online && r.PoolDifficulty is { } pd and > 0 && rowH >= 40)
                TextRight(ctx, L.T("Pool-Diff {0}", ShortDiff(pd)), font, Width - 16, y + (rowH >= 50 ? 28 : 22), Ink);
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
        var bchLine = n.ShowBch;
        var lineY = bchLine ? 388 : 400;
        ctx.Fill(Crisp, Ink, new RectangleF(16, lineY, Width - 32, 1));
        var net = n.Height is { } h
            ? L.T("Bitcoin-Netzwerk: Block {0}", N(h, "N0")) + (n.LastBlockPool is { } p ? L.T(" · zuletzt von {0}", p) : "") +
              (n.LastBlockTime is { } lt ? L.T(" · vor {0} min", N(Math.Max(0, (m.Time - lt).TotalMinutes), "0")) : "")
            : L.T("Bitcoin-Netzwerk: keine Daten (mempool.space nicht erreichbar)");
        Text(ctx, Fit(net, Regular.Value.CreateFont(bchLine ? 18 : 20), Width - 32), Regular.Value.CreateFont(bchLine ? 18 : 20), 16, lineY + 8, Ink);
        if (bchLine)
        {
            var bch = n.BchHeight is { } bh
                ? L.T("Bitcoin-Cash-Netzwerk: Block {0}", N(bh, "N0")) + (n.BchPool is { } bp ? L.T(" · zuletzt von {0}", bp) : "") +
                  (n.BchTime is { } bt ? L.T(" · vor {0} min", N(Math.Max(0, (m.Time - bt).TotalMinutes), "0")) : "")
                : L.T("Bitcoin-Cash-Netzwerk: keine Daten (Blockchair nicht erreichbar)");
            Text(ctx, Fit(bch, Regular.Value.CreateFont(18), Width - 32), Regular.Value.CreateFont(18), 16, lineY + 32, Ink);
        }
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
        Text(ctx, L.T("BLOCK"), Bold.Value, 72, x, 44, Red);
        Text(ctx, L.T("GEFUNDEN!"), Bold.Value, 72, x, 118, Red);
        Text(ctx, Fit(b.Miner, Bold.Value.CreateFont(40), Width - x - 16), Bold.Value.CreateFont(40), x, 214, Ink);
        Text(ctx, b.Time.ToString(L.T("dddd, dd.MM.yyyy · HH:mm 'Uhr'"), L.Culture), Regular.Value, 24, x, 270, Ink);
        if (b.Height is { } h) Text(ctx, L.T("Block {0} (Netzwerkstand beim Fund)", N(h, "N0")), Regular.Value, 22, x, 306, Ink);
        Text(ctx, b.Count == 1 ? L.T("Erster gefundener Block dieses Miners") : L.T("{0} Blöcke insgesamt", b.Count), Regular.Value, 22, x, 340, Ink);
        TextCenter(ctx, (b.Example ? L.T("BEISPIEL · ") : "") + L.T("Taste 1: quittieren"), Regular.Value.CreateFont(20), Width / 2f, 432, Ink);
    }

    private static void DrawAlarm(IImageProcessingContext ctx, DisplayModel m)
    {
        ctx.Fill(Crisp, Red, new RectangleF(0, 0, Width, 14));
        Warning(ctx, 130, 150, 100);
        Text(ctx, L.T("ACHTUNG"), Bold.Value, 64, 270, 60, Red);
        Text(ctx, (m.Alerts.Count == 1 ? L.T("1 Warnung · {0:HH:mm}", m.Time) : L.T("{0} Warnungen · {1:HH:mm}", m.Alerts.Count, m.Time)), Regular.Value, 24, 272, 140, Ink);
        var font = Bold.Value.CreateFont(m.Alerts.Count <= 4 ? 30 : 24);
        var lineH = m.Alerts.Count <= 4 ? 44 : 34;
        var shown = m.Alerts.Take(m.Alerts.Count <= 4 ? 4 : 6).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            ctx.Fill(Crisp, Red, new RectangleF(30, 262 + i * lineH + 8, 10, 10));
            Text(ctx, Fit(shown[i], font, Width - 80), font, 54, 256 + i * lineH, Ink);
        }
        if (m.Alerts.Count > shown.Count) Text(ctx, L.T("+ {0} weitere", m.Alerts.Count - shown.Count), Regular.Value, 20, 54, 256 + shown.Count * lineH, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 442, Width - 32, 2));
        Text(ctx, L.T("Taste 1: quittieren"), Regular.Value, 21, 16, 450, Ink);
        TextRight(ctx, L.T("Lüfter: ") + m.FanMode, Regular.Value.CreateFont(21), Width - 16, 450, m.FanModeAlert ? Red : Ink);
    }

    private static void DrawBestDiff(IImageProcessingContext ctx, DisplayModel m)
    {
        var b = m.BestDiff!;
        Star(ctx, 160, 200, 110);
        const float x = 320;
        Text(ctx, L.T("Neuer Rekord!"), Bold.Value, 52, x, 40, Red);
        Text(ctx, Fit(b.Miner, Bold.Value.CreateFont(36), Width - x - 16), Bold.Value.CreateFont(36), x, 110, Ink);
        Text(ctx, L.T("Best Difficulty ({0})", b.Coin), Regular.Value, 22, x, 170, Ink);
        Text(ctx, b.Current, Bold.Value, 80, x, 196, Ink);
        Text(ctx, L.T("bisher {0} · {1}", b.Previous, L.Short(b.Time)), Regular.Value, 24, x, 300, Ink);
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
