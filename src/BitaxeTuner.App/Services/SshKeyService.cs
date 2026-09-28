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

    /// <summary>Benutzer und Rechner prüfen – sie landen in einer Befehlszeile, daher nur harmlose Zeichen.</summary>
    public static void Validate(string user, string host)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$"))
            throw new InvalidOperationException(L.T("Benutzername: Kleinbuchstaben, Ziffern, - oder _ (z. B. „pi“ oder „admin“)."));
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$|^\[?[0-9A-Fa-f:]+\]?$"))
            throw new InvalidOperationException(L.T("Ungültiger Rechnername oder IP-Adresse."));
    }

    /// <summary>Host aus der Server-Adresse („http://bitaxetuner.local:8484/“ → „bitaxetuner.local“).</summary>
    public static string? HostOf(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;
}
