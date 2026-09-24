using System.Text.Json;

namespace BitaxeTuner.Core.Storage;

/// <summary>
/// Programmeinstellungen. Liegen immer unter <c>%LocalAppData%\BitaxeTuner\settings.json</c>;
/// der Datenordner für Ergebnisse und Profile ist frei wählbar (z. B. auf Laufwerk F:).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Über die Umgebungsvariable <c>BITAXETUNER_SETTINGS_DIR</c> umlenkbar (z. B. für Tests oder portable Nutzung).</summary>
    public static string SettingsDirectory =>
        Environment.GetEnvironmentVariable("BITAXETUNER_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitaxeTuner");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public string? DataDirectory { get; set; }
    public List<string> DeviceAddresses { get; set; } = [];
    public bool WarningAccepted { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public string EffectiveDataDirectory =>
        string.IsNullOrWhiteSpace(DataDirectory) ? SettingsDirectory : DataDirectory!;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
