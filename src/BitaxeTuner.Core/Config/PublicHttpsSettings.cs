namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.12: HTTPS ohne Browser-Warnung – Zertifikat von Let's Encrypt für einen DuckDNS-Namen, bestätigt per DNS-01
/// (TXT-Eintrag über die DuckDNS-API). Kein Port im Router nötig, der Server bleibt im Heimnetz. Das DuckDNS-Token liegt
/// in secrets.json, nicht hier. Additiv, standardmäßig aus.
/// </summary>
public sealed class PublicHttpsSettings
{
    public bool Enabled { get; set; }

    /// <summary>Nur der Teil vor „.duckdns.org“, z. B. „meinminer“.</summary>
    public string Subdomain { get; set; } = "";

    /// <summary>Optionale E-Mail für Hinweise von Let's Encrypt (Ablauf, Änderungen).</summary>
    public string Email { get; set; } = "";

    /// <summary>Testzertifikat von der Let's-Encrypt-Staging-Umgebung (für den ersten Versuch, ohne Rate-Limits).</summary>
    public bool Staging { get; set; }

    /// <summary>DuckDNS-Eintrag auf diese Adresse setzen; leer = Heimnetz-Adresse des Servers automatisch.</summary>
    public string Ip { get; set; } = "";

    public string HostName => Subdomain.Length > 0 ? $"{Subdomain}.duckdns.org" : "";

    /// <summary>Gültiger DuckDNS-Subdomain-Name (Buchstaben, Ziffern, Bindestrich; 1–63 Zeichen).</summary>
    public static bool ValidSubdomain(string? s) =>
        s is { Length: > 0 and <= 63 } && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && s[0] != '-' && s[^1] != '-';
}
