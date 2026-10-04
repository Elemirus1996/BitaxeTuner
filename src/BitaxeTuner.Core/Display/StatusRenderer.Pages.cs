using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Display;

/// <summary>Kurs eines Coins: aktueller EUR-Kurs, Änderung über 24 h in %, Verlauf.</summary>
public sealed record DisplayCoin(string Symbol, string Name, double? Eur, double? Change24h, IReadOnlyList<DisplayValue> Points);

/// <summary>Nächste Difficulty-Anpassung (Bitcoin).</summary>
public sealed record DisplayDifficulty(double ProgressPercent, double ExpectedChangePercent, int RemainingBlocks, DateTime? Eta);

/// <summary>Messwert mit Zeit (Graphen der neuen Seiten).</summary>
public sealed record DisplayValue(DateTime Time, double Value);

/// <summary>Graph der Tagesbilanz: Bezeichnung, Einheit, Werte der letzten 24 h.</summary>
public sealed record DisplaySeries(string Label, string Unit, string Format, IReadOnlyList<DisplayValue> Points);

/// <summary>Monatsbilanz mit Balken je Tag.</summary>
public sealed record DisplayMonthly(DateTime Month, bool Partial, double Kwh, double Cost, string Currency, double IncomeEur, int IncomeMissing,
    double? AvgGh, string BarLabel, string BarFormat, IReadOnlyList<DisplayValue> Bars);

/// <summary>Strompreise der kommenden Stunden (ct/kWh) und das günstigste 3-Stunden-Fenster.</summary>
public sealed record DisplayPower(double? NowCt, IReadOnlyList<DisplayValue> Hours, DateTime? CheapFrom, double? CheapAvg, string Source);

/// <summary>QR-Code (Module zeilenweise, true = schwarz) und die Adresse darin.</summary>
public sealed record DisplayQr(string Url, IReadOnlyList<bool[]> Modules);

/// <summary>Temperaturfühler mit Warnschwelle; Temp null = meldet gerade nichts.</summary>
public sealed record DisplaySensor(string Name, double? Temp, double Warn, bool Hot);

public static partial class StatusRenderer
{
    private static string Eur(double v) => v >= 1000 ? $"{N(v, "N0")} €" : $"{N(v, "N2")} €";

    private static string Signed(double v, string format) => (v > 0 ? "+" : "") + N(v, format);

    /// <summary>Liniengraph mit Achsen, 4 Hilfslinien und Zeitachse (Lücken über <paramref name="gap"/> nicht verbunden).</summary>
    private static void LineChart(IImageProcessingContext ctx, IReadOnlyList<DisplayValue> points, RectangleF area, DateTime from, DateTime to,
        string format, TimeSpan gap, int timeLabels = 4, bool zeroBased = false)
    {
        var small = Regular.Value.CreateFont(17);
        var pts = points.Where(p => p.Time >= from && p.Time <= to).OrderBy(p => p.Time).ToList();
        if (pts.Count < 2)
        {
            TextCenter(ctx, L.T("Noch zu wenige Messwerte."), Regular.Value.CreateFont(24), area.Left + area.Width / 2, area.Top + area.Height / 2 - 14, Ink);
            return;
        }
        var max = pts.Max(p => p.Value);
        var min = zeroBased ? 0 : pts.Min(p => p.Value);
        var pad = (max - min) * 0.08;
        if (pad <= 0) pad = Math.Max(Math.Abs(max) * 0.05, 1);
        max += pad;
        if (!zeroBased) min -= pad;
        float X(DateTime t) => area.Left + (float)((t - from).TotalSeconds / (to - from).TotalSeconds) * area.Width;
        float Y(double v) => area.Bottom - (float)((v - min) / (max - min)) * area.Height;

        ctx.DrawLine(Crisp, Ink, 2, new PointF(area.Left, area.Top), new PointF(area.Left, area.Bottom), new PointF(area.Right, area.Bottom));
        for (var k = 0; k <= 3; k++)
        {
            var v = min + (max - min) * k / 3;
            var y = Y(v);
            TextRight(ctx, N(v, format), small, area.Left - 6, y - 10, Ink);
            if (k > 0) for (var x = area.Left + 4; x < area.Right; x += 12) ctx.Fill(Crisp, Ink, new RectangleF(x, y, 4, 1));
        }
        for (var i = 0; i <= timeLabels; i++)
        {
            var t = from + (to - from) * i / timeLabels;
            TextCenter(ctx, t.ToString("HH:mm", De), small, X(t), area.Bottom + 4, Ink);
        }
        var segment = new List<PointF>();
        void Flush()
        {
            if (segment.Count > 1) ctx.DrawLine(Crisp, Ink, 3, segment.ToArray());
            segment.Clear();
        }
        DateTime? last = null;
        foreach (var p in pts)
        {
            if (last is { } l && p.Time - l > gap) Flush();
            segment.Add(new PointF(X(p.Time), Y(p.Value)));
            last = p.Time;
        }
        Flush();
    }

