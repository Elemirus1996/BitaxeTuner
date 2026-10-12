using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.16, Seite „Eigenes Bild“: je Anzeige ein Bild im Datenordner (display-images/main.png bzw. &lt;Id&gt;.png, in der
/// Sicherung enthalten). Umgerechnet wird bei Bedarf und zwischengespeichert, solange Datei und Einstellungen gleich bleiben.
/// </summary>
public sealed partial class MinerHub
{
    public const string PictureDirectory = "display-images";
    private (string Key, DisplayPictureData Data)? _pictureCache;

    /// <summary>Datei des Bildes einer Anzeige (null = erste Anzeige).</summary>
    public string PictureFile(string? displayId) =>
        Path.Combine(DataDirectory, PictureDirectory, displayId is null ? "main.png" : displayId + ".png");

    public bool HasDisplayPicture(string? displayId) => File.Exists(PictureFile(displayId));

    /// <summary>Bild hochladen (prüft und verkleinert); ersetzt ein vorhandenes Bild dieser Anzeige.</summary>
    public void SaveDisplayPicture(string? displayId, byte[] upload, string by)
    {
        var png = DisplayPicture.Normalize(upload);
        var file = PictureFile(displayId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, png);
        File.Move(tmp, file, overwrite: true);
        _pictureCache = null;
        LogEvent(null, EventCategories.Settings, L.T("Bild für die Anzeige hochgeladen ({0} KB) – {1}", png.Length / 1024, by));
    }

    public void DeleteDisplayPicture(string? displayId, string by)
    {
        var file = PictureFile(displayId);
        if (!File.Exists(file)) return;
        File.Delete(file);
        _pictureCache = null;
        LogEvent(null, EventCategories.Settings, L.T("Bild der Anzeige entfernt – {0}", by));
    }

    /// <summary>Bild der gerade zusammengestellten Anzeige, umgerechnet nach ihren Einstellungen.</summary>
    private DisplayPictureData BuildPicture()
    {
        var s = Ds;
        var caption = s.ImageCaption?.Trim() ?? "";
        var height = caption.Length > 0 ? StatusRenderer.Height - StatusRenderer.CaptionHeight : StatusRenderer.Height;
        var file = PictureFile(_extraContext?.Config.Id);
        DateTime stamp;
        try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
        catch (IOException) { stamp = DateTime.MinValue; }
        if (stamp == DateTime.MinValue) return new DisplayPictureData(null, height, caption);
        var key = $"{file}|{stamp.Ticks}|{height}|{s.ImageFit}|{s.ImageDither}|{s.ImageRed}";
        if (_pictureCache is { } c && c.Key == key) return c.Data with { Caption = caption };
        try
        {
            var data = new DisplayPictureData(DisplayPicture.Convert(File.ReadAllBytes(file), height, s.ImageFit, s.ImageDither, s.ImageRed), height, caption);
            _pictureCache = (key, data);
            return data;
        }
        catch (Exception ex) when (ex is IOException or SixLabors.ImageSharp.ImageFormatException)
        {
            return new DisplayPictureData(null, height, caption);
        }
    }
}
