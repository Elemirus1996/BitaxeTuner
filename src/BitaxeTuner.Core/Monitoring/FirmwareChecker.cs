using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BitaxeTuner.Core.Monitoring;

public enum FirmwareStatus
{
    Unknown,
    UpToDate,
    UpdateAvailable
}

public sealed record FirmwareInfo(FirmwareStatus Status, string? Latest, string? ReleaseUrl);

/// <summary>
/// Vergleicht die Firmware-Version eines Miners mit dem neuesten GitHub-Release
/// des eingestellten Repositorys. Ohne Token erlaubt GitHub 60 Anfragen pro
/// Stunde und IP, deshalb wird je Repository nur alle 6 Stunden abgefragt.
/// </summary>
public sealed partial class FirmwareChecker : IDisposable
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, (DateTime Fetched, string? Tag, string? Url)> _cache = new();

    public FirmwareChecker()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Neuestes Release aus dem Cache, ohne Netzwerkzugriff.</summary>
    public FirmwareInfo Evaluate(string? repo, string? installed)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(installed))
            return new FirmwareInfo(FirmwareStatus.Unknown, null, null);

        lock (_cache)
        {
            if (!_cache.TryGetValue(repo.Trim(), out var entry) || entry.Tag is null)
                return new FirmwareInfo(FirmwareStatus.Unknown, null, null);

            var a = ParseVersion(installed);
            var b = ParseVersion(entry.Tag);
            if (a is null || b is null) return new FirmwareInfo(FirmwareStatus.Unknown, entry.Tag, entry.Url);

            return b > a
                ? new FirmwareInfo(FirmwareStatus.UpdateAvailable, entry.Tag, entry.Url)
                : new FirmwareInfo(FirmwareStatus.UpToDate, entry.Tag, entry.Url);
        }
    }

    /// <summary>Aktualisiert den Cache für alle Repositories, deren Eintrag älter als 6 Stunden ist.</summary>
    public async Task RefreshAsync(IEnumerable<string> repos, CancellationToken ct = default)
    {
        foreach (var repo in repos.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct())
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(repo, out var e) && DateTime.UtcNow - e.Fetched < CacheTime) continue;
            }

            string? tag = null, url = null;
            try
            {
                using var resp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                }
            }
            catch
            {
                // Netzfehler: beim nächsten Durchlauf erneut versuchen
                continue;
            }

            lock (_cache) _cache[repo] = (DateTime.UtcNow, tag, url);
        }
    }

    /// <summary>"v2.4.1", "2.4.1-dev", "v2.4.1b" → 2.4.1</summary>
    public static Version? ParseVersion(string text)
    {
        var m = VersionRegex().Match(text.Trim());
        if (!m.Success) return null;

        var parts = m.Groups[1].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2]);
    }

    [GeneratedRegex(@"^[vV]?(\d+(?:\.\d+){0,2})")]
    private static partial Regex VersionRegex();

    public void Dispose() => _http.Dispose();
}
