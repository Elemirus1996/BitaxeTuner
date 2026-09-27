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
        return new ServerSettings
        {
            DataDirectory = Path.GetFullPath(data),
            Port = port,
            Bind = bind,
            AllowPublic = args.Contains("--allow-public") || Flag(Env("BITAXETUNER_ALLOW_PUBLIC")),
            Https = args.Contains("--https") || Flag(Env("BITAXETUNER_HTTPS")),
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
