using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Server.Api;

public sealed record BackupRequest(BackupSettings Settings, string? SmbPassword, bool ClearSmbPassword);
public sealed record BackupTestRequest(string Target);

/// <summary>Sicherung: Einstellungen (Passwort nur schreibbar), Status, „Jetzt sichern“, Verbindungstest, Download.</summary>
public static class BackupEndpoints
{
    /// <summary>Einhängepunkt des USB-Sticks am Pi (udev-Regel aus install.sh / Pi-Image).</summary>
    public const string UsbMountPoint = "/media/bitaxetuner-usb";
    public const string UsbFolder = UsbMountPoint + "/BitaxeTuner-Sicherungen";

    private static object Usb()
    {
        var linux = OperatingSystem.IsLinux();
        var mounted = false;
        if (linux)
        {
            try { mounted = File.ReadLines("/proc/mounts").Any(l => l.Split(' ') is { Length: > 2 } p && p[1] == UsbMountPoint); }
            catch { /* ohne /proc */ }
        }
        return new
        {
            available = linux,
            ruleInstalled = linux && File.Exists("/etc/udev/rules.d/99-bitaxetuner-usb.rules"),
            mounted,
            folder = UsbFolder,
            setupCommand = "sudo sh /opt/bitaxetuner/current/install.sh --system",
        };
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/backup", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            settings = Dto.Copy(h.Config.Backup),
            smbPasswordSet = h.Secrets.Has(SecretStore.SmbPassword),
            status = h.BackupStatus,
            files = h.LocalBackups().Select(f => new { name = f.Name, size = f.Length, time = f.LastWriteTime }).ToList(),
            usb = Usb(),
        })));

        g.MapPut("/backup", async (BackupRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var s = req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            s.Hour = Math.Clamp(s.Hour, 0, 23);
            s.Keep = Math.Clamp(s.Keep, 1, 365);
            s.LocalKeep = Math.Clamp(s.LocalKeep, 1, 60);
            s.Folder ??= new BackupFolderTarget();
            s.Smb ??= new BackupSmbTarget();
            s.Folder.Path = (s.Folder.Path ?? "").Trim();
            if (s.Folder.Enabled && s.Folder.Path.Length == 0) throw new InvalidOperationException(L.N("Ordner/USB: Zielordner angeben."));
            if (s.Folder.Enabled && !Path.IsPathFullyQualified(s.Folder.Path)) throw new InvalidOperationException(L.N("Ordner/USB: vollständigen Pfad angeben."));
            s.Smb.Server = (s.Smb.Server ?? "").Trim();
            s.Smb.Share = (s.Smb.Share ?? "").Trim().Trim('\\', '/');
            s.Smb.Folder = (s.Smb.Folder ?? "").Trim();
            s.Smb.User = (s.Smb.User ?? "").Trim();
            s.Smb.Domain = (s.Smb.Domain ?? "").Trim();
            if (s.Smb.Enabled && (s.Smb.Server.Length == 0 || s.Smb.Share.Length == 0)) throw new InvalidOperationException(L.N("Netzlaufwerk: Server und Freigabe angeben."));
            if (s.Smb.Folder.Contains("..")) throw new InvalidOperationException(L.N("Netzlaufwerk: ungültiger Unterordner."));
            s.LastRun = h.Config.Backup.LastRun; // nur der Server setzt das
            h.Config.Backup = s;
            if (req.ClearSmbPassword) h.Secrets.Set(SecretStore.SmbPassword, null);
            else if (!string.IsNullOrEmpty(req.SmbPassword)) h.Secrets.Set(SecretStore.SmbPassword, req.SmbPassword);
            h.Config.Save();
            return new { ok = true, smbPasswordSet = h.Secrets.Has(SecretStore.SmbPassword) };
        })));

        g.MapPost("/backup/run", async (HubService hub) => Results.Json(await hub.RunAsync(h => h.RunBackupAsync(DateTime.Now))));

        g.MapPost("/backup/test", async (BackupTestRequest req, HubService hub, HttpContext http) =>
        {
            var target = await hub.RunAsync(h => h.BackupTargets().FirstOrDefault(t => req.Target == "smb" ? t is SmbBackupTarget : t is FolderBackupTarget));
            if (target is null) return Endpoints.Error(400, L.N("Dieses Ziel ist nicht eingeschaltet (erst speichern)."));
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var files = await target.ListAsync(cts.Token);
                if (target is FolderBackupTarget folder)
                {
                    // Schreibrecht prüfen
                    Directory.CreateDirectory(folder.Path);
                    var probe = Path.Combine(folder.Path, ".bitaxetuner-test");
                    await File.WriteAllTextAsync(probe, "ok", cts.Token);
                    File.Delete(probe);
                }
                return Results.Json(new { ok = true, message = Endpoints.LangOf(http).T("{0}: erreichbar, {1} Sicherung(en) vorhanden.", target.Name, files.Count(BackupNames.IsBackup)) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                return Results.Json(new { ok = false, message = $"{target.Name}: {Endpoints.LangOf(http).T(ex.Message)}" });
            }
        });

        g.MapGet("/backup/files/{name}", async (string name, HubService hub) =>
        {
            if (!BackupNames.IsBackup(name)) return Endpoints.Error(404, L.N("Sicherung nicht gefunden."));
            var file = await hub.RunAsync(h => Path.Combine(h.BackupDirectory, name));
            return File.Exists(file)
                ? Results.File(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read), "application/zip", name)
                : Endpoints.Error(404, L.N("Sicherung nicht gefunden."));
        });

        // Sicherung aus dem Datenordner einspielen: erst vollständig entpacken und prüfen, dann ersetzen –
        // der bisherige Stand wird dabei als Ordner backup-<Zeit> im Datenordner aufbewahrt (DataArchive.Apply).
        g.MapPost("/backup/files/{name}/restore", async (string name, HubService hub, HttpContext http) =>
        {
            if (!BackupNames.IsBackup(name)) return Endpoints.Error(404, L.N("Sicherung nicht gefunden."));
            var (file, dir) = await hub.RunAsync(h => (Path.Combine(h.BackupDirectory, name), h.DataDirectory));
            if (!File.Exists(file)) return Endpoints.Error(404, L.N("Sicherung nicht gefunden."));
            var staging = Path.Combine(dir, $"restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..39]);
            try
            {
                ArchiveManifest manifest;
                try
                {
                    await using var zip = File.OpenRead(file);
                    manifest = DataArchive.ExtractAndVerify(zip, staging);
                }
                catch (InvalidDataException ex) { return Endpoints.Error(400, L.N("Archiv abgelehnt: {0}"), ex.Message); }
                var previous = await hub.ReplaceDataAsync(staging);
                return Results.Json(new
                {
                    ok = true,
                    message = Endpoints.LangOf(http).T("Sicherung {0} eingespielt: {1} Gerät(e), {2:N0} Verlaufswerte. Vorheriger Stand gesichert in {3}.",
                        name, manifest.Devices, manifest.HistoryRows.GetValueOrDefault("samples"), Path.GetFileName(previous)),
                });
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* egal */ }
            }
        });
    }
}
