using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Server;
using BitaxeTuner.Server.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BitaxeTuner.Tests;

/// <summary>
/// 0.9.12 HTTPS ohne Warnung: ACME (RFC 8555) gegen einen nachgebauten Let's-Encrypt-Server, der jede Signatur prüft,
/// dazu DuckDNS und DNS-over-HTTPS als Attrappen – ohne Internet.
/// </summary>
public sealed class PublicHttpsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    /// <summary>Nachgebauter ACME-Server mit DuckDNS und DoH.</summary>
    internal sealed class FakeAcme : HttpMessageHandler
    {
        private const string Base = "https://acme.test/";
        private readonly ECDsa _ca = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 _caCert;
        private int _nonce;
        private readonly HashSet<string> _nonces = [];
        public ECParameters? AccountKey;
        public string? Txt, Ip, DuckToken;
        public bool TxtCleared, ChallengeValid, ChallengeTried, RejectFirstNonce;
        public string? IssuedPem;
        public int Accounts, Orders;
        private string? _thumbprint;

        public FakeAcme()
        {
            var req = new CertificateRequest("CN=Test CA", _ca, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            _caCert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        }

        private HttpResponseMessage Json(HttpStatusCode code, object body, string? location = null)
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent(JsonSerializer.Serialize(body)) };
            r.Headers.Add("Replay-Nonce", NewNonce());
            if (location is not null) r.Headers.Location = new Uri(location);
            return r;
        }

        private string NewNonce()
        {
            var n = "n" + Interlocked.Increment(ref _nonce);
            lock (_nonces) _nonces.Add(n);
            return n;
        }

        private static byte[] B64(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://www.duckdns.org/update", StringComparison.Ordinal))
            {
                var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                if (q["token"] != DuckToken) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("KO") };
                if (q["clear"] == "true") TxtCleared = true;
                else if (q["txt"] is { } txt) Txt = txt;
                if (q["ip"] is { } ip) Ip = ip;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
            }
            if (url.StartsWith("https://dns.google/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { Answer = Txt is null ? Array.Empty<object>() : [new { data = $"\"{Txt}\"" }] })) };

            if (request.Method == HttpMethod.Get && url == Base + "directory")
                return Json(HttpStatusCode.OK, new { newNonce = Base + "nonce", newAccount = Base + "acct", newOrder = Base + "order" });
            if (request.Method == HttpMethod.Head)
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("Replay-Nonce", NewNonce());
                return r;
            }

            // Signierte POSTs prüfen wie Let's Encrypt
            Assert.Equal("application/jose+json", request.Content!.Headers.ContentType!.MediaType);
            var jws = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!;
            var prot = JsonNode.Parse(Encoding.UTF8.GetString(B64(jws["protected"]!.GetValue<string>())))!;
            Assert.Equal("ES256", prot["alg"]!.GetValue<string>());
            Assert.Equal(url, prot["url"]!.GetValue<string>());
            var nonce = prot["nonce"]!.GetValue<string>();
            bool known;
            lock (_nonces) known = _nonces.Remove(nonce);
            if (!known || RejectFirstNonce)
            {
                RejectFirstNonce = false;
                return Json(HttpStatusCode.BadRequest, new { type = "urn:ietf:params:acme:error:badNonce", detail = "bad nonce" });
            }
            if (prot["jwk"] is JsonObject jwk)
            {
                AccountKey = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = B64(jwk["x"]!.GetValue<string>()), Y = B64(jwk["y"]!.GetValue<string>()) } };
                var tp = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
                _thumbprint = AcmeClient.B64(SHA256.HashData(Encoding.UTF8.GetBytes(tp)));
            }
            else Assert.Equal(Base + "acct/1", prot["kid"]!.GetValue<string>());
            using (var verify = ECDsa.Create(AccountKey!.Value))
                Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes(jws["protected"]!.GetValue<string>() + "." + jws["payload"]!.GetValue<string>()),
                    B64(jws["signature"]!.GetValue<string>()), HashAlgorithmName.SHA256), "Signatur ungültig");
            var payloadText = jws["payload"]!.GetValue<string>();
            var payload = payloadText.Length == 0 ? null : JsonNode.Parse(Encoding.UTF8.GetString(B64(payloadText)));

            switch (url[Base.Length..])
            {
                case "acct":
                    Accounts++;
                    Assert.True(payload!["termsOfServiceAgreed"]!.GetValue<bool>());
                    return Json(HttpStatusCode.Created, new { status = "valid" }, Base + "acct/1");
                case "order":
                    Orders++;
                    return Json(HttpStatusCode.Created, new { status = "pending", authorizations = new[] { Base + "authz/1" }, finalize = Base + "finalize/1" }, Base + "order/1");
                case "authz/1":
                    return Json(HttpStatusCode.OK, new
                    {
                        status = ChallengeValid ? "valid" : ChallengeTried ? "invalid" : "pending",
                        challenges = new object[]
                        {
                            new { type = "http-01", url = Base + "chall/2", token = "x" },
                            ChallengeTried && !ChallengeValid
                                ? new { type = "dns-01", url = Base + "chall/1", token = "tok-123", error = new { detail = "Incorrect TXT record" } }
                                : (object)new { type = "dns-01", url = Base + "chall/1", token = "tok-123" },
                        },
                    });
                case "chall/1":
                    var expected = AcmeClient.B64(SHA256.HashData(Encoding.ASCII.GetBytes("tok-123." + _thumbprint)));
                    ChallengeValid = Txt == expected;
                    ChallengeTried = true;
                    return Json(HttpStatusCode.OK, new { status = ChallengeValid ? "valid" : "invalid" });
                case "finalize/1":
                    Assert.True(ChallengeValid);
                    var csr = CertificateRequest.LoadSigningRequest(B64(payload!["csr"]!.GetValue<string>()), HashAlgorithmName.SHA256,
                        CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
                    var leaf = csr.Create(_caCert, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(90), [1, 2, 3, 4]);
                    IssuedPem = leaf.ExportCertificatePem() + "\n" + _caCert.ExportCertificatePem() + "\n";
                    return Json(HttpStatusCode.OK, new { status = "processing" });
                case "order/1":
                    return Json(HttpStatusCode.OK, IssuedPem is null ? new { status = "pending" } : new { status = "valid", certificate = Base + "cert/1" });
                case "cert/1":
                    var c = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(IssuedPem!) };
                    c.Headers.Add("Replay-Nonce", NewNonce());
                    return c;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task Acme_issues_a_certificate_via_dns01_and_cleans_up()
    {
        var fake = new FakeAcme { DuckToken = "abc-token-1234567890abcd", RejectFirstNonce = true };
        using var duck = new DuckDnsClient(fake);
        using var acme = new AcmeClient("https://acme.test/directory", null, fake, NoDelay);
        var issued = await acme.IssueAsync("meinminer.duckdns.org", "a@example.org",
            (txt, ct) => duck.SetTxtAsync("meinminer", fake.DuckToken, txt, ct),
            ct => duck.ClearTxtAsync("meinminer", fake.DuckToken, ct));

        Assert.True(issued.Certificate.HasPrivateKey);
        Assert.True(issued.Certificate.MatchesHostname("meinminer.duckdns.org"));
        Assert.True(issued.NotAfter > DateTime.Now.AddDays(80));
        Assert.True(fake.TxtCleared);                                              // TXT wieder entfernt
        Assert.True(await duck.TxtVisibleAsync("_acme-challenge.meinminer.duckdns.org", fake.Txt!, default));

        // Erneuerung mit demselben Kontoschlüssel
        using var again = new AcmeClient("https://acme.test/directory", acme.AccountKeyPem, fake, NoDelay);
        Assert.Equal(acme.Thumbprint(), again.Thumbprint());
    }

    [Fact]
    public async Task Wrong_txt_is_reported_and_duckdns_rejects_a_wrong_token()
    {
        var fake = new FakeAcme { DuckToken = "abc-token-1234567890abcd" };
        using var acme = new AcmeClient("https://acme.test/directory", null, fake, NoDelay);
        var ex = await Assert.ThrowsAsync<AcmeException>(() => acme.IssueAsync("x.duckdns.org", null,
            (_, _) => { fake.Txt = "falsch"; return Task.CompletedTask; }, _ => Task.CompletedTask));
        Assert.Contains("Incorrect TXT record", ex.Message);

        using var duck = new DuckDnsClient(fake);
        await Assert.ThrowsAsync<IOException>(() => duck.SetIpAsync("x", "falsches-token-0000000000", "192.168.1.20", default));
    }

    [Fact]
    public async Task Service_keeps_duckdns_up_to_date_issues_once_and_serves_only_for_the_name()
    {
        var fake = new FakeAcme { DuckToken = "abc-token-1234567890abcd" };
        var settings = new ServerSettings { DataDirectory = _dir.Path };
        using var hub = new HubService(settings, NullLogger<HubService>.Instance, new MinerHubOptions { DataDirectory = _dir.Path, OnlineChecks = false });
        await hub.StartAsync(default);
        try
        {
            await hub.RunAsync(h =>
            {
                h.Config.PublicHttps = new PublicHttpsSettings { Enabled = true, Subdomain = "meinminer", Ip = "192.168.1.20" };
                h.Secrets.Set(PublicCertificates.TokenKey, fake.DuckToken);
                return true;
            });
            using var certs = new PublicCertificates(hub, settings, NullLogger<PublicCertificates>.Instance)
            {
                Handler = fake, Delay = NoDelay, DirectoryOverride = "https://acme.test/directory",
            };
            await certs.TickAsync(DateTime.Now, default);
            Assert.Equal("192.168.1.20", fake.Ip);
            Assert.NotNull(certs.For("MeinMiner.duckdns.org"));                     // Name passt (Groß/Klein egal)
            Assert.Null(certs.For("192.168.1.20"));                                 // per IP: selbst ausgestelltes
            Assert.Null(certs.For(null));
            Assert.True(File.Exists(Path.Combine(_dir.Path, "public-cert.pfx")));
            Assert.True(await hub.RunAsync(h => h.Secrets.Has(PublicCertificates.AccountKey)));

            await certs.TickAsync(DateTime.Now.AddDays(1), default);               // noch lange gültig → kein neuer Auftrag
            Assert.Equal(1, fake.Orders);
            await certs.TickAsync(DateTime.Now.AddDays(65), default);              // < 30 Tage Rest → erneuern
            Assert.Equal(2, fake.Orders);

            // Neustart: Zertifikat wird aus dem Datenordner geladen
            using var reloaded = new PublicCertificates(hub, settings, NullLogger<PublicCertificates>.Instance);
            Assert.NotNull(reloaded.For("meinminer.duckdns.org"));
            var status = await hub.RunAsync(h => reloaded.Status(h.Config.PublicHttps, true));
            Assert.Equal("meinminer.duckdns.org", status.Host);
            Assert.NotNull(status.NotAfter);
        }
        finally
        {
            await hub.StopAsync(default);
        }
    }
}
