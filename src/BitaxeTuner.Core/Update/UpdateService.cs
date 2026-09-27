using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BitaxeTuner.Core.Update;

/// <summary>Ein veröffentlichter Release mit Setup-Datei.</summary>
public sealed record UpdateInfo(
    Version Version, string Tag, string Name, string Notes, string ReleaseUrl,
    string SetupName, string SetupUrl, long SetupSize, string? Sha256);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    /// <summary>Kein (öffentlicher) Release erreichbar – z. B. Repo noch privat oder noch kein Release.</summary>
    NoRelease,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string Message);

/// <summary>
/// Prüft GitHub-Releases (<c>/repos/{owner}/{repo}/releases/latest</c>, ohne Anmeldung – also nur bei öffentlichem
/// Repo), lädt die Setup-Datei und prüft deren SHA-256 (GitHub-Asset-Digest oder SHA256SUMS.txt im Release).
/// Vorabversionen (Pre-Release) liefert <c>releases/latest</c> nicht aus.
/// </summary>
/// <param name="assetFilter">Welche Release-Datei passt (Standard: Setup der Desktop-App). Der Server wählt sein Paket je Plattform.</param>
public sealed partial class UpdateService(HttpClient http, string repository, Func<string, bool>? assetFilter = null)
{
    public const string SetupPrefix = "BitaxeTuner-Setup-";

    public static bool IsDesktopSetup(string name) =>
        name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private readonly Func<string, bool> _matches = assetFilter ?? IsDesktopSetup;

    public string Repository => repository;

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            req.Headers.UserAgent.ParseAdd("BitaxeTuner");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateCheckStatus.NoRelease, null, "Kein öffentlicher Release gefunden (Repository privat oder noch kein Release).");
            if ((int)resp.StatusCode is 403 or 429)
                return new(UpdateCheckStatus.Failed, null, "GitHub-Abfragelimit erreicht – später erneut.");
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = await ParseAsync(json, ct).ConfigureAwait(false);
            if (info is null)
                return new(UpdateCheckStatus.NoRelease, null, "Der neueste Release enthält keine passende Datei für diese Installation.");
            return info.Version > current
                ? new(UpdateCheckStatus.UpdateAvailable, info, $"Version {info.Tag} ist verfügbar.")
                : new(UpdateCheckStatus.UpToDate, info, $"Aktuell (neuester Release {info.Tag}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Failed, null, "Update-Prüfung fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>Release-JSON auswerten; fehlt der Asset-Digest, wird SHA256SUMS.txt aus dem Release gelesen.</summary>
    internal async Task<UpdateInfo?> ParseAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } version) return null;

        JsonElement? setup = null;
        string? sumsUrl = null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (_matches(name))
                setup = a;
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                sumsUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (setup is not { } s) return null;

        var setupName = s.GetProperty("name").GetString()!;
        string? sha = null;
        if (s.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha = digest[7..].ToLowerInvariant();
        if (sha is null && sumsUrl is not null)
        {
            try
            {
                var sums = await http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false);
                sha = ShaFromSums(sums, setupName);
            }
            catch (HttpRequestException) { /* ohne Prüfsumme – der Download wird dann abgelehnt */ }
        }

        return new UpdateInfo(version, tag,
            r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            r.GetProperty("html_url").GetString() ?? "",
            setupName, s.GetProperty("browser_download_url").GetString()!,
            s.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0, sha);
    }

    /// <summary>
    /// Setup herunterladen und Prüfsumme kontrollieren. Ohne bekannte Prüfsumme oder bei Abweichung wird die Datei
    /// gelöscht und eine Ausnahme geworfen – es wird nie eine ungeprüfte Datei ausgeführt.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string targetDirectory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InvalidOperationException("Für diese Setup-Datei ist keine SHA-256-Prüfsumme veröffentlicht – Installation abgebrochen.");

        Directory.CreateDirectory(targetDirectory);
        var file = Path.Combine(targetDirectory, update.SetupName);
        using (var resp = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? update.SetupSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(file))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidOperationException("Prüfsumme der heruntergeladenen Datei stimmt nicht – Installation abgebrochen.");
        }
        return file;
    }

    public static Version? ParseVersion(string text)
    {
        var m = VersionRegex().Match(text.Trim());
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split('.').Select(int.Parse).ToList();
        while (parts.Count < 3) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2]);
    }

    internal static string? ShaFromSums(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(fileName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"^[vV]?(\d+(?:\.\d+){0,2})")]
    private static partial Regex VersionRegex();
}
