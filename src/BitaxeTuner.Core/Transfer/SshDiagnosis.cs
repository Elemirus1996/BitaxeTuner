using System.Text.RegularExpressions;

namespace BitaxeTuner.Core.Transfer;

/// <summary>Ergebnis eines Anmeldeversuchs per SSH mit dem Schlüssel dieses PCs.</summary>
public enum SshProbeResult
{
    /// <summary>Anmeldung mit dem Schlüssel klappt.</summary>
    Ok,
    /// <summary>Der Server hat einen anderen Host-Schlüssel als gemerkt (z. B. nach Pi-Tausch oder Neuinstallation).</summary>
    HostKeyChanged,
    /// <summary>Server erreichbar, aber der Schlüssel wird nicht angenommen (Passwort-Anmeldung evtl. ausgeschaltet).</summary>
    KeyRejected,
    /// <summary>Server nicht erreichbar (aus, falsche Adresse, Zeitüberschreitung).</summary>
    Unreachable,
    Other,
}

/// <summary>
/// 0.9.22: Ausgabe von OpenSSH deuten – damit die Desktop-App einen geänderten Host-Schlüssel (Pi-Tausch) selbst erkennt
/// und „Schlüssel übertragen“ erst prüft, ob der Schlüssel schon funktioniert.
/// </summary>
public static partial class SshDiagnosis
{
    public const string OkMarker = "BT-SSH-OK";

    public static SshProbeResult Classify(string output, int exitCode)
    {
        if (exitCode == 0 && output.Contains(OkMarker, StringComparison.Ordinal)) return SshProbeResult.Ok;
        if (output.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return SshProbeResult.HostKeyChanged;
        if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return SshProbeResult.KeyRejected;
        if (output.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) || output.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No route to host", StringComparison.OrdinalIgnoreCase) || output.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return SshProbeResult.Unreachable;
        return SshProbeResult.Other;
    }

    [GeneratedRegex(@"SHA256:[A-Za-z0-9+/]{20,}=*")]
    private static partial Regex FingerprintPattern();

    /// <summary>Ersten SHA-256-Fingerabdruck aus einer Ausgabe von <c>ssh-keygen -l</c> bzw. der Warnung von ssh lesen.</summary>
    public static string? Fingerprint(string text) => FingerprintPattern().Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>Zeilen von <c>ssh-keyscan</c> für die known_hosts (nur echte Schlüsselzeilen, keine Kommentare).</summary>
    public static List<string> KnownHostLines(string keyscanOutput, string host) =>
        keyscanOutput.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => l.Split(' ') is { Length: >= 3 } p && p[0] == host && p[1].StartsWith("ssh-", StringComparison.Ordinal))
            .ToList();
}
