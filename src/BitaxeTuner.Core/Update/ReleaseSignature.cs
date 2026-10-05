using System.Security.Cryptography;
using System.Text;

namespace BitaxeTuner.Core.Update;

/// <summary>
/// Signatur der Releases (Audit S2): Der Release-Workflow signiert SHA256SUMS.txt mit einem privaten ECDSA-P-256-Schlüssel
/// und legt SHA256SUMS.txt.sig (Base64, IEEE-P1363) daneben. Updates werden nur installiert, wenn die Signatur zu einem der
/// hier eingebauten öffentlichen Schlüssel passt, die Datei zur signierten Prüfsumme und ihr Dateiname zur Version (Audit
/// N-Sec1, kein Downgrade). Schutzwirkung: Geänderte oder untergeschobene Dateien im Release (z. B. per gestohlenem Token mit
/// Schreibrecht auf Releases) werden erkannt. Der Schlüssel liegt als Secret der geschützten GitHub-Umgebung „release“;
/// signiert wird erst nach Freigabe durch den Projektinhaber. Wer dessen Konto selbst übernimmt, kann weiterhin ein
/// signiertes Release auslösen – dagegen schützt nur der offline verwahrte Reserveschlüssel (Schlüsseltausch).
/// </summary>
public static class ReleaseSignature
{
    public const string SumsName = "SHA256SUMS.txt";
    public const string SignatureName = "SHA256SUMS.txt.sig";

    /// <summary>
    /// Vertrauenswürdige öffentliche Schlüssel (SubjectPublicKeyInfo, Base64). Hauptschlüssel = GitHub-Secret; der
    /// Reserveschlüssel liegt offline beim Projektinhaber und ersetzt ihn, falls er verloren geht oder kompromittiert ist.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedKeys =
    [
        // Erster Hauptschlüssel (erstellt 03.10.2026) – der private Teil ist nicht mehr vorhanden (05.10.2026); bleibt, damit
        // ältere Releases weiter geprüft werden können
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhL5p9hf8FLp1PxhXQtPBWkvr4aDixpt4Hi02B/Rj4y7TWR/GEvqyQzAX0RllP3InWYlAbpbuZ0nL8+0aTxu9ig==",
        // Bisheriger Reserveschlüssel (erstellt 03.10.2026) – seit 0.9.10 der aktive Signaturschlüssel (GitHub-Umgebung „release“)
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBiULYz/NY5MJlWqaG0772s43Idzu0eMIioU+g2RVcuZsfnV2FC1rb0hGlOxuzEXaka9+jJN3tl8g0oYFcfeHPQ==",
        // Neuer Reserveschlüssel (erstellt 05.10.2026, offline beim Projektinhaber)
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDeSE2Mml1wdfa8BxmvEwCXZlw3w6Ytkysf6u4viUAl0aFHzOBJyEVCkq1uFSgaCZRmmJVaQTLuudEebB3K5rIg==",
    ];

    /// <summary>true, wenn <paramref name="signatureText"/> (Base64) eine gültige Signatur über <paramref name="sums"/> ist.</summary>
    public static bool Verify(byte[] sums, string signatureText, IReadOnlyList<string>? trustedKeys = null)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText.Trim()); }
        catch (FormatException) { return false; }
        foreach (var key in trustedKeys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(sums, signature, HashAlgorithmName.SHA256)) return true;
            }
            catch (CryptographicException) { /* nächsten Schlüssel versuchen */ }
        }
        return false;
    }

    /// <summary>Signieren (Release-Workflow, Tests): privater Schlüssel als PKCS#8 in Base64.</summary>
    public static string Sign(byte[] sums, string privateKeyPkcs8)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8.Trim()), out _);
        return Convert.ToBase64String(ecdsa.SignData(sums, HashAlgorithmName.SHA256));
    }

    internal static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
