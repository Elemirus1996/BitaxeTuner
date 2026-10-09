using System.Buffers.Binary;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// 0.9.12 Web-Push ohne Fremddienst und ohne Fremdbibliothek: Nachrichten an Browser bzw. die installierte App
/// (Service Worker). Verschlüsselung nach RFC 8291 (aes128gcm, ECDH P-256 + HKDF), Absender-Nachweis nach RFC 8292
/// (VAPID, ES256). Der Push-Dienst des Browsers (Google, Mozilla, Apple) sieht nur verschlüsselte Daten.
/// </summary>
public static class WebPush
{
    /// <summary>Kontakt im VAPID-Token (RFC 8292 verlangt mailto: oder https:).</summary>
    public const string Subject = "https://github.com/Elemirus1996/BitaxeTuner";

    /// <summary>Neuer VAPID-Schlüssel (PKCS#8-PEM, für secrets.json).</summary>
    public static string NewVapidKey()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return k.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>Öffentlicher VAPID-Schlüssel (65 Bytes, unkomprimiert, base64url) – „applicationServerKey“ im Browser.</summary>
    public static string PublicKey(string vapidPem)
    {
        using var k = ECDsa.Create();
        k.ImportFromPem(vapidPem);
        return B64(Uncompressed(k.ExportParameters(false).Q));
    }

    /// <summary>Nachricht verschlüsseln (RFC 8291). <paramref name="salt"/>/<paramref name="serverKey"/> nur für Tests fest vorgebbar.</summary>
    public static byte[] Encrypt(byte[] payload, string p256dh, string auth, byte[]? salt = null, ECDiffieHellman? serverKey = null)
    {
        var uaPublic = FromB64(p256dh);
        var authSecret = FromB64(auth);
        if (uaPublic.Length != 65 || uaPublic[0] != 4) throw new ArgumentException(L.T("Ungültiger Schlüssel des Browsers."));
        if (authSecret.Length < 16) throw new ArgumentException(L.T("Ungültiger Schlüssel des Browsers."));
        if (payload.Length > 3800) throw new ArgumentException(L.T("Nachricht zu lang."));

        using var asKey = serverKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = Uncompressed(asKey.ExportParameters(false).Q);
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info\0" || ua_public || as_public, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        salt ??= RandomNumberGenerator.GetBytes(16);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plain = Concat(payload, [0x02]);                       // letzter (einziger) Datensatz
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, plain, cipher, tag);

        // Kopf: salt (16) | rs (4, big endian) | idlen (1) | keyid = as_public (65)
        var header = new byte[16 + 4 + 1 + 65];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 4096);
        header[20] = 65;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>VAPID-Kopfzeile „vapid t=…, k=…“ für den Push-Dienst der Adresse.</summary>
    public static string VapidHeader(string endpoint, string vapidPem, DateTimeOffset now)
    {
        var uri = new Uri(endpoint);
        using var k = ECDsa.Create();
        k.ImportFromPem(vapidPem);
        var header = B64(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = $"{uri.Scheme}://{uri.Authority}",
            exp = now.AddHours(12).ToUnixTimeSeconds(),
            sub = Subject,
        }));
        var signature = B64(k.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256));
        return $"vapid t={header}.{claims}.{signature}, k={B64(Uncompressed(k.ExportParameters(false).Q))}";
    }

    /// <summary>Ergebnis eines Versands; <c>Gone</c> = Anmeldung im Browser beendet (404/410) – Ziel abschalten.</summary>
    public enum Result { Delivered, Gone, Failed }

    /// <summary>Verschlüsselte Nachricht an den Push-Dienst schicken.</summary>
    public static async Task<(Result Result, string? Error)> SendAsync(HttpClient http, string endpoint, string p256dh, string auth,
        string vapidPem, object message, bool urgent, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (Result.Failed, L.T("Ungültige Push-Adresse des Browsers."));
        var body = Encrypt(JsonSerializer.SerializeToUtf8Bytes(message), p256dh, auth);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("Authorization", VapidHeader(endpoint, vapidPem, DateTimeOffset.UtcNow));
        req.Headers.TryAddWithoutValidation("TTL", "86400");
        req.Headers.TryAddWithoutValidation("Urgency", urgent ? "high" : "normal");
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return (Result.Delivered, null);
        if ((int)resp.StatusCode is 404 or 410) return (Result.Gone, L.T("Der Browser hat die Anmeldung beendet."));
        return (Result.Failed, $"HTTP {(int)resp.StatusCode}");
    }

    private static byte[] Uncompressed(ECPoint q) => Concat([0x04], q.X!, q.Y!);

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(all, o); o += p.Length; }
        return all;
    }

    public static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
