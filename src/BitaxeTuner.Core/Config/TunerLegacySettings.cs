using System.Text.Json;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Einstellungen des eigenständigen BitaxeTuner (bis v0.1.0) unter %LocalAppData%\BitaxeTuner\settings.json.
/// Wird nur noch gelesen, um sie einmalig in die gemeinsame config.json zu übernehmen.
/// </summary>
public sealed class TunerLegacySettings
{
    public string? DataDirectory { get; set; }
    public List<string> DeviceAddresses { get; set; } = [];
    public bool WarningAccepted { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("BITAXETUNER_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitaxeTuner");

    /// <summary>Datenordner des alten Tuners (Ergebnisse, profiles.json).</summary>
    public string EffectiveDataDirectory(string settingsDirectory) =>
        string.IsNullOrWhiteSpace(DataDirectory) ? settingsDirectory : DataDirectory!;

    public static TunerLegacySettings? TryLoad(string settingsDirectory)
    {
        var file = Path.Combine(settingsDirectory, "settings.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<TunerLegacySettings>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
