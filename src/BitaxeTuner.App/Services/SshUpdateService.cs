using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Update;

namespace BitaxeTuner.App.Services;

/// <summary>
/// 0.9.16: Notfall-Update und Systemdateien per SSH (Schlüssel aus <see cref="SshKeyService"/>, sudo ohne Passwort wie bei
/// Raspberry Pi OS üblich). Für den Fall, dass das Update über die Oberfläche nicht geht oder neue Systemdateien
/// (Dienst, USB-Regel, Neustart-Funktion) gebraucht werden – ein Update über die Oberfläche tauscht nur das Programm.
///
/// Sicherheit: Es wird nie ein Skript ausgeführt, das schon auf dem Pi liegt (die Programmdateien gehören dem Dienst).
/// Das Paket kommt aus dem GitHub-Release, ist signiert (SHA256SUMS.txt.sig) und wird hier und auf dem Pi gegen die
/// Prüfsumme kontrolliert; erst dann läuft dessen install.sh mit sudo. Vor dem Update: Handsicherung des Datenordners
/// (Dienst gestoppt, damit history.db konsistent ist) nach /var/backups/bitaxetuner.
/// </summary>
public sealed class SshUpdateService(string user, string host, HttpClient http, Action<string> progress)
{
    private const string RemoteArchive = "/var/tmp/bitaxetuner-update.tar.gz";
    private const string RemoteDir = "/var/tmp/bitaxetuner-update";

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    public sealed record Result(string Output, int ExitCode);

