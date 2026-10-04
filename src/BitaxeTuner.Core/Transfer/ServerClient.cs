using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

/// <summary>Update-Stand des Servers: installierte und neueste Version, ob er sich selbst aktualisieren kann.</summary>
public sealed record ServerUpdateStatus(string Current, string? Latest, bool CanInstall, string Kind);

public sealed record ServerInfo(string Name, string Version, int ApiVersion, bool SetupRequired, string Role, bool Paused, int Devices, string? Os);

/// <summary>Fehler vom Server mit Klartext aus der API ({"error": …}).</summary>
public sealed class ServerException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Zugriff der Desktop-App auf einen BitaxeTuner-Server: Verbindungstest, Anmeldung per API-Token,
/// Datenübertragung, Pausieren. Bei HTTPS mit selbst signiertem Zertifikat gilt nur der bestätigte Fingerabdruck.
/// </summary>
public sealed class ServerClient : IDisposable
{
    public const int SupportedApiVersion = 1;
    public const int DefaultPort = 8484;

    private readonly HttpClient _http;
    private readonly string? _pinned;

    public ServerClient(string url, string? token, string? pinnedFingerprint = null, TimeSpan? timeout = null)
    {
        BaseUri = Normalize(url);
        _pinned = pinnedFingerprint;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
        };
        _http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner-Desktop");
        // Fehlermeldungen des Servers in der Sprache der App
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(I18n.Loc.Current.Language);
        if (!string.IsNullOrWhiteSpace(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>Für Tests: eigener HTTP-Handler (z. B. In-Memory-Testserver).</summary>
    internal ServerClient(HttpClient http, string token)
    {
        _http = http;
        BaseUri = http.BaseAddress ?? new Uri("http://localhost/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Uri BaseUri { get; }

    /// <summary>Fingerabdruck des zuletzt vorgelegten Zertifikats (zum Bestätigen beim ersten Verbinden).</summary>
    public string? PresentedFingerprint { get; private set; }

    /// <summary>"192.168.1.5" → http://192.168.1.5:8484/</summary>
    public static Uri Normalize(string url)
    {
        url = url.Trim();
        if (url.Length == 0) throw new ServerException(L.T("Server-Adresse fehlt."));
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ServerException(L.T("Ungültige Server-Adresse: {0}", url));
        var b = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" };
        if (uri.IsDefaultPort && !url.Contains($":{uri.Port}")) b.Port = DefaultPort;
        return b.Uri;
    }

    public static string Fingerprint(X509Certificate cert) =>
        string.Join(":", Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).Chunk(2).Select(c => new string(c)));

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        PresentedFingerprint = Fingerprint(cert);
        if (errors == SslPolicyErrors.None) return true;
        return _pinned is not null && string.Equals(_pinned, PresentedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ServerInfo> InfoAsync(CancellationToken ct = default)
    {
        var e = await GetJsonAsync("api/v1/info", ct);
        return new ServerInfo(
            e.GetProperty("name").GetString() ?? "",
            e.GetProperty("version").GetString() ?? "",
            e.GetProperty("apiVersion").GetInt32(),
            e.GetProperty("setupRequired").GetBoolean(),
            e.GetProperty("role").GetString() ?? "None",
            e.TryGetProperty("paused", out var p) && p.GetBoolean(),
            e.TryGetProperty("devices", out var d) ? d.GetInt32() : 0,
            e.TryGetProperty("os", out var os) ? os.GetString() : null);
    }

    public Task<JsonElement> StatusAsync(CancellationToken ct = default) => GetJsonAsync("api/v1/status", ct);

    /// <summary>Browser-Sitzung für die eingebettete Oberfläche aus dem API-Token erzeugen.</summary>
    public async Task<(string SessionId, string Csrf, DateTime ExpiresUtc)> CreateSessionAsync(CancellationToken ct = default)
    {
        var e = await SendAsync(HttpMethod.Post, "api/v1/token-session", JsonContent.Create(new { }), ct);
        return (e.GetProperty("session").GetString()!, e.GetProperty("csrf").GetString()!, e.GetProperty("expiresUtc").GetDateTime());
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "api/v1/admin/pause", JsonContent.Create(new { paused }), ct);

    /// <summary>Datenarchiv des Servers herunterladen.</summary>
    public async Task DownloadExportAsync(Stream target, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/v1/admin/export", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(response, ct);
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        await s.CopyToAsync(target, ct);
    }

    /// <summary>Datenarchiv auf den Server hochladen. <paramref name="replace"/>: vorhandene Serverdaten ersetzen (Server sichert sie vorher).</summary>
    public async Task<string> ImportAsync(Stream archive, bool replace, CancellationToken ct = default)
    {
        var content = new StreamContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var e = await SendAsync(HttpMethod.Post, $"api/v1/admin/import?replace={(replace ? "true" : "false")}", content, ct, TimeSpan.FromMinutes(10));
        return e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
    }

    /// <summary>Verbindung des Servers: HTTPS an?, umschaltbar?, Fingerabdruck des Zertifikats (Audit S4).</summary>
    public async Task<(bool Enabled, bool Configurable, string? Fingerprint)> HttpsStatusAsync(CancellationToken ct = default)
    {
        var e = await GetJsonAsync("api/v1/admin/https", ct);
        return (e.GetProperty("enabled").GetBoolean(), e.GetProperty("configurable").GetBoolean(),
            e.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null);
    }

    /// <summary>
    /// Server auf HTTPS umstellen. Liefert den Fingerabdruck seines Zertifikats – über die bisherige (ggf. unverschlüsselte)
    /// Verbindung, daher vor dem Festhalten vom Nutzer abgleichen lassen (Audit N-Sec3). Der Server startet danach neu (10–20 s).
    /// </summary>
    public async Task<string?> EnableHttpsAsync(CancellationToken ct = default)
    {
        var e = await SendAsync(HttpMethod.Post, "api/v1/admin/https", JsonContent.Create(new { enable = true }), ct);
        return e.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
    }

    /// <summary>Gleiche Adresse mit https statt http (Port bleibt).</summary>
    public static Uri ToHttps(Uri uri) => new UriBuilder(uri) { Scheme = "https", Port = uri.Port }.Uri;

    /// <summary>Update-Stand des Servers (nach einer frischen Prüfung bei GitHub).</summary>
    public async Task<ServerUpdateStatus> CheckServerUpdateAsync(CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Post, "api/v1/admin/update/check", JsonContent.Create(new { }), ct, TimeSpan.FromMinutes(1));
        var e = await GetJsonAsync("api/v1/admin/update", ct);
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new ServerUpdateStatus(Str(e, "current") ?? "", Str(e, "latest"),
            e.TryGetProperty("canInstall", out var ci) && ci.ValueKind == JsonValueKind.True, Str(e, "kind") ?? "");
    }

    /// <summary>
    /// Server-Update anstoßen. Ab 0.9.0 sichert der Server vorher selbst (Datenordner, USB/NAS) und meldet einen Fehler,
    /// ohne etwas zu installieren. Liefert die Meldung des Servers; danach startet er neu.
    /// </summary>
    public async Task<string> InstallServerUpdateAsync(CancellationToken ct = default)
    {
        var e = await SendAsync(HttpMethod.Post, "api/v1/admin/update/install", JsonContent.Create(new { }), ct, TimeSpan.FromMinutes(15));
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            await EnsureAsync(response, ct);
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (HttpRequestException ex) { throw Wrap(ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new ServerException(L.T("Zeitüberschreitung – Server nicht erreichbar."), null, ex); }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await (timeout is null ? _http.SendAsync(request, ct) : SendLongAsync(request, cts.Token));
            await EnsureAsync(response, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (HttpRequestException ex) { throw Wrap(ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new ServerException(L.T("Zeitüberschreitung beim Server."), null, ex); }
    }

    // Lange Übertragungen (history.db) ohne das kurze Standard-Timeout
    private async Task<HttpResponseMessage> SendLongAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var longClient = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
        }) { BaseAddress = BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        longClient.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
        var response = await longClient.SendAsync(request, ct);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private ServerException Wrap(HttpRequestException ex) =>
        PresentedFingerprint is not null && ex.InnerException is System.Security.Authentication.AuthenticationException
            ? new ServerException(L.T("Zertifikat nicht bestätigt (Fingerabdruck {0}).", PresentedFingerprint), null, ex)
            : new ServerException(L.T("Server nicht erreichbar: ") + (ex.InnerException?.Message ?? ex.Message), ex.StatusCode, ex);

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var e = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            message = e.TryGetProperty("error", out var err) ? err.GetString() : null;
        }
        catch { /* kein JSON */ }
        message ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => L.T("Token ungültig oder widerrufen."),
            HttpStatusCode.Forbidden => L.T("Zugriff verweigert (nur Heimnetz/VPN oder fehlende Rechte)."),
            _ => L.T("Server antwortet mit {0}.", (int)response.StatusCode),
        };
        throw new ServerException(message, response.StatusCode);
    }

    /// <summary>BitaxeTuner-Server im lokalen Netz suchen (Port 8484 in allen privaten /24-Netzen dieses PCs).</summary>
    public static async Task<List<(Uri Uri, ServerInfo Info)>> DiscoverAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var candidates = Discovery.NetworkScanner.LocalIPv4Addresses()
            .Where(ip => Web.WebViewServer.IsPrivate(ip))
            .SelectMany(ip => { var b = ip.GetAddressBytes(); return Enumerable.Range(1, 254).Select(i => $"{b[0]}.{b[1]}.{b[2]}.{i}"); })
            .Distinct().ToList();
        var found = new List<(Uri, ServerInfo)>();
        var done = 0;
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(candidates.Select(async host =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var client = new ServerClient(host, null, null, TimeSpan.FromMilliseconds(800));
                var info = await client.InfoAsync(ct);
                if (info.Name == "BitaxeTuner-Server") lock (found) found.Add((client.BaseUri, info));
            }
            catch { /* kein Server */ }
            finally
            {
                gate.Release();
                progress?.Report((double)Interlocked.Increment(ref done) / candidates.Count);
            }
        }));
        return found.OrderBy(f => f.Item1.ToString()).ToList();
    }

    public void Dispose() => _http.Dispose();
}
