using System.Net.Http;
using System.Text.Json;

namespace BitaxeTuner.App.Services;

/// <summary>Fragt die GitHub-Releases-API nach einer neueren Version.</summary>
public static class UpdateChecker
{
    public const string Repository = "Elemirus1996/BitaxeTuner";
    public const string ProjectUrl = $"https://github.com/{Repository}";

    public static async Task<(string Version, string Url)?> CheckAsync(string currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            var url = doc.RootElement.GetProperty("html_url").GetString();
            if (tag is null || url is null) return null;
            return IsNewer(tag, currentVersion) ? (tag, url) : null;
        }
        catch
        {
            // Offline oder noch kein Release – kein Problem.
            return null;
        }
    }

    public static bool IsNewer(string remote, string current) =>
        Version.TryParse(remote.TrimStart('v', 'V'), out var r) &&
        Version.TryParse(current.TrimStart('v', 'V'), out var c) &&
        r > c;
}