    // ---------- Kurs ----------

    private static void DrawCoins(IImageProcessingContext ctx, DisplayModel m)
    {
        var coins = m.Coins!;
        PageHeader(ctx, m, coins.Count == 1 ? L.T("Kurs {0}", coins[0].Symbol) : L.T("Kurse"));
        var chartBottom = m.Difficulty is null ? 410f : 350f;
        if (coins.Count == 1)
        {
            var c = coins[0];
            Text(ctx, c.Name, Regular.Value, 22, 16, 76, Ink);
            Text(ctx, c.Eur is { } e ? Eur(e) : "–", Bold.Value, 64, 16, 100, Ink);
            if (c.Change24h is { } ch)
                TextRight(ctx, L.T("{0} % in 24 h", Signed(ch, "0.0")), Bold.Value, 32, Width - 16, 122, ch < 0 ? Red : Ink);
            LineChart(ctx, c.Points, new RectangleF(96, 196, Width - 116, chartBottom - 196 - 24), m.Time.AddHours(-24), m.Time, c.Eur >= 1000 ? "N0" : "N0", TimeSpan.FromHours(2));
        }
        else
        {
            var colW = (Width - 48) / 2f;
            for (var i = 0; i < Math.Min(2, coins.Count); i++)
            {
                var c = coins[i];
                var x = 16 + i * (colW + 16);
                Text(ctx, c.Name, Regular.Value, 22, x, 76, Ink);
                Text(ctx, Fit(c.Eur is { } e ? Eur(e) : "–", Bold.Value.CreateFont(46), colW), Bold.Value.CreateFont(46), x, 102, Ink);
                if (c.Change24h is { } ch) Text(ctx, L.T("{0} % in 24 h", Signed(ch, "0.0")), Bold.Value, 24, x, 160, ch < 0 ? Red : Ink);
                LineChart(ctx, c.Points, new RectangleF(x + 70, 204, colW - 74, chartBottom - 204 - 24), m.Time.AddHours(-24), m.Time,
                    c.Eur is >= 1000 ? "N0" : "N0", TimeSpan.FromHours(2), timeLabels: 2);
            }
        }
        if (m.Difficulty is { } d)
        {
            ctx.Fill(Crisp, Ink, new RectangleF(16, 360, Width - 32, 1));
            var eta = d.Eta is { } t ? L.T(" · ca. {0}", t.ToString(L.T("ddd dd.MM. HH:mm"), De)) : "";
            Text(ctx, Fit(L.T("Difficulty-Anpassung in {0} Blöcken{1} · erwartet {2} %", N(d.RemainingBlocks, "N0"), eta, Signed(d.ExpectedChangePercent, "0.0")),
                Regular.Value.CreateFont(21), Width - 32), Regular.Value.CreateFont(21), 16, 368, Ink);
            ctx.DrawPolygon(Crisp, Ink, 2, new PointF(16, 404), new PointF(Width - 16, 404), new PointF(Width - 16, 420), new PointF(16, 404 + 16));
            ctx.Fill(Crisp, Ink, new RectangleF(16, 404, (float)((Width - 32) * Math.Clamp(d.ProgressPercent / 100, 0, 1)), 16));
        }
        DrawFooter(ctx, m);
    }

