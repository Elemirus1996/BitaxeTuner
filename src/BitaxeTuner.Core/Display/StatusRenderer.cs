using System.Globalization;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Display;

/// <summary>Ein Miner auf der Anzeige.</summary>
public sealed record DisplayMiner(
    string Name, bool Online, bool Maintenance, double? HashGh, double? ChipTemp, double? VrTemp,
    int? FanPercent, bool ChipHot, bool VrHot, bool FanStalled, string? Error);

/// <summary>Ein Temperaturfühler auf der Anzeige (Temp null = fehlt).</summary>
public sealed record DisplayTemp(string Name, double? Temp, bool Hot);

/// <summary>Alles, was auf die Anzeige kommt (vom Hub zusammengestellt).</summary>
public sealed record DisplayModel(
    string Title, DateTime Time, double TotalGh, double TotalW, double? EfficiencyJth, int Online, int Count,
    double? PriceCt, string FanMode, bool FanModeAlert, bool ServerPaused,
    IReadOnlyList<DisplayMiner> Miners, IReadOnlyList<string> Alerts, IReadOnlyList<DisplayTemp>? Temps = null)
{
    /// <summary>Was gezeigt wird; die Übersicht nutzt nur die Felder oben.</summary>
    public DisplayScene Scene { get; init; } = DisplayScene.Overview;
    /// <summary>„Seite 2/4“ im Kopf der Seiten (null bei Sonderanzeigen).</summary>
    public string? PageLabel { get; init; }
    public DisplayBlockFound? BlockFound { get; init; }
    public DisplayBestDiff? BestDiff { get; init; }
    public DisplayDaily? Daily { get; init; }
    public IReadOnlyList<DisplayPoint>? Chart { get; init; }

    /// <summary>0.9.11: Art des Verlaufsgraphen ("hashrate", "temp", "power", "efficiency"); der Wert steht in <see cref="DisplayPoint.Gh"/>.</summary>
    public string ChartKind { get; init; } = "hashrate";
    public IReadOnlyList<DisplaySoak>? Soaks { get; init; }
    public DisplayNetwork? Network { get; init; }
    /// <summary>Schwarz und Weiß tauschen (helle Schrift auf schwarzem Grund); Rot bleibt rot.</summary>
    public bool Inverted { get; init; }
    // 0.9.7 – neue Seiten
    public IReadOnlyList<DisplayCoin>? Coins { get; init; }
    public DisplayDifficulty? Difficulty { get; init; }
    /// <summary>Tagesbilanz mit Graph statt Minerliste.</summary>
    public DisplaySeries? DailySeries { get; init; }
    public DisplayMonthly? Monthly { get; init; }
    public DisplayPower? Power { get; init; }
    /// <summary>0.9.11: Untereinheit der Währung für den Strompreis in der Fußzeile (ct, ¢, p, Rp. …).</summary>
    public string PriceCent { get; init; } = "ct";
    public DisplayQr? Qr { get; init; }
    public IReadOnlyList<DisplaySensor>? Sensors { get; init; }
    /// <summary>0.9.11: Neuigkeiten (neueste zuerst).</summary>
    public IReadOnlyList<Network.NewsItem>? News { get; init; }
}

/// <summary>
/// Zeichnet den Status für das 7,5″-E-Paper (800 × 480, schwarz/weiß/rot) und liefert die beiden Ebenen,
/// wie sie der Pico unverändert an das Display schickt: Schwarz-Ebene (Bit 1 = weiß), Rot-Ebene (Bit 1 = rot),
/// je 1 Bit pro Pixel, zeilenweise, höchstes Bit links.
/// </summary>
public static partial class StatusRenderer
{
    public const int Width = 800;
    public const int Height = 480;
    private static CultureInfo De => L.Culture; // Sprache kann sich zur Laufzeit ändern (Server-Einstellung)
    private static readonly Lazy<FontFamily> Regular = new(() => Load("Regular"));
    private static readonly Lazy<FontFamily> Bold = new(() => Load("Bold"));

