using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Update;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server;

/// <summary>Wie der Server installiert ist – davon hängt ab, ob und wie er sich selbst aktualisieren kann.</summary>
public enum InstallKind
{
    /// <summary>Docker/Container: Update per <c>docker compose pull</c>, hier nur Hinweis.</summary>
    Docker,
    /// <summary>install.sh: /opt/bitaxetuner/versions/&lt;v&gt; + Symlink /opt/bitaxetuner/current, systemd startet neu.</summary>
    LinuxPackage,
    /// <summary>Windows-Setup mit Dienst: neues Setup still ausführen.</summary>
    WindowsService,
    /// <summary>Von Hand gestartet (Entwicklung, Test): nur Hinweis.</summary>
    Manual,
}

/// <summary>
/// Update-Prüfung und Selbst-Update des Servers. Jede Datei wird gegen die veröffentlichte SHA-256-Prüfsumme geprüft.
/// Linux: das neue Paket wird neben die laufende Version entpackt, dann wird der Symlink <c>current</c> atomar
/// umgehängt; die alte Version bleibt als Rückfall liegen. systemd startet den Dienst neu.
/// </summary>
public sealed class ServerUpdater(HubService hub, IHostApplicationLifetime lifetime, ILogger<ServerUpdater> log) : BackgroundService
{
    public const string Repository = "Elemirus1996/BitaxeTuner";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private UpdateInfo? _latest;
    private int _installing;

    /// <summary>Abstand der automatischen Prüfungen.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public string? LastMessage { get; private set; }
    public DateTime? LastCheck { get; private set; }
    /// <summary>Nächste automatische Prüfung (null: ausgeschaltet oder Dienst beendet).</summary>
    public DateTime? NextCheck { get; private set; }
    public UpdateInfo? Latest => _latest;
    public Version Current => Version.TryParse(Api.Endpoints.Version.Split('-')[0], out var v) ? v : new Version(0, 0, 0);