    // ---------- Tagesbilanz mit Graph ----------

    /// <summary>Kopfzeile der Tagesbilanz bleibt, statt der Minerliste ein 24-h-Graph.</summary>
    private static void DrawDailyChart(IImageProcessingContext ctx, DisplayModel m)
    {
        var d = m.Daily!;
        var s = m.DailySeries!;
        PageHeader(ctx, m, L.T("Tagesbilanz · letzte 24 h"));
        var w = (Width - 32) / 4f;
        Tile(ctx, 16, 76, w, L.T("Ø Hashrate"), FormatHash(d.Gh));
        Tile(ctx, 16 + w, 76, w, L.T("Ø Leistung"), $"{N(d.W, "0.0")} W");
        Tile(ctx, 16 + 2 * w, 76, w, L.T("Effizienz"), d.Gh > 1 ? L.T("{0} J/TH", N(d.W / (d.Gh / 1000), "0.0")) : "–");
        Tile(ctx, 16 + 3 * w, 76, w, L.T("Stromkosten"), $"{N(d.Cost, "0.00")} {d.Currency}");
        ctx.Fill(Crisp, Ink, new RectangleF(16, 146, Width - 32, 1));
        Text(ctx, $"{s.Label} ({s.Unit})", Regular.Value, 19, 16, 152, Ink);
        LineChart(ctx, s.Points, new RectangleF(86, 186, Width - 106, 226), m.Time.AddHours(-24), m.Time, s.Format, TimeSpan.FromMinutes(30));
        DrawFooter(ctx, m);
    }

    // ---------- Monatsbilanz ----------

    private static void DrawMonthly(IImageProcessingContext ctx, DisplayModel m)
    {
        var mo = m.Monthly!;
        PageHeader(ctx, m, L.T("Monatsbilanz · {0}", mo.Month.ToString("MMMM yyyy", De)) + (mo.Partial ? L.T(" (läuft)") : ""));
        var w = (Width - 32) / 4f;
        var diff = mo.IncomeEur - mo.Cost;
        Tile(ctx, 16, 76, w, L.T("Ertrag"), $"{N(mo.IncomeEur, "0.00")} €");
        Tile(ctx, 16 + w, 76, w, L.T("Strom"), $"{N(mo.Kwh, "0.0")} kWh");
        Tile(ctx, 16 + 2 * w, 76, w, L.T("Stromkosten"), $"{N(mo.Cost, "0.00")} {mo.Currency}");
        // Audit N-F4: Ertrag ist immer in €, Stromkosten in der eingestellten Währung – nur gleiche Währungen verrechnen
        if (mo.Currency is "€" or "EUR" or "Euro")
            Tile(ctx, 16 + 3 * w, 76, w, L.T("Differenz"), Signed(diff, "0.00"), red: diff < 0);
        else
            Tile(ctx, 16 + 3 * w, 76, w, L.T("Differenz"), L.T("andere Währung"));
        var note = (mo.AvgGh is { } gh ? L.T("Ø Hashrate {0}", FormatHash(gh)) : "") +
                   (mo.IncomeMissing > 0 ? L.T(" · {0} Zuflüsse ohne Kurs", mo.IncomeMissing) : "");
        if (note.Length > 0) Text(ctx, Fit(note.TrimStart(' ', '·'), Regular.Value.CreateFont(19), Width - 32), Regular.Value.CreateFont(19), 16, 142, Ink);
        ctx.Fill(Crisp, Ink, new RectangleF(16, 170, Width - 32, 1));
        Text(ctx, mo.BarLabel, Regular.Value, 19, 16, 176, Ink);

        var days = DateTime.DaysInMonth(mo.Month.Year, mo.Month.Month);
        var area = new RectangleF(70, 210, Width - 86, 196);
        var max = mo.Bars.Count > 0 ? mo.Bars.Max(b => b.Value) : 0;
        var small = Regular.Value.CreateFont(16);
        ctx.DrawLine(Crisp, Ink, 2, new PointF(area.Left, area.Top), new PointF(area.Left, area.Bottom), new PointF(area.Right, area.Bottom));
        if (max <= 0)
        {
            TextCenter(ctx, L.T("Noch keine Werte in diesem Monat."), Regular.Value.CreateFont(22), area.Left + area.Width / 2, area.Top + 70, Ink);
        }
        else
        {
            TextRight(ctx, N(max, mo.BarFormat), small, area.Left - 6, area.Top - 8, Ink);
            TextRight(ctx, "0", small, area.Left - 6, area.Bottom - 10, Ink);
            var slot = area.Width / days;
            foreach (var b in mo.Bars)
            {
                var day = b.Time.Day;
                var h = (float)(b.Value / max) * area.Height;
                var x = area.Left + (day - 1) * slot + slot * 0.15f;
                var today = mo.Partial && day == m.Time.Day;
                ctx.Fill(Crisp, today ? Red : Ink, new RectangleF(x, area.Bottom - h, slot * 0.7f, h));
            }
            foreach (var day in new[] { 1, 5, 10, 15, 20, 25, days })
                TextCenter(ctx, day.ToString(De), small, area.Left + (day - 0.5f) * slot, area.Bottom + 4, Ink);
        }
        DrawFooter(ctx, m);
    }

