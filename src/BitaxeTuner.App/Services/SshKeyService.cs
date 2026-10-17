using System.Diagnostics;
using System.IO;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Services;

/// <summary>
/// SSH ohne Passwort zum Raspberry Pi: eigener Ed25519-Schlüssel dieses PCs (Windows-OpenSSH, <c>ssh-keygen</c>),
/// Eintrag auf dem Pi (beim Vorbereiten per cloud-init, sonst einmal mit Passwort) und Terminal öffnen.
/// Der private Schlüssel bleibt im Benutzerprofil (%USERPROFILE%\.ssh) und ist nur für dieses Windows-Konto lesbar.
/// </summary>
public static class SshKeyService
{
    public static string KeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "bitaxetuner_ed25519");
    public static string PublicKeyFile => KeyFile + ".pub";

    /// <summary>Windows-OpenSSH (optionales Feature, ab Windows 10 1809 Standard) – vollständiger Pfad oder null.</summary>
    public static string? Tool(string name)
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", name + ".exe");
        if (File.Exists(system)) return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), name + ".exe");
            if (dir.Length > 0 && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Öffentlichen Schlüssel liefern, bei Bedarf einmalig erzeugen (ohne Passphrase – dafür ist er da).</summary>
    public static async Task<string> EnsureKeyAsync()
    {
        if (!File.Exists(KeyFile) || !File.Exists(PublicKeyFile))
        {
            var keygen = Tool("ssh-keygen") ?? throw new InvalidOperationException(
                L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
            var psi = new ProcessStartInfo(keygen)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-q", "-t", "ed25519", "-N", "", "-C", $"bitaxetuner@{Environment.MachineName}", "-f", KeyFile })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || !File.Exists(PublicKeyFile))
                throw new InvalidOperationException(L.T("SSH-Schlüssel konnte nicht erzeugt werden: {0}", err.Trim()));
        }
        return (await File.ReadAllTextAsync(PublicKeyFile)).Trim();
    }

    /// <summary>Terminal mit SSH-Verbindung öffnen (Windows Terminal, sonst Konsole).</summary>
    public static void OpenTerminal(string user, string host)
    {
        Validate(user, host);
        var ssh = Tool("ssh") ?? throw new InvalidOperationException(
            L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));
        // accept-new: beim ersten Verbinden Host-Schlüssel merken, danach wird eine Änderung weiterhin gemeldet
        var args = $"-i \"{KeyFile}\" -o StrictHostKeyChecking=accept-new {user}@{host}";
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var psi = File.Exists(wt)
            ? new ProcessStartInfo(wt, $"new-tab --title \"{host}\" \"{ssh}\" {args}")
            : new ProcessStartInfo(ssh, args);
        psi.UseShellExecute = true;
        Process.Start(psi);
    }

    /// <summary>
    /// Schlüssel auf einen laufenden Pi übertragen: Konsole mit ssh, der Nutzer gibt einmal das Pi-Passwort ein.
    /// Der Schlüssel wird nur ergänzt, wenn er noch fehlt; Rechte von ~/.ssh wie von sshd verlangt.
    /// </summary>
    public static void InstallOnPi(string user, string host)
    {
        Validate(user, host);
        var ssh = Tool("ssh") ?? throw new InvalidOperationException(
            L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));
        // Ohne innere Anführungszeichen (cmd und ssh würden sie unterschiedlich deuten): Schlüssel per stdin in eine
        // Temp-Datei, als ganze Zeile suchen (grep -xFf), nur bei Fehlen anhängen.
        const string remote = "umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; t=$(mktemp); cat > $t; " +
                              "grep -qxFf $t ~/.ssh/authorized_keys || cat $t >> ~/.ssh/authorized_keys; rm -f $t; echo OK";
        var script = Path.Combine(Path.GetTempPath(), "bitaxetuner-ssh-key.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            "chcp 65001 >nul",
            $"echo {L.T("Schlüssel auf {0}@{1} eintragen – bitte einmal das Pi-Passwort eingeben.", user, host)}",
            $"\"{ssh}\" -o StrictHostKeyChecking=accept-new {user}@{host} \"{remote}\" < \"{PublicKeyFile}\"",
            "echo.",
            $"if errorlevel 1 (echo {L.T("Fehlgeschlagen – Benutzer, Adresse und Passwort prüfen.")}) else (echo {L.T("Fertig – ab jetzt ohne Passwort.")})",
            "pause") + "\r\n", new System.Text.UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true });
    }

    // ---------- 0.9.22: Pi-Tausch und „Schlüssel übertragen“ ----------

    public static string KnownHostsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    private static async Task<(string Output, int ExitCode)> RunToolAsync(string tool, IEnumerable<string> args, TimeSpan timeout, string? stdin = null)
    {
        var exe = Tool(tool) ?? throw new InvalidOperationException(
            L.T("OpenSSH ist nicht installiert (Windows-Einstellungen → Apps → Optionale Features → „OpenSSH-Client“)."));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin is not null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { /* beendet */ } return ("Connection timed out", 255); }
        return ((await o) + "\n" + (await e), p.ExitCode);
    }

    /// <summary>Mit dem Schlüssel dieses PCs anmelden, ohne Rückfragen (BatchMode) – ändert nichts auf dem Pi.</summary>
    public static async Task<Core.Transfer.SshProbeResult> ProbeAsync(string user, string host)
    {
        Validate(user, host);
        var (output, code) = await RunToolAsync("ssh",
            ["-i", KeyFile, "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=8",
             $"{user}@{host}", "echo " + Core.Transfer.SshDiagnosis.OkMarker], TimeSpan.FromSeconds(20));
        return Core.Transfer.SshDiagnosis.Classify(output, code);
    }

    /// <summary>Gemerkter und aktueller Host-Schlüssel (Fingerabdrücke) sowie die neuen Zeilen für known_hosts.</summary>
    public static async Task<(string? Old, string? New, List<string> Lines)> HostKeysAsync(string host)
    {
        var (known, _) = await RunToolAsync("ssh-keygen", ["-F", host, "-l", "-f", KnownHostsFile], TimeSpan.FromSeconds(10));
        var (scan, _) = await RunToolAsync("ssh-keyscan", ["-T", "8", "-t", "ed25519", host], TimeSpan.FromSeconds(20));
        var lines = Core.Transfer.SshDiagnosis.KnownHostLines(scan, host);
        string? fresh = null;
        if (lines.Count > 0)
        {
            var (fp, _) = await RunToolAsync("ssh-keygen", ["-l", "-f", "-"], TimeSpan.FromSeconds(10), string.Join("\n", lines) + "\n");
            fresh = Core.Transfer.SshDiagnosis.Fingerprint(fp);
        }
        // Gemerkt sind oft mehrere Schlüsseltypen – zum Vergleich den ED25519-Eintrag nehmen (den holt auch ssh-keyscan)
        var oldLine = known.Split('\n').FirstOrDefault(l => l.Contains("ED25519", StringComparison.OrdinalIgnoreCase)) ?? known;
        return (Core.Transfer.SshDiagnosis.Fingerprint(oldLine), fresh, lines);
    }

    /// <summary>Alten Eintrag entfernen (ssh-keygen legt known_hosts.old an) und genau den bestätigten neuen Schlüssel eintragen.</summary>
    public static async Task ReplaceHostKeyAsync(string host, IReadOnlyList<string> lines)
    {
        await RunToolAsync("ssh-keygen", ["-R", host, "-f", KnownHostsFile], TimeSpan.FromSeconds(10));
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsFile)!);
        var lead = File.Exists(KnownHostsFile) && new FileInfo(KnownHostsFile).Length > 0 && !(await File.ReadAllTextAsync(KnownHostsFile)).EndsWith('\n') ? "\n" : "";
        await File.AppendAllTextAsync(KnownHostsFile, lead + string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// Vor jeder SSH-Aktion: Hat der Server einen anderen Host-Schlüssel als gemerkt (z. B. neuer Pi unter derselben Adresse),
    /// beide Fingerabdrücke zeigen und nur nach Bestätigung den neuen übernehmen. Liefert false, wenn abgebrochen wurde.
    /// </summary>
    public static async Task<bool> EnsureHostKeyAsync(System.Windows.Window owner, string user, string host, string title)
    {
        if (await ProbeAsync(user, host) != Core.Transfer.SshProbeResult.HostKeyChanged) return true;
        var (old, fresh, lines) = await HostKeysAsync(host);
        if (lines.Count == 0 || fresh is null)
        {
            System.Windows.MessageBox.Show(owner, L.T("Der SSH-Schlüssel von {0} hat sich geändert, der neue ließ sich aber nicht abrufen. Bitte später erneut versuchen.", host),
                title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
        var text = L.T("Der Server {0} meldet sich mit einem anderen SSH-Schlüssel als beim letzten Mal.", host) + "\n\n" +
                   L.T("Bisher: {0}", old ?? "–") + "\n" + L.T("Jetzt: {0}", fresh) + "\n\n" +
                   L.T("Das ist normal, wenn du den Pi getauscht oder neu aufgesetzt hast (gleiche Adresse). Sonst nicht bestätigen – dann könnte sich ein anderes Gerät als dein Server ausgeben.") + "\n\n" +
                   L.T("Zum Abgleichen am Pi: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub") + "\n\n" +
                   L.T("Neuen Schlüssel übernehmen?");
        if (System.Windows.MessageBox.Show(owner, text, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.Yes)
            return false;
        await ReplaceHostKeyAsync(host, lines);
        return true;
    }

    /// <summary>Benutzer und Rechner prüfen – sie landen in einer Befehlszeile, daher nur harmlose Zeichen.</summary>
    public static void Validate(string user, string host)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$"))
            throw new InvalidOperationException(L.T("Benutzername: Kleinbuchstaben, Ziffern, - oder _ (z. B. „pi“ oder „admin“)."));
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$|^\[?[0-9A-Fa-f:]+\]?$"))
            throw new InvalidOperationException(L.T("Ungültiger Rechnername oder IP-Adresse."));
    }

    /// <summary>Rechner für SSH: eigener Eintrag oder aus der Server-Adresse; null, wenn keiner bekannt ist.</summary>
    public static string? HostFor(Core.Config.ServerConnectionSettings s) =>
        s.SshHost.Trim() is { Length: > 0 } h ? h : HostOf(s.Url);

    /// <summary>
    /// Konsole auf dem Server öffnen (Hauptfenster und Server-Fenster). Fehlen Angaben oder schlägt es fehl, wird
    /// gefragt, ob „Betriebsart …“ geöffnet werden soll (dort Benutzer, Rechner und „Schlüssel übertragen …“).
    /// </summary>
    public static async Task OpenForServerAsync(System.Windows.Window owner, Core.Config.ServerConnectionSettings s, Action openMode)
    {
        try
        {
            var host = HostFor(s) ?? "";
            Validate(s.SshUser, host);
            await EnsureKeyAsync();
            if (!await EnsureHostKeyAsync(owner, s.SshUser, host, L.T("SSH-Terminal zum Server"))) return;
            OpenTerminal(s.SshUser, host);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            if (System.Windows.MessageBox.Show(owner, ex.Message + L.T("\n\nEinstellungen unter „Betriebsart …“ öffnen?"), L.T("SSH-Terminal zum Server"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes)
                openMode();
        }
    }

    /// <summary>Host aus der Server-Adresse („http://bitaxetuner.local:8484/“ → „bitaxetuner.local“).</summary>
    public static string? HostOf(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;
}
