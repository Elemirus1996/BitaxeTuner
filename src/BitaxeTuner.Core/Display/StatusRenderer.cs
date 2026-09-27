using System.Globalization;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BitaxeTuner.Core.Display;

/// <summary>Ein Miner auf der Anzeige.</summary>
public sealed record DisplayMiner(
    string Name, bool Online, bool Maintenance, double? HashGh, double? ChipTemp, double? VrTemp,
    int? FanPercent, bool ChipHot, bool VrHot, bool FanStalled, string? Error);

/// <summary>Alles, was auf die Anzeige kommt (vom Hub zusammengestellt).</summary>
public sealed record DisplayModel(
    string Title, DateTime Time, double TotalGh, double TotalW, double? EfficiencyJth, int Online, int Count,
    double? PriceCt, string FanMode, bool FanModeAlert, bool ServerPaused,
    IReadOnlyList<DisplayMiner> Miners, IReadOnlyList<string> Alerts, double? CaseTemp = null, bool CaseHot = false);

/// <summary>
/// Zeichnet den Status für das 7,5″-E-Paper (800 × 480, schwarz/weiß/rot) und liefert die beiden Ebenen,
/// wie sie der Pico unverändert an das Display schickt: Schwarz-Ebene (Bit 1 = weiß), Rot-Ebene (Bit 1 = rot),
/// je 1 Bit pro Pixel, zeilenweise, höchstes Bit links.
/// </summary>
public static class StatusRenderer
{
    public const int Width = 800;
    public const int Height = 480;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Lazy<FontFamily> Regular = new(() => Load("Regular"));
    private static readonly Lazy<FontFamily> Bold = new(() => Load("Bold"));

    private static readonly Color Ink = Color.Black;
    private static readonly Color Red = Color.FromRgb(220, 0, 0);
    private static readonly DrawingOptions Crisp = new() { GraphicsOptions = new GraphicsOptions { Antialias = false } };

    private static FontFamily Load(string style)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BitaxeTuner.Core.Display.{style}.ttf")
                      ?? throw new InvalidOperationException("Schrift fehlt in der Programmdatei.");
        return new FontCollection().Add(s);
    }

    /// <summary>Bild als RGB (Vorschau im Browser, Tests).</summary>
    public static Image<Rgb24> RenderImage(DisplayModel m)
    {
        var img = new Image<Rgb24>(Width, Height, Color.White);
        img.Mutate(ctx =>
        {
            // Kopf: Titel links, Gesamtleistung rechts
            Text(ctx, m.Title, Bold.Value, 34, 16, 14, Ink);
            var total = FormatHash(m.TotalGh);
            TextRight(ctx, total, Bold.Value, 44, Width - 16, 4, Ink);
            var sub = $"{m.TotalW.ToString("0.0", De)} W" +
                      (m.EfficiencyJth is { } e ? $" · {e.ToString("0.0", De)} J/TH" : "") +
                      $" · {m.Online}/{m.Count} online";
            TextRight(ctx, sub, Regular.Value, 22, Width - 16, 56, m.Online < m.Count ? Red : Ink);
            ctx.Fill(Crisp, Ink, new RectangleF(16, 88, Width - 32, 3));

            // Spaltenköpfe
            const int colHash = 478, colChip = 584, colVr = 688, colFan = Width - 16;
            var small = Regular.Value.CreateFont(17);
            TextRight(ctx, "Hashrate", small, colHash, 96, Ink);
            TextRight(ctx, "Chip", small, colChip, 96, Ink);
            TextRight(ctx, "VR", small, colVr, 96, Ink);
            TextRight(ctx, "Lüfter", small, colFan, 96, Ink);

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
                    var why = r.Maintenance ? "Neustart / Tuning …" : "offline" + (r.Error is { Length: > 0 } err ? $" ({err})" : "");
                    TextRight(ctx, Fit(why, valueFont, 480), valueFont, colFan, y, r.Maintenance ? Ink : Red);
                }
                else
                {
                    TextRight(ctx, FormatHash(r.HashGh ?? 0), valueFont, colHash, y, Ink);
                    TextRight(ctx, r.ChipTemp is { } c ? $"{c.ToString("0", De)} °C" : "–", valueFont, colChip, y, r.ChipHot ? Red : Ink);
                    TextRight(ctx, r.VrTemp is { } v ? $"{v.ToString("0", De)} °C" : "–", valueFont, colVr, y, r.VrHot ? Red : Ink);
                    TextRight(ctx, r.FanStalled ? "steht!" : r.FanPercent is { } f ? $"{f} %" : "–", valueFont, colFan, y, r.FanStalled ? Red : Ink);
                }
                if (i < shown.Count - 1) ctx.Fill(Crisp, Ink, new RectangleF(16, top + (i + 1) * rowH - 1, Width - 32, 1));
            }
            if (m.Miners.Count > shown.Count)
                Text(ctx, $"+ {m.Miners.Count - shown.Count} weitere", small, 16, bottom - 22, Ink);
            if (m.Miners.Count == 0)
                Text(ctx, "Noch keine Miner eingetragen.", Regular.Value.CreateFont(26), 16, top + 20, Ink);

            // Warnungen in Rot
            if (m.Alerts.Count > 0)
            {
                ctx.Fill(Crisp, Red, new RectangleF(16, 398, 6, 38));
                var alertFont = Bold.Value.CreateFont(20);
                Text(ctx, Fit(string.Join(" · ", m.Alerts), alertFont, Width - 48), alertFont, 30, 406, Red);
            }

            // Fußzeile
            ctx.Fill(Crisp, Ink, new RectangleF(16, 442, Width - 32, 2));
            var foot = Regular.Value.CreateFont(21);
            Text(ctx, "Lüfter: " + m.FanMode, m.FanModeAlert ? Bold.Value.CreateFont(21) : foot, 16, 450, m.FanModeAlert ? Red : Ink);
            var middle = string.Join(" · ", new[]
            {
                m.CaseTemp is { } ct ? $"Gehäuse {ct.ToString("0.0", De)} °C" : null,
                m.PriceCt is { } p ? $"Strom {p.ToString("0.0", De)} ct" : null,
            }.Where(x => x is not null));
            if (middle.Length > 0) TextCenter(ctx, middle, m.CaseHot ? Bold.Value.CreateFont(21) : foot, Width / 2 + 30, 450, m.CaseHot ? Red : Ink);
            TextRight(ctx, (m.ServerPaused ? "Server pausiert · " : "") + "Stand " + m.Time.ToString("dd.MM. HH:mm", De), foot, Width - 16, 450,
                m.ServerPaused ? Red : Ink);
        });
        return img;
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
        gh >= 1000 ? $"{(gh / 1000).ToString("0.00", De)} TH/s" : $"{gh.ToString("0", De)} GH/s";

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
