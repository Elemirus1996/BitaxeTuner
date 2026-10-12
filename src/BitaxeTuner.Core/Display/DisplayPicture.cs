using BitaxeTuner.Core.I18n;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BitaxeTuner.Core.Display;

/// <summary>0.9.16, Seite „Eigenes Bild“: umgerechnetes Bild (RGB, 800 × <see cref="Height"/>) und optionale Bildunterschrift.</summary>
/// <param name="Rgb">Pixel als RGB-Bytes, nur Weiß, Schwarz und Rot; null = noch kein Bild hochgeladen.</param>
public sealed record DisplayPictureData(byte[]? Rgb, int Height, string Caption);

/// <summary>
/// 0.9.16: eigenes Bild für das E-Paper. Beim Hochladen wird es gedreht (EXIF), verkleinert und als PNG im Datenordner
/// gespeichert; für die Anzeige wird es auf 800 × 480 gebracht und auf Weiß/Schwarz/Rot umgerechnet (wahlweise gerastert).
/// </summary>
public static class DisplayPicture
{
    public const long MaxUploadBytes = 15L * 1024 * 1024;
    private const int MaxSourcePixels = 12000;
    private const int StoredMaxSide = 1600;

    private static readonly Rgb24 White = new(255, 255, 255);
    private static readonly Rgb24 Black = new(0, 0, 0);
    private static readonly Rgb24 RedInk = new(220, 0, 0);

    /// <summary>Hochgeladenes Bild prüfen und als PNG (längste Seite höchstens 1600 px) zurückgeben.</summary>
    public static byte[] Normalize(byte[] upload)
    {
        if (upload.Length == 0) throw new LocalizedException("Keine Bilddatei erhalten.");
        if (upload.Length > MaxUploadBytes) throw new LocalizedException("Das Bild ist zu groß (höchstens {0} MB).", MaxUploadBytes / 1048576);
        try
        {
            var info = Image.Identify(upload);
            if (info.Width > MaxSourcePixels || info.Height > MaxSourcePixels || info.Width < 8 || info.Height < 8)
                throw new LocalizedException("Das Bild ist zu groß (höchstens {0} MB).", MaxUploadBytes / 1048576);
            using var img = Image.Load<Rgb24>(upload);
            img.Mutate(x =>
            {
                x.AutoOrient();
                if (img.Width > StoredMaxSide || img.Height > StoredMaxSide)
                    x.Resize(new ResizeOptions { Size = new Size(StoredMaxSide, StoredMaxSide), Mode = ResizeMode.Max });
            });
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new LocalizedException("Keine lesbare Bilddatei (PNG, JPEG, GIF, BMP oder WebP).");
        }
    }

    /// <summary>
    /// Für die Anzeige umrechnen: auf 800 × <paramref name="height"/> bringen ("fit" = ganz zeigen mit weißem Rand,
    /// "fill" = Fläche füllen und Ränder abschneiden), dann auf Weiß/Schwarz(/Rot) – mit <paramref name="dither"/> gerastert
    /// (Floyd-Steinberg, gut für Fotos), sonst je Pixel die nächste Farbe (gut für Logos und Schrift).
    /// </summary>
    public static byte[] Convert(byte[] png, int height, string fit, bool dither, bool red)
    {
        using var img = Image.Load<Rgb24>(png);
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(StatusRenderer.Width, height),
            Mode = fit == "fill" ? ResizeMode.Crop : ResizeMode.Pad,
            PadColor = Color.White,
        }));
        var w = img.Width;
        var h = img.Height;
        // Pad liefert genau die Zielgröße; zur Sicherheit auf die Fläche der Anzeige kopieren
        var px = new float[w * h * 3];
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 3;
                    px[i] = row[x].R; px[i + 1] = row[x].G; px[i + 2] = row[x].B;
                }
            }
        });
        Rgb24[] palette = red ? [White, Black, RedInk] : [White, Black];
        var output = new byte[StatusRenderer.Width * height * 3];
        output.AsSpan().Fill(255);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                float r = Math.Clamp(px[i], 0, 255), g = Math.Clamp(px[i + 1], 0, 255), b = Math.Clamp(px[i + 2], 0, 255);
                var c = Nearest(palette, r, g, b);
                if (x < StatusRenderer.Width && y < height)
                {
                    var o = (y * StatusRenderer.Width + x) * 3;
                    output[o] = c.R; output[o + 1] = c.G; output[o + 2] = c.B;
                }
                if (!dither) continue;
                float er = r - c.R, eg = g - c.G, eb = b - c.B;
                Spread(px, w, h, x + 1, y, er, eg, eb, 7 / 16f);
                Spread(px, w, h, x - 1, y + 1, er, eg, eb, 3 / 16f);
                Spread(px, w, h, x, y + 1, er, eg, eb, 5 / 16f);
                Spread(px, w, h, x + 1, y + 1, er, eg, eb, 1 / 16f);
            }
        return output;
    }

    private static Rgb24 Nearest(Rgb24[] palette, float r, float g, float b)
    {
        var best = palette[0];
        var bestD = float.MaxValue;
        foreach (var p in palette)
        {
            // Helligkeit voll, Farbanteil halb gewichtet – sonst läge mittleres Grau näher an Rot als an Schwarz/Weiß
            float y = Luma(r, g, b), py = Luma(p.R, p.G, p.B);
            float cr = (r - y) - (p.R - py), cg = (g - y) - (p.G - py), cb = (b - y) - (p.B - py);
            var d = (y - py) * (y - py) + 0.5f * (cr * cr + cg * cg + cb * cb);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

    private static void Spread(float[] px, int w, int h, int x, int y, float er, float eg, float eb, float f)
    {
        if (x < 0 || x >= w || y >= h) return;
        var i = (y * w + x) * 3;
        px[i] += er * f; px[i + 1] += eg * f; px[i + 2] += eb * f;
    }
}
