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

/// <summary>0.9.25: Datei eines GitHub-Releases (Prüfsumme „sha256:…“ liefert GitHub seit 2025 mit, ältere Releases ohne).</summary>
public sealed record FirmwareAsset(string Name, long Size, string? Sha256, string DownloadUrl);

/// <summary>0.9.25: Ein Release mit seinen Dateien; <see cref="Prerelease"/> = Beta/Release-Candidate.</summary>
public sealed record FirmwareRelease(string Tag, string? Url, bool Prerelease, IReadOnlyList<FirmwareAsset> Assets);

/// <summary>
/// Vergleicht die Firmware-Version eines Miners mit dem neuesten GitHub-Release
/// des eingestellten Repositorys. Ohne Token erlaubt GitHub 60 Anfragen pro
/// Stunde und IP, deshalb wird je Repository nur alle 6 Stunden abgefragt.
/// 0.9.25: holt die letzten Releases samt Dateien (für das Firmware-Update) – weiterhin eine Anfrage je Repository.
/// </summary>
public sealed partial class FirmwareChecker : IDisposable
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, (DateTime Fetched, List<FirmwareRelease> Releases)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FirmwareChecker()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Neuestes Release aus dem Cache, ohne Netzwerkzugriff; <paramref name="beta"/> = auch Vorabversionen.</summary>
    public FirmwareInfo Evaluate(string? repo, string? installed, bool beta = false)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(installed))
            return new FirmwareInfo(FirmwareStatus.Unknown, null, null);

        var target = Target(repo, beta);
        if (target is null) return new FirmwareInfo(FirmwareStatus.Unknown, null, null);
        return IsNewer(target.Tag, installed) switch
        {
            null => new FirmwareInfo(FirmwareStatus.Unknown, target.Tag, target.Url),
            true => new FirmwareInfo(FirmwareStatus.UpdateAvailable, target.Tag, target.Url),
            false => new FirmwareInfo(FirmwareStatus.UpToDate, target.Tag, target.Url),
        };
    }

    /// <summary>
    /// 0.9.25: Release, auf das aktualisiert würde – das neueste offizielle; mit <paramref name="beta"/> eine neuere
    /// Vorabversion, falls es eine gibt. Null = nichts im Cache.
    /// </summary>
    public FirmwareRelease? Target(string? repo, bool beta = false)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;
        lock (_cache)
        {
            if (!_cache.TryGetValue(repo.Trim(), out var entry) || entry.Releases.Count == 0) return null;
            var stable = entry.Releases.FirstOrDefault(r => !r.Prerelease);
            if (!beta) return stable;
            var pre = entry.Releases.Where(r => r.Prerelease).OrderByDescending(r => ParseVersion(r.Tag)).FirstOrDefault();
            if (pre is null) return stable;
            if (stable is null) return pre;
            return ParseVersion(pre.Tag) is { } p && ParseVersion(stable.Tag) is { } s && p > s ? pre : stable;
        }
    }

    /// <summary>
    /// Ist <paramref name="tag"/> neuer als <paramref name="installed"/>? Gleiche Nummer, aber installiert ist eine
    /// Vorabversion („2.16.0-rc1“) und das Ziel nicht → ja. Null = nicht vergleichbar.
    /// </summary>
    public static bool? IsNewer(string tag, string installed)
    {
        var a = ParseVersion(installed);
        var b = ParseVersion(tag);
        if (a is null || b is null) return null;
        if (b != a) return b > a;
        return HasSuffix(installed) && !HasSuffix(tag);
    }

    private static bool HasSuffix(string v) => SuffixRegex().IsMatch(v.Trim());

    /// <summary>Aktualisiert den Cache für alle Repositories, deren Eintrag älter als 6 Stunden ist.</summary>
    public async Task RefreshAsync(IEnumerable<string> repos, CancellationToken ct = default)
    {
        foreach (var repo in repos.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(repo, out var e) && DateTime.UtcNow - e.Fetched < CacheTime) continue;
            }

            List<FirmwareRelease> releases;
            try
            {
                using var resp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=10", ct);
                releases = resp.IsSuccessStatusCode ? ParseReleases(await resp.Content.ReadAsStringAsync(ct)) : [];
            }
            catch
            {
                // Netzfehler: beim nächsten Durchlauf erneut versuchen
                continue;
            }

            lock (_cache) _cache[repo] = (DateTime.UtcNow, releases);
        }
    }

    /// <summary>Antwort von /repos/{repo}/releases – Entwürfe werden übersprungen, Reihenfolge wie geliefert (neueste zuerst).</summary>
    public static List<FirmwareRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<FirmwareRelease>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var assets = new List<FirmwareAsset>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                {
                    var name = x.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = x.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                    var size = x.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    var digest = x.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                    var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    assets.Add(new FirmwareAsset(name, size, sha, url));
                }
            list.Add(new FirmwareRelease(tag, r.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True, assets));
        }
        return list;
    }

    /// <summary>Nur für Tests: Releases eines Repositorys direkt setzen.</summary>
    internal void SetReleases(string repo, List<FirmwareRelease> releases)
    {
        lock (_cache) _cache[repo.Trim()] = (DateTime.UtcNow, releases);
    }

    /// <summary>"v2.4.1", "2.4.1-dev", "v2.4.1b" → 2.4.1.0; „v1.1.0.1“ (NerdQAxe) → 1.1.0.1. Immer vierstellig, damit vergleichbar.</summary>
    public static Version? ParseVersion(string text)
    {
        var m = VersionRegex().Match(text.Trim());
        if (!m.Success) return null;

        var parts = m.Groups[1].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    [GeneratedRegex(@"^[vV]?(\d+(?:\.\d+){0,3})")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[vV]?\d+(?:\.\d+){0,3}[-+a-zA-Z]")]
    private static partial Regex SuffixRegex();

    public void Dispose() => _http.Dispose();
}