    // ---------- Strompreis-Ampel ----------

    private static void DrawPower(IImageProcessingContext ctx, DisplayModel m)
    {
        var p = m.Power!;
        PageHeader(ctx, m, L.T("Strompreis · nächste Stunden"));
        if (p.Hours.Count == 0)
        {
            TextCenter(ctx, L.T("Kein dynamischer Strompreis eingerichtet."), Regular.Value.CreateFont(26), Width / 2f, 200, Ink);
            TextCenter(ctx, L.T("Einstellungen → Strompreis: aWATTar oder Tibber"), Regular.Value.CreateFont(20), Width / 2f, 240, Ink);
            DrawFooter(ctx, m);
            return;
        }
        Text(ctx, L.T("jetzt"), Regular.Value, 20, 16, 74, Ink);
        Text(ctx, p.NowCt is { } now ? L.T("{0} ct/kWh", N(now, "0.0")) : "–", Bold.Value, 44, 16, 96, Ink);
        if (p.CheapFrom is { } cf && p.CheapAvg is { } ca)
        {
            TextRight(ctx, L.T("günstigste 3 Stunden"), Regular.Value, 20, Width - 16, 74, Ink);
            TextRight(ctx, L.T("{0}–{1} Uhr · Ø {2} ct", cf.ToString("HH:mm", De), cf.AddHours(3).ToString("HH:mm", De), N(ca, "0.0")),
                Bold.Value, 30, Width - 16, 104, Ink);
        }
        var hours = p.Hours.OrderBy(h => h.Time).Take(24).ToList();
        var sorted = hours.Select(h => h.Value).Order().ToList();
        var low = sorted[sorted.Count / 3];
        var high = sorted[Math.Min(sorted.Count - 1, sorted.Count * 2 / 3)];
        var area = new RectangleF(70, 170, Width - 86, 230);
        var max = Math.Max(0.1, sorted[^1]);
        var min = Math.Min(0, sorted[0]);
        var small = Regular.Value.CreateFont(16);
        float Y(double v) => area.Bottom - (float)((v - min) / (max - min)) * area.Height;
        ctx.DrawLine(Crisp, Ink, 2, new PointF(area.Left, area.Top), new PointF(area.Left, area.Bottom), new PointF(area.Right, area.Bottom));
        TextRight(ctx, N(max, "0"), small, area.Left - 6, area.Top - 8, Ink);
        TextRight(ctx, N(min, "0"), small, area.Left - 6, Y(min) - 10, Ink);
        Text(ctx, "ct", small, 16, area.Top - 30, Ink);
        var slot = area.Width / hours.Count;
        for (var i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            var x = area.Left + i * slot + slot * 0.12f;
            var top = Y(Math.Max(h.Value, min));
            var rect = new RectangleF(x, top, slot * 0.76f, Math.Max(1, Y(min) - top));
            if (h.Value >= high) ctx.Fill(Crisp, Red, rect);                       // teuer
            else if (h.Value <= low) ctx.Fill(Crisp, Ink, rect);                   // günstig
            else ctx.Draw(Crisp, Ink, 2, rect);                                    // mittel
            if (i % 3 == 0) TextCenter(ctx, h.Time.ToString("HH", De), small, x + slot * 0.38f, area.Bottom + 4, Ink);
        }
        Text(ctx, Fit(L.T("schwarz = günstig · rot = teuer · {0}", p.Source), small, Width - 32), small, 16, 428, Ink);
        DrawFooter(ctx, m);
    }

