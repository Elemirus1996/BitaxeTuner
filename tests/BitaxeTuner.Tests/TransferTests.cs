using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Server;
using BitaxeTuner.Server.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BitaxeTuner.Tests;

/// <summary>Einmalige, geprüfte Datenübertragung Desktop ↔ Server.</summary>
public class TransferTests
{
    /// <summary>Datenordner wie auf dem Desktop: Einstellungen, Verlauf, Steuer, Benchmark, Sicherungen – plus Dinge, die nie mitgehen.</summary>
    private static void FillDataDirectory(string dir, int samples = 50)
    {
        var config = new AppConfig { ElectricityCtPerKwh = 31.5 };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "192.168.60.5" });
        config.Server = new ServerConnectionSettings { Enabled = true, Url = "http://pi:8484/", Token = "btk_geheim" };
        config.Save(Path.Combine(dir, "config.json"));
        using (var h = new HistoryStore(Path.Combine(dir, "history.db")))
        {
            var t = DateTime.Now.AddHours(-samples);
            for (var i = 0; i < samples; i++) h.AddSample("192.168.60.5", t.AddMinutes(i), 1000 + i, 55, 15, true);
        }
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(Path.Combine(dir, "tax"));
        File.WriteAllText(Path.Combine(dir, "tax", "rewards.json"), "[]");
        Directory.CreateDirectory(Path.Combine(dir, "tuning"));
        File.WriteAllText(Path.Combine(dir, "tuning", "lauf.json"), "{\"a\":1}");
        Directory.CreateDirectory(Path.Combine(dir, "snapshots"));
        File.WriteAllText(Path.Combine(dir, "snapshots", "s.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "backup-20260101-000000"));
        File.WriteAllText(Path.Combine(dir, "backup-20260101-000000", "config.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "server-auth.json"), "{\"AdminHash\":\"x\"}");
        File.WriteAllText(Path.Combine(dir, "datadir.json"), "{}");
    }

    private static MemoryStream Archive(string dir)
    {
        var ms = new MemoryStream();
        DataArchive.Create(dir, null, ms, "desktop", "0.3.0");
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Archive_round_trip_verifies_everything_and_leaves_out_secrets_and_backups()
    {
        using var src = new TempDir();
        using var staging = new TempDir();
        FillDataDirectory(src.Path);

        using var zip = Archive(src.Path);
        var manifest = DataArchive.ExtractAndVerify(zip, staging.File("x"));

        Assert.Equal(50, manifest.HistoryRows["samples"]);
        Assert.Equal(1, manifest.Devices);
        var files = manifest.Files.Select(f => f.Path).OrderBy(x => x).ToList();
        Assert.Equal(["config.json", "history.db", "snapshots/s.json", "tax/rewards.json", "tuning/lauf.json"], files);
        Assert.False(File.Exists(staging.File("x", "server-auth.json")));
        Assert.False(Directory.Exists(staging.File("x", "backup-20260101-000000")));
    }

    [Fact]
    public void Tampered_archive_is_rejected()
    {
        using var src = new TempDir();
        using var staging = new TempDir();
        FillDataDirectory(src.Path);
        using var zip = Archive(src.Path);

        // Inhalt einer Datei ändern, Manifest unverändert lassen
        var tampered = new MemoryStream();
        zip.CopyTo(tampered);
        using (var a = new ZipArchive(tampered, ZipArchiveMode.Update, leaveOpen: true))
        {
            a.GetEntry("data/tuning/lauf.json")!.Delete();
            using var w = new StreamWriter(a.CreateEntry("data/tuning/lauf.json").Open());
            w.Write("{\"a\":2}");
        }
        tampered.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => DataArchive.ExtractAndVerify(tampered, staging.File("t")));
        Assert.Contains("Prüfsumme", ex.Message);
    }

    [Fact]
    public void Path_traversal_in_archive_is_rejected()
    {
        using var staging = new TempDir();
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var m = new ArchiveManifest { Files = [new ArchiveFile { Path = "../evil.txt", Size = 1, Sha256 = "x" }] };
            using (var s = a.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(s, m);
            using var w = new StreamWriter(a.CreateEntry("data/../evil.txt").Open());
            w.Write("x");
        }
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => DataArchive.ExtractAndVerify(ms, staging.File("t")));
        Assert.False(File.Exists(staging.File("evil.txt")));
    }

    [Fact]
    public void Apply_backs_up_target_first_and_keeps_local_settings()
    {
        using var src = new TempDir();
        using var target = new TempDir();
        using var staging = new TempDir();
        FillDataDirectory(src.Path, samples: 20);
        // Ziel hat eigene Daten und eine eigene Serververbindung
        var local = new AppConfig { Theme = "light" };
        local.Devices.Add(new DeviceConfig { Name = "Alt", Host = "10.0.0.1" });
        local.Save(target.File("config.json"));
        File.WriteAllText(target.File("crash.log"), "bleibt");

        using (var zip = Archive(src.Path)) DataArchive.ExtractAndVerify(zip, staging.File("s"));
        var backup = DataArchive.Apply(staging.File("s"), target.Path, (old, fresh) => fresh.Theme = old.Theme);

        var result = AppConfig.Load(target.File("config.json"));
        Assert.Equal("Gamma", Assert.Single(result.Devices).Name);
        Assert.Equal("light", result.Theme);
        Assert.Equal(31.5, result.ElectricityCtPerKwh);
        Assert.Equal("Alt", AppConfig.Load(Path.Combine(backup, "config.json")).Devices[0].Name);
        Assert.Equal("bleibt", File.ReadAllText(target.File("crash.log")));
        Assert.Equal(20, HistoryStore.Verify(target.File("history.db")).Rows["samples"]);
        SqliteConnection.ClearAllPools();
    }

    // ---------- Server ----------

    private static WebApplicationFactory<Program> Server(string dir)
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ServerSettings>();
            s.AddSingleton(new ServerSettings { DataDirectory = dir });
            s.AddSingleton(new MinerHubOptions { ClientFactory = h => new SimulatedMinerClient(gamma, 3, h), OnlineChecks = false });
        }));
    }

    private static async Task<HttpClient> TokenClientAsync(WebApplicationFactory<Program> f)
    {
        var auth = f.Services.GetRequiredService<AuthStore>();
        if (auth.SetupCode is { } code) auth.Setup(code, "sehr-geheim-123");
        var (_, token) = auth.CreateToken("Test");
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await Task.CompletedTask;
        return c;
    }

    [Fact]
    public async Task Server_accepts_import_when_empty_asks_otherwise_and_exports_again()
    {
        using var desktop = new TempDir();
        using var serverDir = new TempDir();
        FillDataDirectory(desktop.Path);
        File.Delete(desktop.File("server-auth.json"));
        using var f = Server(serverDir.Path);
        var c = await TokenClientAsync(f);

        using (var zip = Archive(desktop.Path))
        {
            var r = await c.PostAsync("/api/v1/admin/import", new StreamContent(zip));
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            Assert.Contains("1 Gerät", await r.Content.ReadAsStringAsync());
        }
        var status = await c.GetFromJsonAsync<JsonElement>("/api/v1/status");
        Assert.Equal("192.168.60.5", status.GetProperty("devices")[0].GetProperty("host").GetString());
        // Zugangsdaten des Servers überleben, Desktop-Verbindungsdaten landen nicht auf dem Server
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Empty(AppConfig.Load(serverDir.File("config.json")).Server.Token);

        // Zweiter Import ohne Bestätigung: abgelehnt, nichts geändert
        using (var zip = Archive(desktop.Path))
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/v1/admin/import", new StreamContent(zip))).StatusCode);
        using (var zip = Archive(desktop.Path))
            Assert.True((await c.PostAsync("/api/v1/admin/import?replace=true", new StreamContent(zip))).IsSuccessStatusCode);
        Assert.Contains(Directory.GetDirectories(serverDir.Path), d => Path.GetFileName(d).StartsWith("backup-"));

        // Export zurück: prüfbar, gleicher Verlauf
        var export = await c.GetAsync("/api/v1/admin/export");
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        using var staging = new TempDir();
        var manifest = DataArchive.ExtractAndVerify(await export.Content.ReadAsStreamAsync(), staging.File("e"));
        Assert.Equal("server", manifest.Source);
        Assert.True(manifest.HistoryRows["samples"] >= 50);
        Assert.DoesNotContain(manifest.Files, x => x.Path.Contains("server-auth"));
    }

    [Fact]
    public async Task Provisioning_package_sets_up_pi_on_first_boot_and_removes_secrets()
    {
        using var desktop = new TempDir();
        using var boot = new TempDir();
        using var serverDir = new TempDir();
        FillDataDirectory(desktop.Path);

        Assert.Throws<InvalidOperationException>(() => Provisioning.Write(boot.Path, "kurz", null, null, "0.3.0"));
        var token = Provisioning.Write(boot.Path, "sehr-geheim-123", desktop.Path, null, "0.3.0");
        var package = Path.Combine(boot.Path, Provisioning.FolderName);
        var accessJson = File.ReadAllText(Path.Combine(package, Provisioning.AccessFile));
        Assert.DoesNotContain("sehr-geheim-123", accessJson);
        Assert.DoesNotContain(token, accessJson);
        Assert.True(File.Exists(Path.Combine(package, Provisioning.DataFile)));

        var log = Provisioning.Apply(package, serverDir.Path);
        Assert.Contains(log, l => l.StartsWith("Daten übernommen: 1 Gerät"));
        // Geheimnisse und Datenarchiv sind von der Boot-Partition verschwunden
        Assert.False(File.Exists(Path.Combine(package, Provisioning.AccessFile)));
        Assert.False(File.Exists(Path.Combine(package, Provisioning.DataFile)));
        Assert.True(File.Exists(Path.Combine(package, "UEBERNOMMEN.txt")));
        Assert.Empty(AppConfig.Load(serverDir.File("config.json")).Server.Token);

        using var f = Server(serverDir.Path);
        var auth = f.Services.GetRequiredService<AuthStore>();
        Assert.Null(auth.SetupCode);
        Assert.Equal(Role.Admin, auth.Login("sehr-geheim-123"));
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var info = await c.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.True(info.GetProperty("paused").GetBoolean());
        var status = await c.GetFromJsonAsync<JsonElement>("/api/v1/status");
        Assert.Equal("192.168.60.5", status.GetProperty("devices")[0].GetProperty("host").GetString());

        // Zweites Paket auf einem eingerichteten Pi: nichts wird überschrieben
        Provisioning.Write(boot.Path, "anderes-passwort-9", desktop.Path, null, "0.3.0");
        var second = Provisioning.Apply(package, serverDir.Path);
        Assert.Contains(second, l => l.Contains("bereits eingerichtet"));
        Assert.Contains(second, l => l.Contains("nicht übernommen"));
    }

    [Fact]
    public async Task Pause_stops_server_and_survives_restart()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "Devices": [ { "Name": "A", "Host": "192.168.60.9" } ] }""");
        using (var f = Server(dir.Path))
        {
            var c = await TokenClientAsync(f);
            (await c.PostAsJsonAsync("/api/v1/admin/pause", new { paused = true })).EnsureSuccessStatusCode();
            Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/info")).GetProperty("paused").GetBoolean());
        }
        using (var f = Server(dir.Path))
        {
            var c = await TokenClientAsync(f);
            var info = await c.GetFromJsonAsync<JsonElement>("/api/v1/info");
            Assert.True(info.GetProperty("paused").GetBoolean());
            Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/v1/status")).GetProperty("running").GetBoolean());
            (await c.PostAsJsonAsync("/api/v1/admin/pause", new { paused = false })).EnsureSuccessStatusCode();
            Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/status")).GetProperty("running").GetBoolean());
        }
    }

    [Fact]
    public async Task Token_can_open_browser_session_for_embedded_ui()
    {
        using var dir = new TempDir();
        using var f = Server(dir.Path);
        var c = await TokenClientAsync(f);
        var s = await (await c.PostAsJsonAsync("/api/v1/token-session", new { })).Content.ReadFromJsonAsync<JsonElement>();
        var browser = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        browser.DefaultRequestHeaders.Add("Cookie", $"{AuthContext.CookieName}={s.GetProperty("session").GetString()}");
        var session = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session");
        Assert.Equal("Admin", session.GetProperty("role").GetString());

        // Ohne Token keine Sitzung
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/token-session", new { })).StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.20", "http://192.168.1.20:8484/")]
    [InlineData("pi.local:9000", "http://pi.local:9000/")]
    [InlineData("https://nas:8484/foo", "https://nas:8484/")]
    public void Server_address_is_normalized(string input, string expected) =>
        Assert.Equal(expected, ServerClient.Normalize(input).ToString());
}
