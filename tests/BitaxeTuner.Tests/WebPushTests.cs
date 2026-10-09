using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.12 Web-Push: Verschlüsselung (RFC 8291) wird wie im Browser entschlüsselt, VAPID-Token (RFC 8292) geprüft.</summary>
public class WebPushTests
{
    /// <summary>Browser-Seite: Schlüsselpaar und Auth-Geheimnis wie bei PushManager.subscribe().</summary>
    private sealed class Browser : IDisposable
    {
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public byte[] Auth { get; } = RandomNumberGenerator.GetBytes(16);
        public byte[] Public => [0x04, .. Key.ExportParameters(false).Q.X!, .. Key.ExportParameters(false).Q.Y!];
        public string P256dh => WebPush.B64(Public);
        public string AuthB64 => WebPush.B64(Auth);

        /// <summary>Entschlüsseln wie der Browser (unabhängig nachgebaut nach RFC 8291/8188).</summary>
        public byte[] Decrypt(byte[] body)
        {
            var salt = body[..16];
            Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)));
            int idlen = body[20];
            var asPublic = body[21..(21 + idlen)];
            var cipher = body[(21 + idlen)..];

            using var server = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
            });
            var ecdh = Key.DeriveRawSecretAgreement(server.PublicKey);
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, Auth, [.. "WebPush: info\0"u8, .. Public, .. asPublic]);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
            var plain = new byte[cipher.Length - 16];
            using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
            Assert.Equal(0x02, plain[^1]);                    // letzter Datensatz
            return plain[..^1];
        }

        public void Dispose() => Key.Dispose();
    }

    [Fact]
    public void Encrypted_message_is_decrypted_by_the_browser()
    {
        using var browser = new Browser();
        var text = """{"title":"Miner offline","body":"Gamma – seit 5 min"}""";
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(text), browser.P256dh, browser.AuthB64);
        Assert.Equal(text, Encoding.UTF8.GetString(browser.Decrypt(body)));

        // jede Nachricht mit neuem Salz und neuem Server-Schlüssel
        Assert.NotEqual(body[..86], WebPush.Encrypt(Encoding.UTF8.GetBytes(text), browser.P256dh, browser.AuthB64)[..86]);
    }

    [Fact]
    public void Wrong_browser_keys_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt([1], WebPush.B64(new byte[65]), WebPush.B64(new byte[16])));
        using var browser = new Browser();
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt([1], browser.P256dh, WebPush.B64(new byte[8])));
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[5000], browser.P256dh, browser.AuthB64));
    }

    [Fact]
    public void Vapid_header_is_signed_for_the_push_service_origin()
    {
        var pem = WebPush.NewVapidKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var header = WebPush.VapidHeader("https://push.example/send/abc?x=1", pem, now);

        var m = System.Text.RegularExpressions.Regex.Match(header, "^vapid t=([^.]+)\\.([^.]+)\\.([^,]+), k=(.+)$");
        Assert.True(m.Success, header);
        Assert.Equal(WebPush.PublicKey(pem), m.Groups[4].Value);
        var claims = JsonDocument.Parse(WebPush.FromB64(m.Groups[2].Value)).RootElement;
        Assert.Equal("https://push.example", claims.GetProperty("aud").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
        Assert.StartsWith("https://", claims.GetProperty("sub").GetString());

        var pub = WebPush.FromB64(m.Groups[4].Value);
        using var verify = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
        Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes(m.Groups[1].Value + "." + m.Groups[2].Value),
            WebPush.FromB64(m.Groups[3].Value), HashAlgorithmName.SHA256));
    }

    private sealed class PushService(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add((request, await request.Content!.ReadAsByteArrayAsync(ct)));
            return new HttpResponseMessage(status);
        }
    }

    private static (NotificationService Service, PushService Push, NotificationSettings Settings) Create(Browser browser, HttpStatusCode status)
    {
        var settings = new NotificationSettings
        {
            Targets = [new PushTarget
            {
                Id = "wp1", Name = "Handy", Provider = "webpush", Enabled = true,
                WebPushEndpoint = "https://push.example/send/abc", WebPushP256dh = browser.P256dh, WebPushAuth = browser.AuthB64,
                Categories = [nameof(NotifyCategory.Offline)],
            }],
        };
        var push = new PushService(status);
        var pem = WebPush.NewVapidKey();
        return (new NotificationService(() => settings, push) { VapidKey = () => pem }, push, settings);
    }

    [Fact]
    public async Task Notification_reaches_the_browser_as_encrypted_push()
    {
        using var browser = new Browser();
        var (service, push, _) = Create(browser, HttpStatusCode.Created);
        await service.SendAsync("offline:a", "Gamma offline", "seit 5 min", NotifyPriority.High, category: NotifyCategory.Offline, host: "h");

        var (req, body) = Assert.Single(push.Sent);
        Assert.Equal("aes128gcm", Assert.Single(req.Content!.Headers.ContentEncoding));
        Assert.Equal("high", req.Headers.GetValues("Urgency").Single());
        Assert.StartsWith("vapid t=", req.Headers.GetValues("Authorization").Single());
        var msg = JsonDocument.Parse(browser.Decrypt(body)).RootElement;
        Assert.Equal("Gamma offline", msg.GetProperty("title").GetString());
        Assert.Equal("seit 5 min", msg.GetProperty("body").GetString());
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task Ended_subscription_is_reported_so_the_target_can_be_switched_off()
    {
        using var browser = new Browser();
        var (service, _, settings) = Create(browser, HttpStatusCode.Gone);
        string? gone = null;
        service.WebPushGone += id => gone = id;
        Assert.NotNull(await service.TestAsync(settings.Targets[0]));
        Assert.Equal("wp1", gone);
    }

    [Fact]
    public void Webpush_targets_are_accepted_but_not_mirrored_to_the_old_single_setting()
    {
        using var browser = new Browser();
        var s = new NotificationSettings
        {
            Targets =
            [
                new PushTarget { Provider = "webpush", Enabled = true, WebPushEndpoint = " https://push.example/x ", WebPushP256dh = browser.P256dh, WebPushAuth = browser.AuthB64 },
                new PushTarget { Provider = "ntfy", Enabled = true, NtfyTopic = "t" },
            ],
        };
        s.ApplyTargets([]);
        Assert.Equal("https://push.example/x", s.Targets[0].WebPushEndpoint);
        s.SyncLegacyFromTargets();
        Assert.Equal("ntfy", s.Provider);

        s.Targets.RemoveAt(1);
        s.SyncLegacyFromTargets();
        Assert.Equal("none", s.Provider);
    }
}