    private static string Ssh => SshKeyService.Tool("ssh") ?? throw new InvalidOperationException(
        L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));

    private static string Scp => SshKeyService.Tool("scp") ?? throw new InvalidOperationException(
        L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));

    private IEnumerable<string> CommonArgs() =>
        ["-i", SshKeyService.KeyFile, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=15"];

    private static async Task<Result> RunProcessAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
            throw new TimeoutException(L.T("Zeitüberschreitung bei der SSH-Verbindung zu {0}.", Path.GetFileName(exe)));
        }
        return new Result(((await stdout) + (await stderr)).Trim(), p.ExitCode);
    }

    /// <summary>Befehl auf dem Pi ausführen (nur fest vorgegebene Befehle – keine Eingaben von außen).</summary>
    private Task<Result> RunAsync(string remote, TimeSpan? timeout = null) =>
        RunProcessAsync(Ssh, [.. CommonArgs(), $"{user}@{host}", remote], timeout ?? TimeSpan.FromMinutes(2));

    private async Task<string> RunOkAsync(string remote, string failure, TimeSpan? timeout = null)
    {
        var r = await RunAsync(remote, timeout);
        if (r.ExitCode != 0) throw new InvalidOperationException(failure + (r.Output.Length > 0 ? "\n\n" + Tail(r.Output) : ""));
        return r.Output;
    }

    private static string Tail(string text, int lines = 12) => string.Join("\n", text.Split('\n').TakeLast(lines));

    /// <summary>Verbindung, sudo ohne Passwort und Architektur prüfen; liefert z. B. „linux-arm64“ und die installierte Version.</summary>
    public async Task<(string Arch, string? Installed)> ProbeAsync()
    {
        SshKeyService.Validate(user, host);
        progress(L.T("Verbinde mit {0} …", host));
        var r = await RunAsync("uname -m; readlink -f /opt/bitaxetuner/current 2>/dev/null; sudo -n true && echo SUDO-OK");
        if (r.ExitCode != 0 && !r.Output.Contains("SUDO-OK"))
        {
            if (r.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("SSH-Anmeldung ohne Passwort klappt nicht – unter „Betriebsart …“ den Schlüssel übertragen."));
            if (r.Output.Contains("password is required", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("sudo:", StringComparison.Ordinal))
                throw new InvalidOperationException(L.T("sudo verlangt auf dem Pi ein Passwort – dann bitte im SSH-Terminal selbst ausführen:\n{0}",
                    "sudo sh /opt/bitaxetuner/current/install.sh --system"));
            throw new InvalidOperationException(L.T("Keine SSH-Verbindung zu {0}: {1}", host, Tail(r.Output, 4)));
        }
        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arch = lines.FirstOrDefault() switch
        {
            "aarch64" or "arm64" => "linux-arm64",
            "armv7l" or "armv6l" or "armhf" => "linux-arm",
            "x86_64" or "amd64" => "linux-x64",
            var other => throw new InvalidOperationException(L.T("Unbekannte Architektur auf dem Pi: {0}", other ?? "–")),
        };
        var installed = lines.FirstOrDefault(l => l.StartsWith("/opt/bitaxetuner/versions/", StringComparison.Ordinal)) is { } path
            && Path.GetFileName(path) is var v && VersionPattern.IsMatch(v) ? v : null;
        return (arch, installed);
    }

    /// <summary>Neuestes Release-Paket für die Architektur prüfen (signiert) und herunterladen.</summary>
    public async Task<(UpdateInfo Info, string File)> DownloadAsync(string arch)
    {
        progress(L.T("Suche das Server-Paket für {0} …", arch));
        var service = new UpdateService(http, UpdateChecker.Repository,
            name => name.StartsWith("BitaxeTuner-Server-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{arch}.tar.gz", StringComparison.OrdinalIgnoreCase));
        var check = await service.CheckAsync(new Version(0, 0, 0));
        if (check.Update is not { } info) throw new InvalidOperationException(check.Message);
        if (!VersionPattern.IsMatch(info.Version.ToString(3))) throw new InvalidOperationException(L.T("Unerwartete Versionsnummer: {0}", info.Tag));
        progress(L.T("Lade {0} …", info.SetupName));
        var file = await service.DownloadAsync(info, Path.Combine(Path.GetTempPath(), "BitaxeTuner-Server-Update"),
            new Progress<double>(p => progress(L.T("Lade {0} … {1:P0}", info.SetupName, p))));   // prüft Signatur und Prüfsumme
        return (info, file);
    }

    /// <summary>Paket hochladen und auf dem Pi erneut gegen die Prüfsumme kontrollieren, dann entpacken.</summary>
    private async Task UploadAsync(string file, string sha256)
    {
        progress(L.T("Übertrage das Paket auf den Pi …"));
        var scp = await RunProcessAsync(Scp, [.. CommonArgs(), file, $"{user}@{host}:{RemoteArchive}"], TimeSpan.FromMinutes(10));
        if (scp.ExitCode != 0) throw new InvalidOperationException(L.T("Übertragung fehlgeschlagen: {0}", Tail(scp.Output, 4)));
        var sum = await RunOkAsync($"sha256sum {RemoteArchive}", L.T("Prüfsumme auf dem Pi konnte nicht berechnet werden."));
        if (!sum.StartsWith(sha256, StringComparison.OrdinalIgnoreCase))
        {
            await RunAsync($"rm -f {RemoteArchive}");
            throw new InvalidOperationException(L.T("Prüfsumme auf dem Pi stimmt nicht – Paket verworfen, nichts installiert."));
        }
        await RunOkAsync($"rm -rf {RemoteDir} && mkdir -p {RemoteDir} && tar -xzf {RemoteArchive} -C {RemoteDir} && test -f {RemoteDir}/bitaxetuner-server/install.sh",
            L.T("Paket konnte auf dem Pi nicht entpackt werden."));
    }

    private Task CleanupAsync() => RunAsync($"rm -rf {RemoteDir} {RemoteArchive}");

    /// <summary>
    /// Notfall-Update: Handsicherung (Dienst gestoppt), Paket installieren (install.sh – Programm, Dienste, USB-Regel,
    /// Neustart). Bei einem Fehler nach dem Stoppen wird der Dienst wieder gestartet. Liefert die Datei der Handsicherung.
    /// </summary>
    public async Task<string> UpdateAsync(UpdateInfo info, string file)
    {
        await UploadAsync(file, info.Sha256!);
        progress(L.T("Handsicherung des Datenordners (Dienst wird dafür gestoppt) …"));
        // Platz prüfen (ohne die ohnehin vorhandenen Sicherungen und vorherigen Stände), dann stoppen und packen
        const string backup =
            "set -e; d=/var/backups/bitaxetuner; sudo -n mkdir -p $d; " +
            "need=$(sudo -n du -sb --exclude=auto-backups --exclude='backup-*' --exclude='.export-*' --exclude=tmp /var/lib/bitaxetuner | cut -f1); " +
            "avail=$(df --output=avail -B1 $d | tail -1); " +
            "if [ \"$avail\" -lt \"$need\" ]; then echo NOSPACE; exit 3; fi; " +
            "f=$d/vor-update-$(date +%Y%m%d-%H%M%S).tar.gz; " +
            "sudo -n systemctl stop bitaxetuner; " +
            "sudo -n tar -czf $f -C /var/lib --exclude=bitaxetuner/auto-backups --exclude='bitaxetuner/backup-*' --exclude='bitaxetuner/.export-*' --exclude=bitaxetuner/tmp bitaxetuner; " +
            "sudo -n chmod 600 $f; echo FILE=$f";
        var b = await RunAsync(backup, TimeSpan.FromMinutes(20));
        if (b.ExitCode != 0)
        {
            await RunAsync("sudo -n systemctl start bitaxetuner");
            await CleanupAsync();
            throw new InvalidOperationException(b.Output.Contains("NOSPACE")
                ? L.T("Zu wenig Platz für die Handsicherung auf dem Pi – nichts installiert, Dienst läuft weiter.")
                : L.T("Handsicherung fehlgeschlagen – nichts installiert, Dienst wurde wieder gestartet.") + "\n\n" + Tail(b.Output));
        }
        var saved = b.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("FILE=", StringComparison.Ordinal))?[5..] ?? "/var/backups/bitaxetuner";

        progress(L.T("Installiere {0} …", info.Tag));
        var r = await RunAsync($"cd {RemoteDir}/bitaxetuner-server && sudo -n ./install.sh", TimeSpan.FromMinutes(15));
        await CleanupAsync();
        if (r.ExitCode != 0)
        {
            await RunAsync("sudo -n systemctl start bitaxetuner");
            throw new InvalidOperationException(L.T("Installation fehlgeschlagen (Handsicherung: {0}):", saved) + "\n\n" + Tail(r.Output));
        }
        return saved;
    }

    /// <summary>Nur Systemdateien (Dienst, Neustart-Funktion, USB-Regel) aus dem geprüften Paket neu einrichten.</summary>
    public async Task<string> SystemFilesAsync(UpdateInfo info, string file)
    {
        await UploadAsync(file, info.Sha256!);
        progress(L.T("Richte die Systemdateien ein …"));
        var r = await RunAsync($"cd {RemoteDir}/bitaxetuner-server && sudo -n sh ./install.sh --system", TimeSpan.FromMinutes(5));
        await CleanupAsync();
        if (r.ExitCode != 0) throw new InvalidOperationException(L.T("Systemdateien konnten nicht eingerichtet werden:") + "\n\n" + Tail(r.Output));
        return r.Output;
    }
}
