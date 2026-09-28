using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.Update;

namespace BitaxeTuner.Tests;

public class UpdateTests
{
    private static readonly byte[] SetupBytes = Encoding.ASCII.GetBytes("fake setup exe");
    private static readonly string SetupSha = Convert.ToHexString(SHA256.HashData(SetupBytes)).ToLowerInvariant();

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static string Release(string tag, bool withDigest = true, bool prerelease = false, string? digest = null) => $$"""
        { "tag_name": "{{tag}}", "name": "BitaxeTuner {{tag}}", "body": "Änderungen", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/x/y/releases/tag/{{tag}}",
          "assets": [
            { "name": "BitaxeTuner-{{tag.TrimStart('v')}}-portable-win-x64.zip", "browser_download_url": "https://dl/zip", "size": 10 },
            { "name": "BitaxeTuner-Setup-{{tag.TrimStart('v')}}.exe", "browser_download_url": "https://dl/setup", "size": {{SetupBytes.Length}}
              {{(withDigest ? $", \"digest\": \"sha256:{digest ?? SetupSha}\"" : "")}} },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://dl/sums", "size": 100 } ] }
        """;

    private static UpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeGitHub(respond)), "x/y");

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Detects_newer_release_and_ignores_same_or_older()
    {
        var s = Service(_ => Ok(Release("v0.3.0")));
        var r = await s.CheckAsync(new Version(0, 2, 0));
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, r.Status);
        Assert.Equal("BitaxeTuner-Setup-0.3.0.exe", r.Update!.SetupName);
        Assert.Equal(SetupSha, r.Update.Sha256);

        Assert.Equal(UpdateCheckStatus.UpToDate, (await s.CheckAsync(new Version(0, 3, 0))).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await s.CheckAsync(new Version(1, 0, 0))).Status);
    }

    [Fact]
    public async Task Private_repo_or_no_release_is_reported_quietly()
    {
        var r = await Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync(new Version(0, 2, 0));
        Assert.Equal(UpdateCheckStatus.NoRelease, r.Status);
        Assert.Null(r.Update);
    }

    [Fact]
    public async Task Prerelease_is_not_offered()
    {
        var r = await Service(_ => Ok(Release("v0.9.0", prerelease: true))).CheckAsync(new Version(0, 2, 0));
        Assert.Equal(UpdateCheckStatus.NoRelease, r.Status);
    }

    [Fact]
    public async Task Falls_back_to_sha256sums_when_digest_missing()
    {
        var s = Service(req => req.RequestUri!.AbsoluteUri.Contains("sums")
            ? Ok($"0000  andere.zip\n{SetupSha} *BitaxeTuner-Setup-0.3.0.exe\n")
            : Ok(Release("v0.3.0", withDigest: false)));
        var r = await s.CheckAsync(new Version(0, 2, 0));
        Assert.Equal(SetupSha, r.Update!.Sha256);
    }

    [Fact]
    public async Task Download_verifies_checksum_and_deletes_tampered_file()
    {
        using var dir = new TempDir();
        var good = Service(req => req.RequestUri!.AbsoluteUri.Contains("setup") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) } : Ok(Release("v0.3.0")));
        var update = (await good.CheckAsync(new Version(0, 2, 0))).Update!;
        var file = await good.DownloadAsync(update, dir.Path);
        Assert.Equal(SetupBytes, File.ReadAllBytes(file));

        var bad = update with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => good.DownloadAsync(bad, dir.File("b")));
        Assert.False(File.Exists(Path.Combine(dir.File("b"), bad.SetupName)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => good.DownloadAsync(update with { Sha256 = null }, dir.File("c")));
    }

    [Fact]
    public async Task Server_picks_its_own_platform_package_not_the_desktop_setup()
    {
        var json = """
            { "tag_name": "v0.3.0", "draft": false, "prerelease": false, "html_url": "x", "assets": [
              { "name": "BitaxeTuner-Setup-0.3.0.exe", "browser_download_url": "https://dl/desk", "size": 1, "digest": "sha256:aa" },
              { "name": "BitaxeTuner-Server-Setup-0.3.0.exe", "browser_download_url": "https://dl/srvwin", "size": 1, "digest": "sha256:bb" },
              { "name": "BitaxeTuner-Server-0.3.0-linux-arm64.tar.gz", "browser_download_url": "https://dl/arm64", "size": 1, "digest": "sha256:cc" },
              { "name": "BitaxeTuner-Server-0.3.0-linux-x64.tar.gz", "browser_download_url": "https://dl/x64", "size": 1, "digest": "sha256:dd" } ] }
            """;
        var desktop = await Service(_ => Ok(json)).CheckAsync(new Version(0, 2, 0));
        Assert.Equal("BitaxeTuner-Setup-0.3.0.exe", desktop.Update!.SetupName);

        var server = await new UpdateService(new HttpClient(new FakeGitHub(_ => Ok(json))), "x/y", BitaxeTuner.Server.ServerUpdater.MatchesPlatform)
            .CheckAsync(new Version(0, 2, 0));
        var expected = OperatingSystem.IsWindows() ? "BitaxeTuner-Server-Setup-0.3.0.exe"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "BitaxeTuner-Server-0.3.0-linux-arm64.tar.gz" : "BitaxeTuner-Server-0.3.0-linux-x64.tar.gz";
        Assert.Equal(expected, server.Update!.SetupName);
    }

    [Fact]
    public async Task Updates_use_the_update_copy_so_install_downloads_stay_countable()
    {
        // Reihenfolge absichtlich: Originaldatei vor und nach der Kopie – die Kopie gewinnt immer
        var json = """
            { "tag_name": "v0.6.0", "draft": false, "prerelease": false, "html_url": "x", "assets": [
              { "name": "BitaxeTuner-Setup-0.6.0.exe", "browser_download_url": "https://dl/setup", "size": 1, "digest": "sha256:aa" },
              { "name": "update-BitaxeTuner-Setup-0.6.0.exe", "browser_download_url": "https://dl/update-setup", "size": 1, "digest": "sha256:aa" },
              { "name": "update-BitaxeTuner-Server-Setup-0.6.0.exe", "browser_download_url": "https://dl/update-srvwin", "size": 1, "digest": "sha256:bb" },
              { "name": "update-BitaxeTuner-Server-0.6.0-linux-x64.tar.gz", "browser_download_url": "https://dl/update-x64", "size": 1, "digest": "sha256:dd" },
              { "name": "update-BitaxeTuner-Server-0.6.0-linux-arm64.tar.gz", "browser_download_url": "https://dl/update-arm64", "size": 1, "digest": "sha256:cc" },
              { "name": "BitaxeTuner-Server-Setup-0.6.0.exe", "browser_download_url": "https://dl/srvwin", "size": 1, "digest": "sha256:bb" },
              { "name": "BitaxeTuner-Server-0.6.0-linux-x64.tar.gz", "browser_download_url": "https://dl/x64", "size": 1, "digest": "sha256:dd" },
              { "name": "BitaxeTuner-Server-0.6.0-linux-arm64.tar.gz", "browser_download_url": "https://dl/arm64", "size": 1, "digest": "sha256:cc" } ] }
            """;
        var desktop = (await Service(_ => Ok(json)).CheckAsync(new Version(0, 5, 0))).Update!;
        Assert.Equal("https://dl/update-setup", desktop.SetupUrl);

        var server = (await new UpdateService(new HttpClient(new FakeGitHub(_ => Ok(json))), "x/y", BitaxeTuner.Server.ServerUpdater.MatchesPlatform)
            .CheckAsync(new Version(0, 5, 0))).Update!;
        Assert.StartsWith("https://dl/update-", server.SetupUrl);
        Assert.StartsWith(UpdateService.UpdatePrefix + "BitaxeTuner-Server-", server.SetupName);
    }

    [Theory]
    [InlineData("BitaxeTuner-Server-0.3.0-linux-arm.tar.gz", false)]      // 32-bit-Paket nie auf 64-bit
    [InlineData("BitaxeTuner-Setup-0.3.0.exe", false)]
    public void Server_never_matches_foreign_packages(string name, bool expected)
    {
        if (!OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm) return;
        Assert.Equal(expected, BitaxeTuner.Server.ServerUpdater.MatchesPlatform(name));
    }

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.10", 0, 10, 0)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    public void Parses_tags(string tag, int a, int b, int c) =>
        Assert.Equal(new Version(a, b, c), UpdateService.ParseVersion(tag));

    [Fact]
    public void Server_update_push_is_independent_of_the_desktop_app()
    {
        // Nach einer Datenübertragung vom PC steht dort schon die Meldung der Desktop-App für dieselbe Version (#3)
        var config = new BitaxeTuner.Core.Config.AppConfig { NotifiedAppVersion = "v0.5.1" };
        config.Notifications.OnMaintenance = true;
        Assert.True(BitaxeTuner.Server.ServerUpdater.ShouldNotify(config, pushEnabled: true, "v0.5.1"));

        config.NotifiedServerVersion = "v0.5.1";                       // einmal je Version
        Assert.False(BitaxeTuner.Server.ServerUpdater.ShouldNotify(config, pushEnabled: true, "v0.5.1"));
        Assert.True(BitaxeTuner.Server.ServerUpdater.ShouldNotify(config, pushEnabled: true, "v0.6.0"));
        Assert.False(BitaxeTuner.Server.ServerUpdater.ShouldNotify(config, pushEnabled: false, "v0.6.0"));
        config.Notifications.OnMaintenance = false;
        Assert.False(BitaxeTuner.Server.ServerUpdater.ShouldNotify(config, pushEnabled: true, "v0.6.0"));
    }
}
