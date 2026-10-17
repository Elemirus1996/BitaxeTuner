using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Backup;

/// <summary>0.9.23: Speicher im Netz (WebDAV oder S3) – nur Dateien ablegen, holen, auflisten, löschen.</summary>
public interface IRemoteStore
{
    string Name { get; }
    /// <summary>Datei vollständig ablegen (erst danach unter <paramref name="name"/> sichtbar).</summary>
    Task PutAsync(string localFile, string name, CancellationToken ct);
    Task GetAsync(string name, Stream target, CancellationToken ct);
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>
/// 0.9.23 Sicherung ins Netz: vor dem Hochladen mit eigenem Passwort verschlüsselt (<see cref="BackupCrypto"/>), danach wieder
/// heruntergeladen und gegen SHA-256 geprüft. Abgelegt als „bitaxetuner-backup-….zip.enc“.
/// </summary>
public sealed class EncryptedBackupTarget(IRemoteStore store, string password, string workDirectory) : IBackupTarget
{
    public string Name => store.Name;
    public IRemoteStore Store => store;

    public async Task UploadAsync(string localFile, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(L.T("Für die Sicherung ins Netz fehlt das Verschlüsselungs-Passwort."));
        Directory.CreateDirectory(workDirectory);
        var enc = Path.Combine(workDirectory, fileName + BackupCrypto.Extension + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                using var src = File.OpenRead(localFile);
                using var dst = File.Create(enc);
                BackupCrypto.Encrypt(src, dst, password);
            }, ct);
            byte[] expected;
            await using (var s = File.OpenRead(enc)) expected = await SHA256.HashDataAsync(s, ct);
            await store.PutAsync(enc, fileName + BackupCrypto.Extension, ct);
            // Prüfung nach dem Hochladen: Datei wieder holen und vergleichen
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var hashing = new HashingStream(sha)) await store.GetAsync(fileName + BackupCrypto.Extension, hashing, ct);
            if (!CryptographicOperations.FixedTimeEquals(expected, sha.GetHashAndReset()))
                throw new IOException(L.T("Hochgeladene Sicherung weicht vom Original ab (SHA-256)."));
        }
        finally
        {
            try { File.Delete(enc); } catch { /* egal */ }
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public Task DeleteAsync(string fileName, CancellationToken ct)
    {
        if (!BackupNames.IsBackup(fileName)) throw new InvalidOperationException(L.T("Nur eigene Sicherungen werden gelöscht."));
        return store.DeleteAsync(fileName, ct);
    }

    /// <summary>Sicherung holen und entschlüsseln (zum Einspielen).</summary>
    public async Task DownloadAsync(string fileName, string targetZip, CancellationToken ct)
    {
        if (!BackupNames.IsBackup(fileName) || !fileName.EndsWith(BackupCrypto.Extension, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("Sicherung nicht gefunden."));
        Directory.CreateDirectory(workDirectory);
        var enc = Path.Combine(workDirectory, fileName + ".download");
        try
        {
            await using (var f = File.Create(enc)) await store.GetAsync(fileName, f, ct);
            await Task.Run(() =>
            {
                using var src = File.OpenRead(enc);
                using var dst = File.Create(targetZip);
                BackupCrypto.Decrypt(src, dst, password);
            }, ct);
        }
        finally
        {
            try { File.Delete(enc); } catch { /* egal */ }
        }
    }

    /// <summary>Schreibt nichts, rechnet nur SHA-256 mit.</summary>
    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>WebDAV (Nextcloud, ownCloud, Synology, …): Ordner-Adresse, Benutzer und (App-)Passwort.</summary>
public sealed class WebDavStore : IRemoteStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _folder;

    public WebDavStore(string url, string user, string password, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
            throw new InvalidOperationException(L.T("WebDAV: vollständige Adresse des Ordners angeben (https://…)."));
        _folder = new Uri(u.AbsoluteUri.EndsWith('/') ? u.AbsoluteUri : u.AbsoluteUri + "/");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(30);
        if (user.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public string Name => $"WebDAV {_folder.Host}";

    private Uri FileUri(string name) => new(_folder, Uri.EscapeDataString(name));

    private static async Task Check(HttpResponseMessage r, string what)
    {
        if (r.IsSuccessStatusCode) return;
        throw new IOException(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? L.T("WebDAV: Anmeldung abgelehnt ({0}) – Benutzer und (App-)Passwort prüfen.", (int)r.StatusCode)
            : L.T("WebDAV: {0} fehlgeschlagen ({1}).", what, (int)r.StatusCode));
    }

    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        using var r = await _http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), _folder), ct);
        // 405 = gibt es schon
        if (r.StatusCode is not HttpStatusCode.MethodNotAllowed) await Check(r, L.T("Ordner anlegen"));
    }

    public async Task PutAsync(string localFile, string name, CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        var tmp = FileUri(name + ".part");
        await using (var src = File.OpenRead(localFile))
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, tmp) { Content = new StreamContent(src) };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var r = await _http.SendAsync(put, ct);
            await Check(r, L.T("Hochladen"));
        }
        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), tmp);
        move.Headers.Add("Destination", FileUri(name).AbsoluteUri);
        move.Headers.Add("Overwrite", "T");
        using var m = await _http.SendAsync(move, ct);
        await Check(m, L.T("Umbenennen"));
    }

    public async Task GetAsync(string name, Stream target, CancellationToken ct)
    {
        using var r = await _http.GetAsync(FileUri(name), HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(r, L.T("Herunterladen"));
        await r.Content.CopyToAsync(target, ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _folder)
        {
            Content = new StringContent("""<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:getcontentlength/></d:prop></d:propfind>""", Encoding.UTF8, "application/xml"),
        };
        req.Headers.Add("Depth", "1");
        using var r = await _http.SendAsync(req, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return [];
        await Check(r, L.T("Auflisten"));
        var xml = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        XNamespace d = "DAV:";
        return xml.Descendants(d + "href")
            .Select(x => Uri.UnescapeDataString(x.Value.TrimEnd('/').Split('/').Last()))
            .Where(n => n.Length > 0 && !_folder.AbsolutePath.TrimEnd('/').EndsWith("/" + n, StringComparison.Ordinal))
            .Distinct().ToList();
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var r = await _http.DeleteAsync(FileUri(name), ct);
        if (r.StatusCode != HttpStatusCode.NotFound) await Check(r, L.T("Löschen"));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// S3-kompatibel (Backblaze B2, MinIO, Wasabi, AWS): Pfad-Adressierung „Endpunkt/Bucket/Präfix/Datei“, Signatur AWS SigV4.
/// </summary>
public sealed class S3Store : IRemoteStore, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _bucket, _prefix, _region, _accessKey, _secret;
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public S3Store(string endpoint, string region, string bucket, string prefix, string accessKey, string secret, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate((endpoint ?? "").Trim(), UriKind.Absolute, out var e) || e.Scheme is not ("https" or "http"))
            throw new InvalidOperationException(L.T("S3: Endpunkt angeben (z. B. https://s3.eu-central-003.backblazeb2.com)."));
        if (string.IsNullOrWhiteSpace(bucket)) throw new InvalidOperationException(L.T("S3: Bucket angeben."));
        _endpoint = new Uri(e.GetLeftPart(UriPartial.Authority));
        _bucket = bucket.Trim();
        _prefix = (prefix ?? "").Trim().Trim('/');
        _region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim();
        _accessKey = (accessKey ?? "").Trim();
        _secret = secret ?? "";
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public string Name => $"S3 {_bucket}";

    private string Key(string name) => _prefix.Length > 0 ? $"{_prefix}/{name}" : name;

    private string PathOf(string? key) => "/" + S3Signer.Encode(_bucket) + (key is null ? "/" : "/" + string.Join('/', key.Split('/').Select(S3Signer.Encode)));

    private HttpRequestMessage Request(HttpMethod method, string? key, IReadOnlyDictionary<string, string>? query, string payloadHash)
    {
        var path = PathOf(key);
        var q = query is null ? "" : S3Signer.CanonicalQuery(query);
        var req = new HttpRequestMessage(method, new Uri(_endpoint, path + (q.Length > 0 ? "?" + q : "")));
        var host = _endpoint.IsDefaultPort ? _endpoint.Host : $"{_endpoint.Host}:{_endpoint.Port}";
        var headers = S3Signer.Sign(method.Method, host, path, query ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            payloadHash, _accessKey, _secret, _region, Clock());
        foreach (var (k, v) in headers)
            if (k != "host") req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    private static async Task Check(HttpResponseMessage r, string what)
    {
        if (r.IsSuccessStatusCode) return;
        var body = await r.Content.ReadAsStringAsync();
        var code = System.Text.RegularExpressions.Regex.Match(body, "<Code>([^<]+)</Code>").Groups[1].Value;
        throw new IOException(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? L.T("S3: Zugriff abgelehnt ({0}) – Schlüssel, Region und Bucket prüfen.", code.Length > 0 ? code : ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture))
            : L.T("S3: {0} fehlgeschlagen ({1}).", what, code.Length > 0 ? code : ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture)));
    }

    public async Task PutAsync(string localFile, string name, CancellationToken ct)
    {
        string hash;
        await using (var s = File.OpenRead(localFile)) hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
        await using var src = File.OpenRead(localFile);
        using var req = Request(HttpMethod.Put, Key(name), null, hash);
        req.Content = new StreamContent(src);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = src.Length;
        using var r = await _http.SendAsync(req, ct);
        await Check(r, L.T("Hochladen"));
    }

    public async Task GetAsync(string name, Stream target, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, Key(name), null, S3Signer.EmptyHash);
        using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(r, L.T("Herunterladen"));
        await r.Content.CopyToAsync(target, ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var names = new List<string>();
        string? token = null;
        var prefix = _prefix.Length > 0 ? _prefix + "/" : "";
        do
        {
            var q = new Dictionary<string, string> { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null) q["continuation-token"] = token;
            using var req = Request(HttpMethod.Get, null, q, S3Signer.EmptyHash);
            using var r = await _http.SendAsync(req, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return [];
            await Check(r, L.T("Auflisten"));
            var xml = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
            names.AddRange(xml.Descendants(ns + "Key").Select(k => k.Value[prefix.Length..]).Where(n => n.Length > 0 && !n.Contains('/')));
            token = xml.Descendants(ns + "IsTruncated").FirstOrDefault()?.Value == "true"
                ? xml.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value : null;
        } while (token is not null);
        return names;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, Key(name), null, S3Signer.EmptyHash);
        using var r = await _http.SendAsync(req, ct);
        await Check(r, L.T("Löschen"));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>AWS Signature Version 4 für S3 (nur die benötigten Teile).</summary>
public static class S3Signer
{
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>RFC 3986: alles außer A–Z a–z 0–9 - _ . ~ wird kodiert (Großbuchstaben-Hex).</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string CanonicalQuery(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query.Select(kv => (Encode(kv.Key), Encode(kv.Value))).OrderBy(x => x.Item1, StringComparer.Ordinal).Select(x => $"{x.Item1}={x.Item2}"));

    /// <summary>Header für die Anfrage (host, x-amz-date, x-amz-content-sha256, Authorization und die übergebenen).</summary>
    public static Dictionary<string, string> Sign(string method, string host, string path, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> extraHeaders, string payloadHash, string accessKey, string secret, string region, DateTime utc)
    {
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        foreach (var (k, v) in extraHeaders) headers[k.ToLowerInvariant()] = v.Trim();
        var signed = string.Join(';', headers.Keys);
        var canonical = string.Join('\n', method, path, CanonicalQuery(query),
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n")), signed, payloadHash);
        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date), region), "s3"), "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        var result = new Dictionary<string, string>(headers)
        {
            ["Authorization"] = $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signed}, Signature={signature}",
        };
        return result;
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>Ziel mit ungültigen Einstellungen: meldet den Fehler bei jeder Sicherung, statt sie ganz zu verhindern.</summary>
public sealed class FailedBackupTarget(string name, string error) : IBackupTarget
{
    public string Name { get; } = name;
    public Task UploadAsync(string localFile, string fileName, CancellationToken ct) => throw new InvalidOperationException(error);
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => throw new InvalidOperationException(error);
    public Task DeleteAsync(string fileName, CancellationToken ct) => throw new InvalidOperationException(error);
}
