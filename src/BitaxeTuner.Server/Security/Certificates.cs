using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BitaxeTuner.Server.Security;

/// <summary>
/// Selbst signiertes Zertifikat für optionales HTTPS (server-cert.pfx im Datenordner). Der SHA-256-Fingerabdruck
/// steht im Protokoll und wird in der Desktop-App beim Verbinden bestätigt.
/// </summary>
public static class Certificates
{
    public static X509Certificate2 LoadOrCreate(string dataDirectory)
    {
        // Neuinstallation mit HTTPS: beim ersten Start gibt es den Datenordner noch nicht (sonst Absturz beim Speichern)
        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, "server-cert.pfx");
        if (File.Exists(file))
        {
            var existing = new X509Certificate2(file, (string?)null, X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=BitaxeTuner-Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in Core.Discovery.NetworkScanner.LocalIPv4Addresses()) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        File.WriteAllBytes(file, cert.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new X509Certificate2(file, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b);
}
