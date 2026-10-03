using System.Net;
using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Server;

/// <summary>
/// Start-Parameter des Servers. Reihenfolge: Kommandozeile, dann Umgebungsvariable, dann Standard.
/// <list type="bullet">
/// <item><c>--data &lt;Ordner&gt;</c> / <c>BITAXETUNER_DATA_DIR</c> – Datenordner (gleiches Format wie die Desktop-App)</item>
/// <item><c>--port &lt;n&gt;</c> / <c>BITAXETUNER_PORT</c> – Standard 8484</item>
/// <item><c>--bind &lt;IP&gt;</c> / <c>BITAXETUNER_BIND</c> – Standard alle Adressen</item>
/// <item><c>--allow-public</c> / <c>BITAXETUNER_ALLOW_PUBLIC=1</c> – auch Zugriffe von außerhalb des Heimnetzes (nicht empfohlen)</item>
/// <item><c>--https</c> / <c>BITAXETUNER_HTTPS=1</c> – selbst signiertes Zertifikat im Datenordner</item>
/// </list>
/// </summary>
public sealed class ServerSettings
{
    public const int DefaultPort = 8484;

    public required string DataDirectory { get; init; }
    public int Port { get; init; } = DefaultPort;
    public IPAddress? Bind { get; init; }
    public bool AllowPublic { get; init; }
    public bool Https { get; init; }

    /// <summary>HTTPS fest über Aufruf/Umgebung vorgegeben (--https, BITAXETUNER_HTTPS) – dann nicht in der Oberfläche umschaltbar.</summary>
    public bool HttpsFixed { get; init; }

    /// <summary>Gespeicherte Wahl HTTP/HTTPS im Datenordner (server-settings.json, additiv).</summary>
    public const string StoredFile = "server-settings.json";

    private sealed record Stored(bool? Https);

    public static bool? ReadStoredHttps(string dataDirectory)
    {
        try
        {
            var file = Path.Combine(dataDirectory, StoredFile);
            return File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<Stored>(File.ReadAllText(file))?.Https : null;
        }
        catch { return null; }
    }

    public static void WriteStoredHttps(string dataDirectory, bool https)
    {
        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, StoredFile);
        File.WriteAllText(file + ".tmp", System.Text.Json.JsonSerializer.Serialize(new Stored(https)));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>
    /// HTTPS (Audit S4): fest vorgegeben → so; sonst die gespeicherte Wahl; sonst Neuinstallation (noch keine Zugangsdaten
    /// und keine Einstellungen) → HTTPS; bestehende Installation → HTTP wie bisher (Umstellen in der Oberfläche).
    /// </summary>
    internal static bool ResolveHttps(string dataDirectory, bool? fixedValue)
    {
        if (fixedValue is { } f) return f;
        if (ReadStoredHttps(dataDirectory) is { } stored) return stored;
        // Ohne Schreiben: gemerkt wird die Wahl erst beim echten Start (Program.cs), nicht schon beim Lesen der Argumente
        return !File.Exists(Path.Combine(dataDirectory, "server-auth.json")) && !File.Exists(Path.Combine(dataDirectory, "config.json"));
    }

    public static ServerSettings FromArgs(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        static bool Flag(string? v) => v is "1" or "true" or "yes" or "on";

        var data = Arg("--data") ?? Env(DataPaths.EnvironmentVariable) ?? DefaultDataDirectory();
        var port = int.TryParse(Arg("--port") ?? Env("BITAXETUNER_PORT"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        var bind = IPAddress.TryParse(Arg("--bind") ?? Env("BITAXETUNER_BIND"), out var ip) ? ip : null;
        var dir = Path.GetFullPath(data);
        bool? httpsFixed = args.Contains("--https") ? true : Env("BITAXETUNER_HTTPS") is { } h ? Flag(h) : null;
        return new ServerSettings
        {
            DataDirectory = dir,
            Port = port,
            Bind = bind,
            AllowPublic = args.Contains("--allow-public") || Flag(Env("BITAXETUNER_ALLOW_PUBLIC")),
            Https = ResolveHttps(dir, httpsFixed),
            HttpsFixed = httpsFixed is not null,
        };
    }

    /// <summary>Windows-Dienst: %ProgramData%\BitaxeTuner. Linux: /var/lib/bitaxetuner (Dienst) bzw. ~/.local/share/bitaxetuner.</summary>
    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BitaxeTuner");
        const string system = "/var/lib/bitaxetuner";
        if (Directory.Exists(system) && IsWritable(system)) return system;
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(xdg, "bitaxetuner");
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