    private static readonly Color Ink = Color.Black;
    private static readonly Color Red = Color.FromRgb(220, 0, 0);
    private static readonly DrawingOptions Crisp = new() { GraphicsOptions = new GraphicsOptions { Antialias = false } };

    private static FontFamily Load(string style)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BitaxeTuner.Core.Display.{style}.ttf")
                      ?? throw new InvalidOperationException(L.T("Schrift fehlt in der Programmdatei."));
        return new FontCollection().Add(s);
    }

    /// <summary>Bild als RGB (Vorschau im Browser, Tests).</summary>
    public static Image<Rgb24> RenderImage(DisplayModel m)
    {
        var img = new Image<Rgb24>(Width, Height, Color.White);
        img.Mutate(ctx =>
        {
            switch (m.Scene)
            {
                case DisplayScene.BlockFound when m.BlockFound is not null: DrawBlockFound(ctx, m); break;
                case DisplayScene.Alarm: DrawAlarm(ctx, m); break;
                case DisplayScene.BestDiff when m.BestDiff is not null: DrawBestDiff(ctx, m); break;
                case DisplayScene.Daily when m.Daily is not null && m.DailySeries is not null: DrawDailyChart(ctx, m); break;
                case DisplayScene.Daily when m.Daily is not null: DrawDaily(ctx, m); break;
                case DisplayScene.Prices when m.Coins is { Count: > 0 }: DrawCoins(ctx, m); break;
                case DisplayScene.Monthly when m.Monthly is not null: DrawMonthly(ctx, m); break;
                case DisplayScene.Power when m.Power is not null: DrawPower(ctx, m); break;
                case DisplayScene.Qr when m.Qr is not null: DrawQr(ctx, m); break;
                case DisplayScene.Group: DrawOverview(ctx, m); break;
                case DisplayScene.Sensors: DrawSensors(ctx, m); break;
                case DisplayScene.News: DrawNews(ctx, m); break;
                case DisplayScene.Chart when m.Chart is not null: DrawChart(ctx, m); break;
                case DisplayScene.Soak when m.Soaks is not null: DrawSoak(ctx, m); break;
                case DisplayScene.Network when m.Network is not null: DrawNetwork(ctx, m); break;
                default: DrawOverview(ctx, m); break;
            }
        });
        if (m.Inverted) Invert(img);
        return img;
    }

    /// <summary>Schwarz ↔ Weiß tauschen, Rot unverändert lassen (gleiche Schwellen wie <see cref="ToPlanes"/>).</summary>
    private static void Invert(Image<Rgb24> img) => img.ProcessPixelRows(rows =>
    {
        for (var y = 0; y < Height; y++)
        {
            var row = rows.GetRowSpan(y);
            for (var x = 0; x < Width; x++)
            {
                var p = row[x];
                if (p.R > 150 && p.G < 100 && p.B < 100) continue;
                row[x] = p.R + p.G + p.B < 384 ? new Rgb24(255, 255, 255) : new Rgb24(0, 0, 0);
            }
        }
    });

    private static void DrawOverview(IImageProcessingContext ctx, DisplayModel m)
    {
        // Kopf: Titel links, Gesamtleistung rechts
        Text(ctx, m.Title, Bold.Value, 34, 16, 14, Ink);
        var total = FormatHash(m.TotalGh);
        TextRight(ctx, total, Bold.Value, 44, Width - 16, 4, Ink);
        var sub = $"{m.TotalW.ToString("0.0", De)} W" +
                  (m.EfficiencyJth is { } e ? L.T(" · {0} J/TH", e.ToString("0.0", De)) : "") +
                  L.T(" · {0}/{1} online", m.Online, m.Count);
        TextRight(ctx, sub, Regular.Value, 22, Width - 16, 56, m.Online < m.Count ? Red : Ink);
        // Temperaturfühler unter dem Titel, jeder für sich (zu warm oder fehlend in Rot)
        if (m.Temps is { Count: > 0 } temps)
        {
            var tf = Regular.Value.CreateFont(21);
            var tfHot = Bold.Value.CreateFont(21);
            var subWidth = TextMeasurer.MeasureSize(sub, new TextOptions(Regular.Value.CreateFont(22))).Width;
            var limit = Width - 16 - subWidth - 24;
            float x = 16;
            foreach (var t in temps)
            {
                var text = $"{t.Name} {(t.Temp is { } v ? v.ToString("0.0", De) + " °C" : "–")}";
                var font = t.Hot || t.Temp is null ? tfHot : tf;
                var w = TextMeasurer.MeasureSize(text, new TextOptions(font)).Width;
                if (x + w > limit) break;
                Text(ctx, text, font, x, 57, t.Hot || t.Temp is null ? Red : Ink);
                x += w + 18;
            }
        }
        ctx.Fill(Crisp, Ink, new RectangleF(16, 88, Width - 32, 3));

        // Spaltenköpfe
        const int colHash = 478, colChip = 584, colVr = 688, colFan = Width - 16;
        var small = Regular.Value.CreateFont(17);
        TextRight(ctx, L.T("Hashrate"), small, colHash, 96, Ink);
        TextRight(ctx, L.T("Chip"), small, colChip, 96, Ink);
        TextRight(ctx, L.T("VR"), small, colVr, 96, Ink);
        TextRight(ctx, L.T("Lüfter"), small, colFan, 96, Ink);

        // Miner-Zeilen
        var top = 120;
        var bottom = m.Alerts.Count > 0 ? 392 : 434;
        var shown = m.Miners.Take(Math.Max(1, (bottom - top) / 44)).ToList();
        var rowH = shown.Count == 0 ? 0 : Math.Min(64, (bottom - top) / shown.Count);
        var size = rowH >= 56 ? 30 : rowH >= 48 ? 26 : 22;
        for (var i = 0; i < shown.Count; i++)
        {
            var r = shown[i];
            var y = top + i * rowH + (rowH - size) / 2 - 4;
            var nameFont = Bold.Value.CreateFont(size);
            var valueFont = Regular.Value.CreateFont(size);
            Text(ctx, Fit(r.Name, nameFont, 300), nameFont, 16, y, r.Online ? Ink : Red);
            if (!r.Online)
            {
                var why = r.Maintenance ? L.T("Neustart / Tuning …") : "offline" + (r.Error is { Length: > 0 } err ? $" ({err})" : "");
                TextRight(ctx, Fit(why, valueFont, 480), valueFont, colFan, y, r.Maintenance ? Ink : Red);
            }
            else
            {
                TextRight(ctx, FormatHash(r.HashGh ?? 0), valueFont, colHash, y, Ink);
                TextRight(ctx, r.ChipTemp is { } c ? $"{c.ToString("0", De)} °C" : "–", valueFont, colChip, y, r.ChipHot ? Red : Ink);
                TextRight(ctx, r.VrTemp is { } v ? $"{v.ToString("0", De)} °C" : "–", valueFont, colVr, y, r.VrHot ? Red : Ink);
                TextRight(ctx, r.FanStalled ? L.T("steht!") : r.FanPercent is { } f ? $"{f} %" : "–", valueFont, colFan, y, r.FanStalled ? Red : Ink);
            }
            if (i < shown.Count - 1) ctx.Fill(Crisp, Ink, new RectangleF(16, top + (i + 1) * rowH - 1, Width - 32, 1));
        }
        if (m.Miners.Count > shown.Count)
            Text(ctx, L.T("+ {0} weitere", m.Miners.Count - shown.Count), small, 16, bottom - 22, Ink);
        if (m.Miners.Count == 0)
            Text(ctx, L.T("Noch keine Miner eingetragen."), Regular.Value.CreateFont(26), 16, top + 20, Ink);

        // Warnungen in Rot
        if (m.Alerts.Count > 0)
        {
            ctx.Fill(Crisp, Red, new RectangleF(16, 398, 6, 38));
            var alertFont = Bold.Value.CreateFont(20);
            Text(ctx, Fit(string.Join(" · ", m.Alerts), alertFont, Width - 48), alertFont, 30, 406, Red);
        }

        DrawFooter(ctx, m);
    }

    /// <summary>Fußzeile aller Seiten: Lüfter · Strompreis · Stand.</summary>
    private static void DrawFooter(IImageProcessingContext ctx, DisplayModel m)
    {
        ctx.Fill(Crisp, Ink, new RectangleF(16, 442, Width - 32, 2));
        var foot = Regular.Value.CreateFont(21);
        var fanText = L.T("Lüfter: ") + m.FanMode;
        var fanFont = m.FanModeAlert ? Bold.Value.CreateFont(21) : foot;
        Text(ctx, fanText, fanFont, 16, 450, m.FanModeAlert ? Red : Ink);
        var stand = (m.ServerPaused ? L.T("Server pausiert · ") : "") + L.T("Stand ") + L.Short(m.Time);
        TextRight(ctx, stand, foot, Width - 16, 450, m.ServerPaused ? Red : Ink);
        if (m.PriceCt is { } p)
        {
            // Strompreis zwischen Lüfter und Stand – nur, wenn er ohne Überlappung passt
            var price = L.T("Strom {0} {1}", p.ToString("0.0", De), m.PriceCent);
            var left = 16 + TextMeasurer.MeasureSize(fanText, new TextOptions(fanFont)).Width + 28;
            var right = Width - 16 - TextMeasurer.MeasureSize(stand, new TextOptions(foot)).Width - 28;
            var w = TextMeasurer.MeasureSize(price, new TextOptions(foot)).Width;
            if (right - left >= w) Text(ctx, price, foot, (left + right - w) / 2, 450, Ink);
        }
    }

    /// <summary>Die beiden Ebenen für das Display (2 × 48 000 Byte).</summary>
    public static byte[] Render(DisplayModel m)
    {
        using var img = RenderImage(m);
        return ToPlanes(img);
    }

    public static byte[] ToPlanes(Image<Rgb24> img)
    {
        const int plane = Width * Height / 8;
        var result = new byte[2 * plane];
        Array.Fill(result, (byte)0xFF, 0, plane); // Schwarz-Ebene: alles weiß
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < Width; x++)
                {
                    var p = row[x];
                    var index = y * (Width / 8) + x / 8;
                    var bit = (byte)(0x80 >> (x % 8));
                    if (p.R > 150 && p.G < 100 && p.B < 100) result[plane + index] |= bit;          // rot
                    else if (p.R + p.G + p.B < 384) result[index] &= (byte)~bit;                  // schwarz
                }
            }
        });
        return result;
    }

    public static string FormatHash(double gh) =>
        gh >= 1000 ? L.T("{0} TH/s", (gh / 1000).ToString("0.00", De)) : L.T("{0} GH/s", gh.ToString("0", De));

    private static string Fit(string text, Font font, float maxWidth)
    {
        if (TextMeasurer.MeasureSize(text, new TextOptions(font)).Width <= maxWidth) return text;
        while (text.Length > 1 && TextMeasurer.MeasureSize(text + "…", new TextOptions(font)).Width > maxWidth) text = text[..^1];
        return text + "…";
    }

    private static void Text(IImageProcessingContext ctx, string text, FontFamily family, float size, float x, float y, Color color) =>
        Text(ctx, text, family.CreateFont(size), x, y, color);

    private static void Text(IImageProcessingContext ctx, string text, Font font, float x, float y, Color color) =>
        ctx.DrawText(Crisp, new RichTextOptions(font) { Origin = new PointF(x, y) }, text, Brushes.Solid(color), null);

    private static void TextRight(IImageProcessingContext ctx, string text, FontFamily family, float size, float right, float y, Color color) =>
        TextRight(ctx, text, family.CreateFont(size), right, y, color);

    private static void TextRight(IImageProcessingContext ctx, string text, Font font, float right, float y, Color color) =>
        ctx.DrawText(Crisp, new RichTextOptions(font) { Origin = new PointF(right, y), HorizontalAlignment = HorizontalAlignment.Right }, text,
            Brushes.Solid(color), null);

    private static void TextCenter(IImageProcessingContext ctx, string text, Font font, float center, float y, Color color) =>
        ctx.DrawText(Crisp, new RichTextOptions(font) { Origin = new PointF(center, y), HorizontalAlignment = HorizontalAlignment.Center }, text,
            Brushes.Solid(color), null);
}
