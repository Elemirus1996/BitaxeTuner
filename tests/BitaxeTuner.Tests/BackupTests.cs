using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Server;
using BitaxeTuner.Server.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BitaxeTuner.Tests;

/// <summary>Tägliche Sicherung: geprüftes Archiv, Ziele, Aufräumen, Passwörter getrennt.</summary>
public class BackupTests
{
    private static MinerHub Hub(TempDir dir, List<string>? sent = null)
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "t" } };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.6.1" });
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
        });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        if (sent is not null) hub.Notify.Sending += (key, _, _, _) => { lock (sent) sent.Add(key); };
        return hub;
    }

    [Fact]
    public async Task Update_backup_goes_to_all_targets_is_reused_and_blocks_the_update_on_failure()
    {
        using var dir = new TempDir();
        using var usb = new TempDir();
        var sent = new List<string>();
        using var hub = Hub(dir, sent);
        await hub.PollNowAsync();
        hub.Config.Backup.Folder = new BackupFolderTarget { Enabled = true, Path = usb.File("Sicherungen") };

        var first = await hub.BackupBeforeUpdateAsync("v9.9.9");
        Assert.True(first.LastOk);
        Assert.Single(Directory.GetFiles(usb.File("Sicherungen")));                        // auch auf dem USB-Stick
        var again = await hub.BackupBeforeUpdateAsync("v9.9.9");                             // innerhalb von 15 min: wiederverwendet
        Assert.Equal(first.LastFile, again.LastFile);
        Assert.Single(hub.LocalBackups());
        var log = hub.History!.QueryEvents(DateTime.Now.AddHours(-1), DateTime.Now.AddMinutes(1), host: "");
        Assert.Contains(log, e => e.Message.Contains("v9.9.9") && e.Message.Contains(first.LastFile!));

        // Ziel nicht beschreibbar (eine Datei statt eines Ordners): Update wird abgebrochen, Meldung geht raus
        File.WriteAllText(usb.File("kein-ordner"), "x");
        hub.Config.Backup.Folder.Path = usb.File("kein-ordner");
        var failed = await hub.RunBackupAsync(DateTime.Now);                                 // schlägt am Ziel fehl …
        Assert.False(failed.LastOk);
        // … wird nicht wiederverwendet, die neue Sicherung schlägt ebenfalls fehl → kein Update
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.BackupBeforeUpdateAsync("v9.9.10"));
        Assert.Contains("Update abgebrochen", ex.Message);
        Assert.Contains("update-backup:v9.9.10", sent);
    }

    [Fact]
    public async Task Backup_is_verified_copied_to_folder_rotated_and_never_contains_secrets()
    {
        using var dir = new TempDir();
        using var usb = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        hub.Secrets.Set(SecretStore.SmbPassword, "nas-geheim-42");
        hub.Config.Backup.Folder = new BackupFolderTarget { Enabled = true, Path = usb.File("BitaxeTuner-Sicherungen") };
        hub.Config.Backup.LocalKeep = 2;
        hub.Config.Backup.Keep = 3;

        var t = new DateTime(2026, 9, 1, 3, 0, 0);
        BackupStatus? status = null;
        for (var day = 0; day < 4; day++) status = await hub.RunBackupAsync(t.AddDays(day));

        Assert.True(status!.LastOk);
        Assert.All(status.Targets, x => Assert.True(x.Ok, x.Message));
        Assert.Equal(2, hub.LocalBackups().Count);                                        // Datenordner: 2
        var copies = Directory.GetFiles(usb.File("BitaxeTuner-Sicherungen"));
        Assert.Equal(3, copies.Length);                                                   // Ordner/USB: 3
        Assert.Equal(BackupNames.For(t.AddDays(3)), hub.LocalBackups()[0].Name);          // neueste zuerst

        // Kopie identisch und als Datenarchiv prüfbar; keine Geheimnisse, keine Serverzugänge
        var newest = Path.Combine(usb.File("BitaxeTuner-Sicherungen"), BackupNames.For(t.AddDays(3)));
        Assert.Equal(File.ReadAllBytes(hub.LocalBackups()[0].FullName), File.ReadAllBytes(newest));
        using var staging = new TempDir();
        await using (var fs = File.OpenRead(newest))
        {
            var manifest = DataArchive.ExtractAndVerify(fs, staging.File("x"));
            Assert.Equal("backup", manifest.Source);
            Assert.Contains(manifest.Files, f => f.Path == "history.db");
        }
        using (var zip = ZipFile.OpenRead(newest))
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("secrets") || e.FullName.Contains("server-auth") || e.FullName.Contains("auto-backups"));
        Assert.DoesNotContain("nas-geheim-42", File.ReadAllText(dir.File("config.json")));

        // Fremde Dateien im Zielordner bleiben unberührt
        File.WriteAllText(Path.Combine(usb.File("BitaxeTuner-Sicherungen"), "eigene-notiz.txt"), "x");
        await hub.RunBackupAsync(t.AddDays(5));
        Assert.True(File.Exists(Path.Combine(usb.File("BitaxeTuner-Sicherungen"), "eigene-notiz.txt")));

        // Status übersteht einen Neustart
        hub.Dispose();
        using var again = Hub(dir);
        Assert.Equal(t.AddDays(5), again.BackupStatus.LastRun);
    }

    [Fact]
    public async Task Failing_target_is_reported_but_local_backup_is_kept()
    {
        using var dir = new TempDir();
        var sent = new List<string>();
        using var hub = Hub(dir, sent);
        File.WriteAllText(dir.File("keinordner"), "ich bin eine Datei");
        hub.Config.Backup.Folder = new BackupFolderTarget { Enabled = true, Path = dir.File("keinordner", "sub") };
        var status = await hub.RunBackupAsync(DateTime.Now);
        Assert.False(status.LastOk);
        Assert.True(status.Targets.Single(t => t.Target == "Datenordner").Ok);
        Assert.False(status.Targets.Single(t => t.Target == "Ordner/USB").Ok);
        Assert.Single(hub.LocalBackups());
        Assert.Contains("backup-failed:Ordner/USB", sent);
    }

    [Fact]
    public async Task Scheduled_backup_runs_once_per_day_after_the_hour()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        hub.BackupNotBefore = DateTime.MinValue;
        hub.CheckBackup(new DateTime(2026, 9, 2, 2, 59, 0));
        Assert.Null(hub.Config.Backup.LastRun);                                           // vor 3 Uhr nicht
        hub.CheckBackup(new DateTime(2026, 9, 2, 3, 5, 0));
        Assert.Equal("2026-09-02", hub.Config.Backup.LastRun);
        for (var i = 0; i < 100 && hub.BackupStatus.LastRun is null; i++) await Task.Delay(50);
        Assert.Single(hub.LocalBackups());
        hub.CheckBackup(new DateTime(2026, 9, 2, 9, 0, 0));                              // am selben Tag nicht noch einmal
        await Task.Delay(200);
        Assert.Single(hub.LocalBackups());
        hub.Config.Backup.Enabled = false;
        hub.CheckBackup(new DateTime(2026, 9, 3, 4, 0, 0));
        Assert.Equal("2026-09-02", hub.Config.Backup.LastRun);
    }

    [Fact]
    public void Secrets_are_stored_apart_and_protected()
    {
        using var dir = new TempDir();
        var store = new SecretStore(dir.Path);
        store.Set(SecretStore.MqttPassword, "mqtt-geheim-7");
        Assert.Equal("mqtt-geheim-7", new SecretStore(dir.Path).Get(SecretStore.MqttPassword));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("mqtt-geheim-7", File.ReadAllText(store.FilePath));
        Assert.False(DataArchive.IsTransferable("secrets.json"));
        store.Set(SecretStore.MqttPassword, null);
        Assert.False(store.Has(SecretStore.MqttPassword));
    }

    [Fact]
    public async Task Desktop_picks_up_a_verified_server_backup_once_a_day()
    {
        using var dir = new TempDir();
        using var pc = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "Devices": [ { "Name": "A", "Host": "192.168.60.9" } ] }""");
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ServerSettings>();
            s.AddSingleton(new ServerSettings { DataDirectory = dir.Path });
            s.AddSingleton(new MinerHubOptions { OnlineChecks = false });
        }));
        var auth = f.Services.GetRequiredService<AuthStore>();
        if (auth.SetupCode is { } code) auth.Setup(code, "sehr-geheim-123");
        var (_, token) = auth.CreateToken("PC");
        using var client = new ServerClient(f.CreateClient(), token);

        var s = new ServerConnectionSettings { Enabled = true, BackupPickup = true, BackupFolder = pc.Path };
        var now = new DateTime(2026, 9, 5, 10, 0, 0);
        Assert.True(BackupPickup.IsDue(s, now));
        for (var d = 0; d < 3; d++) await BackupPickup.RunAsync(client, BackupPickup.FolderOf(s), keep: 2, now.AddDays(d));
        var files = Directory.GetFiles(pc.Path).Select(Path.GetFileName).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, n => Assert.True(BackupNames.IsBackup(n!)));
        s.BackupLastPickup = "2026-09-05";
        Assert.False(BackupPickup.IsDue(s, now));
        Assert.False(BackupPickup.IsDue(new ServerConnectionSettings { Enabled = false, BackupPickup = true }, now));   // Lokal-Modus: nichts holen
    }

    [Fact]
    public async Task Api_never_returns_the_nas_password_and_validates_input()
    {
        using var dir = new TempDir();
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ServerSettings>();
            s.AddSingleton(new ServerSettings { DataDirectory = dir.Path });
            s.AddSingleton(new MinerHubOptions { OnlineChecks = false });
        }));
        var auth = f.Services.GetRequiredService<AuthStore>();
        if (auth.SetupCode is { } code) auth.Setup(code, "sehr-geheim-123");
        var (_, token) = auth.CreateToken("Test");
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var settings = new BackupSettings { Smb = new BackupSmbTarget { Enabled = true, Server = "nas", Share = "backup", User = "bt" }, LastRun = "2099-01-01" };
        var r = await c.PutAsJsonAsync("/api/v1/backup", new { settings, smbPassword = "nas-geheim-42", clearSmbPassword = false });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var get = await c.GetStringAsync("/api/v1/backup");
        Assert.DoesNotContain("nas-geheim-42", get);
        var json = JsonDocument.Parse(get).RootElement;
        Assert.True(json.GetProperty("smbPasswordSet").GetBoolean());
        Assert.NotEqual("2099-01-01", json.GetProperty("settings").GetProperty("lastRun").GetString());   // nur der Server setzt das

        settings.Folder = new BackupFolderTarget { Enabled = true, Path = "relativ/ordner" };
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/backup", new { settings, smbPassword = (string?)null, clearSmbPassword = false })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/backup/files/..%2Fconfig.json")).StatusCode);

        var run = await c.PostAsJsonAsync("/api/v1/backup/run", new { });
        Assert.True(run.IsSuccessStatusCode);
        var name = JsonDocument.Parse(await c.GetStringAsync("/api/v1/backup")).RootElement.GetProperty("files")[0].GetProperty("name").GetString()!;
        var download = await c.GetAsync($"/api/v1/backup/files/{name}");
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Backup_from_data_folder_can_be_restored_and_keeps_the_previous_state()
    {
        using var dir = new TempDir();
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ServerSettings>();
            s.AddSingleton(new ServerSettings { DataDirectory = dir.Path });
            s.AddSingleton(new MinerHubOptions { OnlineChecks = false });
        }));
        var auth = f.Services.GetRequiredService<AuthStore>();
        if (auth.SetupCode is { } code) auth.Setup(code, "sehr-geheim-123");
        var (_, token) = auth.CreateToken("Test");
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);

        async Task SetPrice(double ct)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync("/api/v1/settings"))!;
            node["electricityCtPerKwh"] = ct;
            var put = await c.PutAsync("/api/v1/settings", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        }
        async Task<double> Price() =>
            JsonDocument.Parse(await c.GetStringAsync("/api/v1/settings")).RootElement.GetProperty("electricityCtPerKwh").GetDouble();

        await SetPrice(27.5);
        Assert.True((await c.PostAsJsonAsync("/api/v1/backup/run", new { })).IsSuccessStatusCode);
        var name = JsonDocument.Parse(await c.GetStringAsync("/api/v1/backup")).RootElement.GetProperty("files")[0].GetProperty("name").GetString()!;
        await SetPrice(41);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/backup/files/config.json/restore", new { })).StatusCode);
        var r = await c.PostAsJsonAsync($"/api/v1/backup/files/{name}/restore", new { });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

        Assert.Equal(27.5, await Price());                                                    // Stand der Sicherung
        var previous = Directory.GetDirectories(dir.Path, "backup-*").Single();                  // vorheriger Stand aufbewahrt
        Assert.Contains("41", File.ReadAllText(Path.Combine(previous, "config.json")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "auto-backups", name)));                  // Sicherungen bleiben
    }
}
