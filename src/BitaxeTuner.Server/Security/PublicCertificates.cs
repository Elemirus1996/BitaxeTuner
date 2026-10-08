using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Server.Security;

/// <summary>Zustand von „HTTPS ohne Warnung“ für die Oberfläche.</summary>
public sealed record PublicHttpsStatus(bool Enabled, string Host, bool Staging, DateTime? NotAfter, DateTime? LastAttempt,
    string? LastError, string? Progress, bool Busy, string? DnsIp, DateTime? DnsUpdated, bool TokenSet);

/// <summary>
/// DuckDNS: A-Eintrag auf die Heimnetz-Adresse, TXT-Eintrag für die DNS-01-Bestätigung. Das Token geht nur an
/// duckdns.org (HTTPS); geprüft wird der TXT-Eintrag über DNS-over-HTTPS (Google, Rückfall Cloudflare).
/// </summary>
public sealed class DuckDnsClient(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner/1.0");
        return http;
    }

    private async Task UpdateAsync(string subdomain, string token, string query, CancellationToken ct)
    {
        var url = $"https://www.duckdns.org/update?domains={Uri.EscapeDataString(subdomain)}&token={Uri.EscapeDataString(token)}&{query}";
        var text = (await _http.GetStringAsync(url, ct)).Trim();
        if (!text.StartsWith("OK", StringComparison.Ordinal))
            throw new IOException(L.T("DuckDNS hat abgelehnt – Name und Token prüfen."));
    }

    public Task SetIpAsync(string subdomain, string token, string ip, CancellationToken ct) => UpdateAsync(subdomain, token, $"ip={Uri.EscapeDataString(ip)}", ct);

    public Task SetTxtAsync(string subdomain, string token, string txt, CancellationToken ct) => UpdateAsync(subdomain, token, $"txt={Uri.EscapeDataString(txt)}", ct);

    public Task ClearTxtAsync(string subdomain, string token, CancellationToken ct) => UpdateAsync(subdomain, token, "txt=removed&clear=true", ct);

    /// <summary>Ist der TXT-Wert öffentlich sichtbar? (DNS-over-HTTPS)</summary>
    public async Task<bool> TxtVisibleAsync(string name, string value, CancellationToken ct)
    {
        foreach (var url in new[] { $"https://dns.google/resolve?name={name}&type=TXT", $"https://cloudflare-dns.com/dns-query?name={name}&type=TXT" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array &&
                    answers.EnumerateArray().Any(a => a.TryGetProperty("data", out var d) && d.GetString()?.Trim('"') == value))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { /* nächster Resolver */ }
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 0.9.12 HTTPS ohne Warnung: holt und erneuert ein Let's-Encrypt-Zertifikat für &lt;name&gt;.duckdns.org (DNS-01) und
/// hält den DuckDNS-Eintrag auf der Heimnetz-Adresse. Kestrel liefert es nur aus, wenn der Browser genau diesen Namen
/// aufruft (SNI) – per IP oder localhost bleibt das selbst ausgestellte Zertifikat, damit bestehende Verbindungen der
/// Desktop-App (Fingerabdruck) weiter funktionieren. Fehler fallen nie auf HTTP oder ohne Zertifikat zurück.
/// </summary>
public sealed class PublicCertificates : BackgroundService
{
    public const string TokenKey = "duckdns.token";
    public const string AccountKey = "acme.account.key";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromHours(6);
    private static readonly TimeSpan DnsEvery = TimeSpan.FromHours(6);

    private readonly HubService _hub;
    private readonly ServerSettings _settings;
    private readonly ILogger<PublicCertificates> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _lock = new();
    private volatile X509Certificate2? _current;
    private volatile string _currentHost = "";
    private DateTime? _lastAttempt, _dnsUpdated;
    private string? _lastError, _progress, _dnsIp;
    private bool _force;

    public PublicCertificates(HubService hub, ServerSettings settings, ILogger<PublicCertificates> log)
    {
        _hub = hub;
        _settings = settings;
        _log = log;
        Load();
    }

    /// <summary>Für Tests: eigene HTTP-Behandlung (ACME, DuckDNS, DoH) und Wartezeiten.</summary>
    internal HttpMessageHandler? Handler { get; set; }
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    internal string? DirectoryOverride { get; set; }

    private string CertFile => Path.Combine(_settings.DataDirectory, "public-cert.pfx");
    private string StateFile => Path.Combine(_settings.DataDirectory, "public-cert.json");

    /// <summary>Für Kestrel: Zertifikat, wenn der aufgerufene Name passt, sonst null (→ selbst ausgestelltes).</summary>
    public X509Certificate2? For(string? serverName) =>
        _current is { } c && serverName is { Length: > 0 } n && string.Equals(n, _currentHost, StringComparison.OrdinalIgnoreCase) && c.NotAfter > DateTime.Now
            ? c : null;

    public X509Certificate2? Current => _current;

    private sealed record State(DateTime? LastAttempt, string? LastError, string? DnsIp, DateTime? DnsUpdated);

    private void Load()
    {
        try
        {
            if (File.Exists(CertFile))
            {
                var cert = new X509Certificate2(CertFile, (string?)null, X509KeyStorageFlags.Exportable);
                _current = cert;
                _currentHost = cert.GetNameInfo(X509NameType.DnsName, false);
            }
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is { } s)
                (_lastAttempt, _lastError, _dnsIp, _dnsUpdated) = (s.LastAttempt, s.LastError, s.DnsIp, s.DnsUpdated);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Öffentliches Zertifikat nicht lesbar: {Message}", ex.Message);
        }
    }

    private void SaveState()
    {
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(_lastAttempt, _lastError, _dnsIp, _dnsUpdated))); }
        catch { /* nicht kritisch */ }
    }

    public PublicHttpsStatus Status(PublicHttpsSettings s, bool tokenSet) => new(s.Enabled, s.HostName, s.Staging,
        _current is { } c && string.Equals(_currentHost, s.HostName, StringComparison.OrdinalIgnoreCase) ? c.NotAfter : null,
        _lastAttempt, _lastError, _progress, _busy.CurrentCount == 0, _dnsIp, _dnsUpdated, tokenSet);

    /// <summary>Gleich prüfen (nach dem Speichern oder per Knopf „Zertifikat jetzt holen“).</summary>
    public void RequestNow()
    {
        _force = true;
        try { _wake.Release(); } catch (SemaphoreFullException) { /* schon angestoßen */ }
    }

    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(DateTime.Now, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning("HTTPS ohne Warnung: {Message}", ex.Message); }
            try { await _wake.WaitAsync(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Ein Durchlauf: DuckDNS-Adresse aktualisieren, Zertifikat bei Bedarf holen.</summary>
    internal async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var (s, token, online) = await _hub.RunAsync(h => (Copy(h.Config.PublicHttps), h.Secrets.Get(TokenKey), h.Options.OnlineChecks));
        if (!online && Handler is null) return;                  // Tests/Offline-Betrieb: nie ins Internet
        var force = _force;
        _force = false;
        if (!s.Enabled || !PublicHttpsSettings.ValidSubdomain(s.Subdomain) || token is not { Length: > 0 }) return;
        if (!await _busy.WaitAsync(0, ct)) return;
        try
        {
            using var duck = new DuckDnsClient(Handler);
            var ip = s.Ip.Length > 0 ? s.Ip : Core.Discovery.NetworkScanner.LocalIPv4Addresses().FirstOrDefault()?.ToString();
            if (ip is not null && (force || ip != _dnsIp || _dnsUpdated is not { } d || now - d > DnsEvery))
            {
                try
                {
                    await duck.SetIpAsync(s.Subdomain, token, ip, ct);
                    if (ip != _dnsIp) await LogAsync(L.T("DuckDNS: {0} zeigt auf {1}.", s.HostName, ip));
                    (_dnsIp, _dnsUpdated) = (ip, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await FailAsync(now, L.T("DuckDNS-Adresse nicht aktualisiert: {0}", ex.Message));
                    return;
                }
            }

            var valid = _current is { } c && string.Equals(_currentHost, s.HostName, StringComparison.OrdinalIgnoreCase)
                        && c.NotAfter - now > RenewBefore && IsStaging(c) == s.Staging;
            if (valid && !force) return;
            if (!force && _lastError is not null && _lastAttempt is { } last && now - last < RetryAfterError) return;
            await IssueAsync(s, token, duck, now, ct);
        }
        finally
        {
            _progress = null;
            SaveState();
            _busy.Release();
        }
    }

    private async Task IssueAsync(PublicHttpsSettings s, string token, DuckDnsClient duck, DateTime now, CancellationToken ct)
    {
        _lastAttempt = now;
        var accountPem = await _hub.RunAsync(h => h.Secrets.Get(AccountKey + (s.Staging ? ".staging" : "")));
        using var acme = new AcmeClient(DirectoryOverride ?? (s.Staging ? AcmeClient.Staging : AcmeClient.Production), accountPem, Handler, Delay);
        try
        {
            var issued = await acme.IssueAsync(s.HostName, s.Email,
                async (txt, c) =>
                {
                    await duck.SetTxtAsync(s.Subdomain, token, txt, c);
                    // auf das öffentliche DNS warten (DuckDNS ist meist nach Sekunden sichtbar), danach kurze Reserve
                    for (var i = 0; i < 18 && !await duck.TxtVisibleAsync("_acme-challenge." + s.HostName, txt, c); i++)
                        await Delay(TimeSpan.FromSeconds(10), c);
                    await Delay(TimeSpan.FromSeconds(5), c);
                },
                c => duck.ClearTxtAsync(s.Subdomain, token, c),
                p => _progress = p, ct);
            await _hub.RunAsync(h => { h.Secrets.Set(AccountKey + (s.Staging ? ".staging" : ""), acme.AccountKeyPem); return true; });

            var tmp = CertFile + ".tmp";
            File.WriteAllBytes(tmp, issued.Certificate.Export(X509ContentType.Pfx));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, CertFile, overwrite: true);
            lock (_lock)
            {
                _current = issued.Certificate;
                _currentHost = s.HostName;
            }
            _lastError = null;
            await LogAsync(s.Staging
                ? L.T("Testzertifikat (Staging) für {0} geholt, gültig bis {1:d} – Browser zeigen dafür noch eine Warnung.", s.HostName, issued.NotAfter)
                : L.T("Zertifikat für {0} von Let's Encrypt geholt, gültig bis {1:d}.", s.HostName, issued.NotAfter));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(now, L.T("Zertifikat für {0} nicht geholt: {1}", s.HostName, ex.Message));
        }
    }

    private async Task FailAsync(DateTime now, string message)
    {
        _lastAttempt = now;
        _lastError = message;
        await LogAsync(message);
        await _hub.RunAsync(async h =>
        {
            if (h.Config.Notifications.Wants(NotifyCategory.Maintenance))
                await h.Notify.SendAsync("public-https", L.T("HTTPS-Zertifikat"), message, NotifyPriority.Normal, TimeSpan.FromDays(1), NotifyCategory.Maintenance);
        });
    }

    private Task LogAsync(string message)
    {
        _log.LogInformation("{Message}", message);
        return _hub.RunAsync(h => { h.LogEvent(null, EventCategories.System, message); return true; });
    }

    /// <summary>Zwischenzertifikate der Let's-Encrypt-Staging-Umgebung heißen „(STAGING) …“ (früher „Fake LE …“).</summary>
    private static bool IsStaging(X509Certificate2 c) => c.Issuer.Contains("STAGING", StringComparison.OrdinalIgnoreCase)
                                                         || c.Issuer.Contains("Fake LE", StringComparison.OrdinalIgnoreCase);

    private static PublicHttpsSettings Copy(PublicHttpsSettings s) => new()
    {
        Enabled = s.Enabled, Subdomain = s.Subdomain, Email = s.Email, Staging = s.Staging, Ip = s.Ip,
    };

    public override void Dispose()
    {
        base.Dispose();
        _busy.Dispose();
        _wake.Dispose();
    }
}