    public static InstallKind DetectKind()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true") return InstallKind.Docker;
        if (OperatingSystem.IsLinux() && LinuxLayout() is not null) return InstallKind.LinuxPackage;
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            return InstallKind.WindowsService;
        return InstallKind.Manual;
    }

    /// <summary>
    /// Layout von install.sh erkennen: &lt;Wurzel&gt;/versions/&lt;v&gt;/ und Symlink &lt;Wurzel&gt;/current → dorthin.
    /// Gestartet wird über den Symlink, daher beide Fälle auflösen. Liefert Wurzel und echten Ordner der laufenden Version.
    /// </summary>
    public static (string Root, string Running)? LinuxLayout()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        if (dir.LinkTarget is not null && dir.ResolveLinkTarget(true) is DirectoryInfo real) dir = real;
        if (dir.Parent is not { Name: "versions" } versions || versions.Parent is not { } root) return null;
        var current = new DirectoryInfo(Path.Combine(root.FullName, "current"));
        return current.LinkTarget is null ? null : (root.FullName, dir.FullName);
    }

    /// <summary>Name des passenden Release-Pakets für diese Plattform.</summary>
    public static bool MatchesPlatform(string name)
    {
        if (OperatingSystem.IsWindows())
            return name.StartsWith("BitaxeTuner-Server-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm",
            _ => "linux-x64",
        };
        return name.StartsWith("BitaxeTuner-Server-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{arch}.tar.gz", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanInstall => DetectKind() is InstallKind.LinuxPackage or InstallKind.WindowsService;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            NextCheck = DateTime.Now + delay;
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            delay = Interval;
            try
            {
                // Einstellung auf dem Hub-Thread lesen (Config gehört dem Hub)
                if (!await hub.RunAsync(h => h.Config.CheckForUpdates))
                {
                    log.LogInformation("Automatische Update-Prüfung ausgeschaltet (Einstellungen → Nach neuen Versionen suchen).");
                    continue;
                }
                var result = await CheckAsync(notify: true, stoppingToken);
                log.LogInformation("Update-Prüfung: {Status} – {Message} Nächste Prüfung {Next:g}.",
                    result.Status, result.Message, DateTime.Now + Interval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Nie den Dienst beenden (in .NET 8 würde eine Ausnahme hier den ganzen Server stoppen)
                LastCheck = DateTime.Now;
                LastMessage = ex.Message;
                log.LogError(ex, "Update-Prüfung fehlgeschlagen – nächster Versuch in {Hours} h.", Interval.TotalHours);
            }
        }
        NextCheck = null;
    }

    /// <summary>Push nur einmal je Version, nur mit eingerichtetem Push-Dienst und „Wartung“ an.</summary>
    internal static bool ShouldNotify(Core.Config.AppConfig config, bool pushEnabled, string tag) =>
        config.NotifiedServerVersion != tag && pushEnabled && config.Notifications.OnMaintenance;

    public async Task<UpdateCheckResult> CheckAsync(bool notify, CancellationToken ct = default)
    {
        var result = await new UpdateService(_http, Repository, MatchesPlatform).CheckAsync(Current, ct);
        LastCheck = DateTime.Now;
        LastMessage = result.Message;
        _latest = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Update : null;
        if (notify && _latest is { } u)
        {
            await hub.RunAsync(h =>
            {
                if (!ShouldNotify(h.Config, h.Notify.Enabled, u.Tag)) return false;
                h.Config.NotifiedServerVersion = u.Tag;
                h.Config.Save();
                h.SendAlert(new Alert($"server-update:{u.Tag}", L.T("BitaxeTuner-Server {0} verfügbar", u.Tag),
                    CanInstall ? L.T("Installation per Klick in der Server-Oberfläche (Einstellungen).") : "Update: docker compose pull && docker compose up -d",
                    NotifyPriority.Low, TimeSpan.FromDays(30)));
                return true;
            });
        }
        return result;
    }

    /// <summary>Update laden, prüfen und installieren. Der Dienst startet danach neu.</summary>
    public async Task InstallAsync()
    {
        if (_latest is not { } u) throw new InvalidOperationException(L.N("Kein Update verfügbar – zuerst nach Updates suchen."));
        if (!CanInstall) throw new InvalidOperationException(L.N("Diese Installation aktualisiert sich nicht selbst (Docker: „docker compose pull“, sonst neues Paket installieren)."));
        if (Interlocked.Exchange(ref _installing, 1) == 1) throw new InvalidOperationException(L.N("Update läuft bereits."));
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "bitaxetuner-update");
            var file = await new UpdateService(_http, Repository, MatchesPlatform).DownloadAsync(u, work); // prüft SHA-256
            log.LogWarning("Update {Tag} geladen und geprüft – installiere …", u.Tag);

            // Laufende Benchmarks sauber beenden (Einstellungen wiederherstellen), Stand sichern
            await hub.RunAsync(async h =>
            {
                await h.Benchmarks.StopAllAsync();
                h.Config.Save();
            });

            if (DetectKind() == InstallKind.WindowsService)
            {
                // Das Setup stoppt den Dienst, ersetzt die Dateien und startet ihn wieder
                Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            SwapLinuxVersion(file, u.Version.ToString(3));
            log.LogWarning("Update {Tag} installiert – Neustart durch systemd.", u.Tag);
            lifetime.StopApplication(); // systemd (Restart=always) startet die neue Version
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    /// <summary>/opt/bitaxetuner/versions/&lt;neu&gt; entpacken, Symlink current atomar umhängen, alte Versionen bis auf eine löschen.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void SwapLinuxVersion(string archive, string version)
    {
        var (root, running) = LinuxLayout() ?? throw new InvalidOperationException(L.N("Installationslayout nicht erkannt."));
        var versionsDir = Path.Combine(root, "versions");
        var target = Path.Combine(versionsDir, version);
        var staging = target + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        using (var fs = File.OpenRead(archive))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, staging, overwriteFiles: false);
        // Paket enthält einen Ordner bitaxetuner-server/ – dessen Inhalt ist die Version
        var inner = Directory.GetDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, "BitaxeTuner.Server")));
        var source = inner ?? staging;
        var exe = Path.Combine(source, "BitaxeTuner.Server");
        if (!File.Exists(exe)) throw new InvalidDataException("Paket enthält kein BitaxeTuner.Server.");
        File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(source, target);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);

        // Atomar: neuen Link anlegen und per rename über "current" legen
        var current = Path.Combine(root, "current");
        var tmpLink = Path.Combine(root, "current.tmp");
        if (File.Exists(tmpLink) || Directory.Exists(tmpLink)) File.Delete(tmpLink);
        File.CreateSymbolicLink(tmpLink, target);
        if (rename(tmpLink, current) != 0) throw new IOException("Symlink konnte nicht umgehängt werden (errno " + Marshal.GetLastPInvokeError() + ").");

        // Aufräumen: laufende (jetzt vorherige) und neue Version behalten
        var keep = new[] { target, running };
        foreach (var d in Directory.GetDirectories(versionsDir).Where(d => !keep.Contains(d)).OrderBy(Directory.GetCreationTimeUtc))
        {
            try { Directory.Delete(d, true); } catch { /* egal */ }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(string oldpath, string newpath);

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
