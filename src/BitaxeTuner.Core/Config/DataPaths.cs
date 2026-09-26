using System.Text.Json;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Ermittelt den Datenordner (config.json, history.db, tax\, tuning\).
///
/// Standard ist der bisherige Ordner von BitaxeMonitor: %AppData%\BitaxeMonitor.
/// Wurde der Ordner umgezogen, steht der neue Pfad in %AppData%\BitaxeMonitor\datadir.json.
/// Die Umgebungsvariable BITAXETUNER_DATA_DIR hat Vorrang (Tests, Arbeiten mit einer Datenkopie).
/// </summary>
public static class DataPaths
{
    public const string EnvironmentVariable = "BITAXETUNER_DATA_DIR";
    public const string BootstrapFileName = "datadir.json";

    /// <summary>Stammordner von BitaxeMonitor – hier liegt immer die Bootstrap-Datei.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BitaxeMonitor");

    public static string BootstrapFile => Path.Combine(DefaultDirectory, BootstrapFileName);

    private static string? _current;

    /// <summary>Aktueller Datenordner (einmal ermittelt, danach gecacht).</summary>
    public static string Current => _current ??= Resolve();

    public static string ConfigFile => Path.Combine(Current, "config.json");
    public static string HistoryFile => Path.Combine(Current, "history.db");
    public static string TaxDirectory => Path.Combine(Current, "tax");
    public static string TuningDirectory => Path.Combine(Current, "tuning");
    public static string SnapshotsDirectory => Path.Combine(Current, "snapshots");

    /// <summary>Nur für Tests: Datenordner fest vorgeben.</summary>
    public static void Override(string? directory) => _current = directory;

    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            if (File.Exists(BootstrapFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(BootstrapFile));
                if (doc.RootElement.TryGetProperty("DataDirectory", out var d) &&
                    d.GetString() is { Length: > 0 } dir && Directory.Exists(dir))
                    return dir;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* Standard verwenden */ }

        return DefaultDirectory;
    }
}
