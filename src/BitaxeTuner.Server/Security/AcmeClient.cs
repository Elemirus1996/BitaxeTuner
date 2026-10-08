using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Security;

/// <summary>
/// Schlanker ACME-Client (RFC 8555) für Let's Encrypt mit DNS-01 – ohne Fremdbibliothek. Kontoschlüssel ECDSA P-256
/// (ES256), Zertifikatsschlüssel ECDSA P-256. Ablauf: Konto → Auftrag → TXT-Eintrag setzen (Rückruf) → Challenge →
/// CSR → Zertifikat. Fehler kommen als <see cref="AcmeException"/> mit der Meldung des Servers.
/// </summary>
public sealed class AcmeClient : IDisposable
{
    public const string Production = "https://acme-v02.api.letsencrypt.org/directory";
    public const string Staging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    private readonly HttpClient _http;
    private readonly ECDsa _accountKey;
    private readonly string _directoryUrl;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private JsonObject? _directory;
    private string? _nonce;
    private string? _kid;

    /// <param name="accountKeyPem">Vorhandener Kontoschlüssel (PKCS#8-PEM) oder null für einen neuen.</param>
    public AcmeClient(string directoryUrl, string? accountKeyPem, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _directoryUrl = directoryUrl;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner-ACME/1.0");
        _accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (accountKeyPem is { Length: > 0 }) _accountKey.ImportFromPem(accountKeyPem);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Kontoschlüssel zum Aufbewahren (secrets.json), damit Erneuerungen dasselbe Konto nutzen.</summary>
    public string AccountKeyPem => _accountKey.ExportPkcs8PrivateKeyPem();

    /// <summary>Ergebnis: Zertifikat mit privatem Schlüssel (für Kestrel) und Ablaufdatum.</summary>
    public sealed record Issued(X509Certificate2 Certificate, DateTime NotAfter);

    /// <summary>
    /// Zertifikat für <paramref name="host"/> holen. <paramref name="setTxt"/> setzt den TXT-Wert für
    /// _acme-challenge.&lt;host&gt; und wartet, bis er im DNS sichtbar ist; <paramref name="clearTxt"/> räumt danach auf.
    /// </summary>
    public async Task<Issued> IssueAsync(string host, string? email, Func<string, CancellationToken, Task> setTxt,
        Func<CancellationToken, Task> clearTxt, Action<string>? progress = null, CancellationToken ct = default)
    {
        await LoadDirectoryAsync(ct);
        progress?.Invoke(L.T("Let's-Encrypt-Konto …"));
        await EnsureAccountAsync(email, ct);

        progress?.Invoke(L.T("Auftrag für {0} …", host));
        var (orderUrl, order) = await PostAsync(Url("newOrder"), new JsonObject
        {
            ["identifiers"] = new JsonArray(new JsonObject { ["type"] = "dns", ["value"] = host }),
        }, ct);
        if (orderUrl is null) throw new AcmeException(L.T("Let's Encrypt hat keine Auftragsadresse geliefert."));

        try
        {
            foreach (var authzUrl in (order["authorizations"] as JsonArray ?? []).Select(a => a!.GetValue<string>()))
            {
                var (_, authz) = await PostAsync(authzUrl, null, ct);
                if (Str(authz, "status") == "valid") continue;
                var challenge = (authz["challenges"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => Str(c, "type") == "dns-01")
                                ?? throw new AcmeException(L.T("Let's Encrypt bietet keine DNS-Bestätigung an."));
                var keyAuth = Str(challenge, "token") + "." + Thumbprint();
                var txt = B64(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuth)));

                progress?.Invoke(L.T("TXT-Eintrag bei DuckDNS setzen und auf das DNS warten …"));
                await setTxt(txt, ct);
                progress?.Invoke(L.T("Let's Encrypt prüft den Eintrag …"));
                await PostAsync(Str(challenge, "url"), new JsonObject(), ct);
                await PollAsync(authzUrl, "valid", ct);
            }

            progress?.Invoke(L.T("Zertifikat ausstellen …"));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build());
            await PostAsync(Str(order, "finalize"), new JsonObject { ["csr"] = B64(request.CreateSigningRequest()) }, ct);
            var done = await PollAsync(orderUrl, "valid", ct);

            var pem = await PostForTextAsync(Str(done, "certificate"), ct);
            using var withKey = X509Certificate2.CreateFromPem(pem, key.ExportPkcs8PrivateKeyPem());
            // Unter Windows braucht Kestrel einen gespeicherten Schlüssel – Umweg über PFX
            var cert = new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
            if (!cert.MatchesHostname(host)) throw new AcmeException(L.T("Das Zertifikat passt nicht zu {0}.", host));
            return new Issued(cert, cert.NotAfter);
        }
        finally
        {
            try { await clearTxt(CancellationToken.None); } catch { /* aufräumen ist nicht kritisch */ }
        }
    }

    // ---------- Protokoll ----------

    private async Task LoadDirectoryAsync(CancellationToken ct)
    {
        if (_directory is not null) return;
        using var resp = await _http.GetAsync(_directoryUrl, ct);
        if (!resp.IsSuccessStatusCode) throw new AcmeException(L.T("Let's Encrypt nicht erreichbar (HTTP {0}).", (int)resp.StatusCode));
        _directory = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject
                     ?? throw new AcmeException(L.T("Unerwartete Antwort von Let's Encrypt."));
    }

    private string Url(string name) => _directory?[name]?.GetValue<string>() ?? throw new AcmeException(L.T("Unerwartete Antwort von Let's Encrypt."));

    private async Task EnsureAccountAsync(string? email, CancellationToken ct)
    {
        if (_kid is not null) return;
        var payload = new JsonObject { ["termsOfServiceAgreed"] = true };
        if (email is { Length: > 3 } && email.Contains('@')) payload["contact"] = new JsonArray("mailto:" + email.Trim());
        var (location, _) = await PostAsync(Url("newAccount"), payload, ct, useJwk: true);
        _kid = location ?? throw new AcmeException(L.T("Let's Encrypt hat kein Konto angelegt."));
    }

    private async Task<string> NonceAsync(CancellationToken ct)
    {
        if (_nonce is { } n) { _nonce = null; return n; }
        using var req = new HttpRequestMessage(HttpMethod.Head, Url("newNonce"));
        using var resp = await _http.SendAsync(req, ct);
        return resp.Headers.TryGetValues("Replay-Nonce", out var v) ? v.First() : throw new AcmeException(L.T("Unerwartete Antwort von Let's Encrypt."));
    }

    /// <summary>Signierter POST; <paramref name="payload"/> null = POST-as-GET. Liefert Location und Antwort.</summary>
    private async Task<(string? Location, JsonObject Body)> PostAsync(string url, JsonObject? payload, CancellationToken ct, bool useJwk = false)
    {
        var (location, text) = await SendSignedAsync(url, payload, "application/json", ct, useJwk);
        return (location, JsonNode.Parse(text.Length > 0 ? text : "{}") as JsonObject ?? new JsonObject());
    }

    private async Task<string> PostForTextAsync(string url, CancellationToken ct) =>
        (await SendSignedAsync(url, null, "application/pem-certificate-chain", ct, false)).Text;

    private async Task<(string? Location, string Text)> SendSignedAsync(string url, JsonObject? payload, string accept, CancellationToken ct, bool useJwk)
    {
        for (var attempt = 0; ; attempt++)
        {
            var protectedHeader = new JsonObject { ["alg"] = "ES256", ["nonce"] = await NonceAsync(ct), ["url"] = url };
            if (useJwk) protectedHeader["jwk"] = Jwk(); else protectedHeader["kid"] = _kid;
            var p = B64(Encoding.UTF8.GetBytes(protectedHeader.ToJsonString()));
            var body = payload is null ? "" : B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            var signature = B64(_accountKey.SignData(Encoding.ASCII.GetBytes(p + "." + body), HashAlgorithmName.SHA256));
            var jws = new JsonObject { ["protected"] = p, ["payload"] = body, ["signature"] = signature }.ToJsonString();

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(jws, Encoding.UTF8) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            req.Headers.Accept.ParseAdd(accept);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("Replay-Nonce", out var nonces)) _nonce = nonces.First();
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return (resp.Headers.Location?.ToString(), text);

            var problem = Problem(text);
            // badNonce: einmal mit frischer Nonce wiederholen (RFC 8555 §6.5)
            if (attempt == 0 && problem.Type.EndsWith(":badNonce", StringComparison.Ordinal)) continue;
            throw new AcmeException(L.T("Let's Encrypt: {0}", problem.Detail.Length > 0 ? problem.Detail : $"HTTP {(int)resp.StatusCode}"));
        }
    }

    /// <summary>Auftrag/Bestätigung abfragen, bis der Status erreicht ist (höchstens ca. 3 Minuten).</summary>
    private async Task<JsonObject> PollAsync(string url, string wanted, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var (_, body) = await PostAsync(url, null, ct);
            var status = Str(body, "status");
            if (status == wanted) return body;
            if (status == "invalid")
            {
                var detail = (body["challenges"] as JsonArray)?.OfType<JsonObject>().Select(c => c["error"]?["detail"]?.GetValue<string>()).FirstOrDefault(d => d is not null)
                             ?? body["error"]?["detail"]?.GetValue<string>();
                throw new AcmeException(L.T("Let's Encrypt hat abgelehnt: {0}", detail ?? status));
            }
            await _delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new AcmeException(L.T("Let's Encrypt hat nicht rechtzeitig geantwortet."));
    }

    private JsonObject Jwk()
    {
        var p = _accountKey.ExportParameters(false);
        return new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = B64(p.Q.X!), ["y"] = B64(p.Q.Y!) };
    }

    /// <summary>JWK-Fingerabdruck (RFC 7638): Felder alphabetisch, ohne Leerzeichen.</summary>
    internal string Thumbprint()
    {
        var p = _accountKey.ExportParameters(false);
        var json = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{B64(p.Q.X!)}\",\"y\":\"{B64(p.Q.Y!)}\"}}";
        return B64(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static (string Type, string Detail) Problem(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            return (r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "", r.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "");
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string Str(JsonObject o, string name) => o[name]?.GetValue<string>() ?? "";

    internal static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _http.Dispose();
        _accountKey.Dispose();
    }
}

public sealed class AcmeException(string message) : Exception(message);