    // ---------- QR-Code ----------

    private static void DrawQr(IImageProcessingContext ctx, DisplayModel m)
    {
        var q = m.Qr!;
        if (q.Modules.Count == 0)
        {
            PageHeader(ctx, m, L.T("QR-Code"));
            TextCenter(ctx, L.T("Adresse dieses Servers unbekannt."), Regular.Value.CreateFont(26), Width / 2f, 190, Ink);
            TextCenter(ctx, L.T("Unter Lüfter & Anzeige eine QR-Code-Adresse eintragen."), Regular.Value.CreateFont(20), Width / 2f, 232, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var n = q.Modules.Count;
        const float size = 372;
        var cell = MathF.Floor(size / n);
        var x0 = 28f;
        var y0 = (Height - n * cell) / 2f - 6;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < q.Modules[y].Length; x++)
                if (q.Modules[y][x]) ctx.Fill(Crisp, Ink, new RectangleF(x0 + x * cell, y0 + y * cell, cell, cell));
        const float tx = 430;
        Text(ctx, L.T("Im Browser öffnen"), Bold.Value, 34, tx, 90, Ink);
        var font = Regular.Value.CreateFont(22);
        var url = q.Url;
        var line = 0;
        while (url.Length > 0 && line < 4)
        {
            var take = url.Length;
            while (take > 1 && TextMeasurer.MeasureSize(url[..take], new TextOptions(font)).Width > Width - tx - 16) take--;
            Text(ctx, url[..take], font, tx, 150 + line * 30, Ink);
            url = url[take..];
            line++;
        }
        Text(ctx, L.T("Mit der Handy-Kamera scannen."), Regular.Value, 20, tx, 290, Ink);
        Text(ctx, L.T("Nur im Heimnetz erreichbar."), Regular.Value, 20, tx, 318, Ink);
        DrawFooter(ctx, m);
    }

    // ---------- Temperaturfühler ----------

    private static void DrawSensors(IImageProcessingContext ctx, DisplayModel m)
    {
        PageHeader(ctx, m, L.T("Temperaturfühler"));
        var list = (m.Sensors ?? []).Take(8).ToList();
        if (list.Count == 0)
        {
            TextCenter(ctx, L.T("Kein Temperaturfühler eingetragen."), Regular.Value.CreateFont(26), Width / 2f, 200, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var rowH = Math.Min(92, 360 / list.Count);
        var big = Bold.Value.CreateFont(rowH >= 70 ? 44 : rowH >= 50 ? 32 : 24);
        var name = Bold.Value.CreateFont(rowH >= 70 ? 30 : 22);
        var small = Regular.Value.CreateFont(rowH >= 50 ? 19 : 16);
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var y = 76 + i * rowH;
            Text(ctx, Fit(s.Name, name, 420), name, 16, y, s.Hot ? Red : Ink);
            if (rowH >= 50) Text(ctx, L.T("Grenze {0} °C", N(s.Warn, "0.#")), small, 16, y + (rowH >= 70 ? 38 : 28), Ink);
            TextRight(ctx, s.Temp is { } t ? $"{N(t, "0.0")} °C" : L.T("fehlt"), big, Width - 16, y, s.Hot || s.Temp is null ? Red : Ink);
            if (i < list.Count - 1) ctx.Fill(Crisp, Ink, new RectangleF(16, y + rowH - 6, Width - 32, 1));
        }
        DrawFooter(ctx, m);
    }
}
