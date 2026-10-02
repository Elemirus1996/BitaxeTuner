using System.Globalization;
using System.Text.Json;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

public sealed record BackupTargetStatus(string Target, bool Ok, string Message);

public sealed record BackupStatus(DateTime? LastRun, bool? LastOk, string? LastFile, IReadOnlyList<BackupTargetStatus> Targets, bool Running);

/// <summary>
/// Tägliche Sicherung: dasselbe geprüfte Datenarchiv wie bei der Übertragung (SQLite-Backup-API, SHA-256 je Datei,
/// integrity_check), vor dem Ablegen einmal vollständig entpackt und geprüft. Immer im Datenordner (auto-backups),
/// dazu auf Ordner/USB und Netzlaufwerk; alte Sicherungen werden je Ziel aufgeräumt (nur eigene Dateien).
/// </summary>
public sealed partial class MinerHub
{
    private SecretStore? _secrets;
    private bool _backupBusy;
    private BackupStatus? _backupStatus;
    /// <summary>Echte Startzeit (nicht die Test-Uhr): geplante Sicherung frühestens 5 min danach – schont den Pi beim Hochfahren.</summary>
    internal DateTime BackupNotBefore { get; set; } = DateTime.Now.AddMinutes(5);

    public SecretStore Secrets => _secrets ??= new SecretStore(DataDirectory);

    public string BackupDirectory => Path.Combine(DataDirectory, "auto-backups");
    private string BackupStatusFile => Path.Combine(BackupDirectory, "status.json");

    public BackupStatus BackupStatus
    {
        get
        {
            if (_backupStatus is not null) return _backupStatus;
            try { _backupStatus = File.Exists(BackupStatusFile) ? JsonSerializer.Deserialize<BackupStatus>(File.ReadAllText(BackupStatusFile)) : null; }
            catch { _backupStatus = null; }
            return _backupStatus ??= new BackupStatus(null, null, null, [], false);
        }
        private set
        {
            _backupStatus = value;
            if (value.Running) return;
            try
            {
                Directory.CreateDirectory(BackupDirectory);
                File.WriteAllText(BackupStatusFile, JsonSerializer.Serialize(value));
            }
            catch { /* nicht kritisch */ }
        }
    }

    /// <summary>Sicherungen im Datenordner, neueste zuerst.</summary>
    public IReadOnlyList<FileInfo> LocalBackups() => Directory.Exists(BackupDirectory)
        ? new DirectoryInfo(BackupDirectory).EnumerateFiles().Where(f => BackupNames.IsBackup(f.Name)).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
        : [];

    /// <summary>Zusätzliche Ziele laut Einstellungen.</summary>
    public IReadOnlyList<IBackupTarget> BackupTargets()
    {
        var s = Config.Backup;
        var list = new List<IBackupTarget>();
        if (s.Folder.Enabled && s.Folder.Path.Trim() is { Length: > 0 } path) list.Add(new FolderBackupTarget(L.T("Ordner/USB"), path));
        if (s.Smb.Enabled) list.Add(new SmbBackupTarget(s.Smb, Secrets.Get(SecretStore.SmbPassword) ?? ""));
        return list;
    }

    /// <summary>Einmal täglich ab der eingestellten Stunde (aus der Abfragerunde).</summary>
    internal void CheckBackup(DateTime now)
    {
        var s = Config.Backup;
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!s.Enabled || _backupBusy || DateTime.Now < BackupNotBefore || now.Hour < Math.Clamp(s.Hour, 0, 23) || s.LastRun == today) return;
        s.LastRun = today; // vorher merken: ein Fehler wird gemeldet, aber nicht im Minutentakt wiederholt
        Config.Save();
        _ = RunBackupAsync(now);
    }

    /// <summary>Sicherung jetzt erstellen und verteilen (auch „Jetzt sichern“).</summary>
    public async Task<BackupStatus> RunBackupAsync(DateTime now, CancellationToken ct = default)
    {
        if (_backupBusy) throw new InvalidOperationException(L.T("Eine Sicherung läuft bereits."));
        _backupBusy = true;
        var s = Config.Backup;
        var results = new List<BackupTargetStatus>();
        string? name = null;
        var ok = true;
        BackupStatus = BackupStatus with { Running = true };
        var tmp = "";
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            name = BackupNames.For(now);
            tmp = Path.Combine(BackupDirectory, name + ".tmp");
            Config.Save();
            var dir = DataDirectory;
            var history = History;
            var version = typeof(MinerHub).Assembly.GetName().Version?.ToString(3) ?? "?";
            var manifest = await Task.Run(() =>
            {
                using (var fs = File.Create(tmp)) DataArchive.Create(dir, history, fs, "backup", version);
                // Einmal vollständig prüfen, bevor die Datei als Sicherung gilt
                var staging = Path.Combine(Path.GetTempPath(), "bt-backup-check-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var check = File.OpenRead(tmp);
                    return DataArchive.ExtractAndVerify(check, staging);
                }
                finally
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(staging, true); } catch { /* egal */ }
                }
            }, ct);
            var local = Path.Combine(BackupDirectory, name);
            File.Move(tmp, local);
            foreach (var old in BackupNames.Surplus(LocalBackups().Select(f => f.Name), s.LocalKeep))
                File.Delete(Path.Combine(BackupDirectory, old));
            var size = new FileInfo(local).Length / 1024.0 / 1024.0;
            results.Add(new BackupTargetStatus(L.T("Datenordner"), true,
                L.T("{0} MB, {1:N0} Verlaufswerte, geprüft", size.ToString("0.0", L.Culture), manifest.HistoryRows.GetValueOrDefault("samples"))));

            foreach (var target in BackupTargets())
            {
                try
                {
                    await target.UploadAsync(local, name, ct);
                    var removed = 0;
                    foreach (var old in BackupNames.Surplus(await target.ListAsync(ct), s.Keep))
                    {
                        await target.DeleteAsync(old, ct);
                        removed++;
                    }
                    results.Add(new BackupTargetStatus(target.Name, true, removed > 0 ? L.T("abgelegt, {0} alte gelöscht", removed) : L.T("abgelegt und geprüft")));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ok = false;
                    results.Add(new BackupTargetStatus(target.Name, false, ex.Message));
                    SendAlert(new Alert($"backup-failed:{target.Name}", L.T("Sicherung fehlgeschlagen"), $"{target.Name}: {ex.Message}", NotifyPriority.High, TimeSpan.FromHours(20), NotifyCategory.Maintenance));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            results.Add(new BackupTargetStatus(L.T("Datenordner"), false, ex.Message));
            SendAlert(new Alert("backup-failed:local", L.T("Sicherung fehlgeschlagen"), ex.Message, NotifyPriority.High, TimeSpan.FromHours(20), NotifyCategory.Maintenance));
            name = null;
        }
        finally
        {
            if (tmp.Length > 0) try { File.Delete(tmp); } catch { /* egal */ }
            _backupBusy = false;
        }
        BackupStatus = new BackupStatus(now, ok, name, results, false);
        LogEvent(null, EventCategories.System, ok ? L.T("Sicherung erstellt: {0}", name) : L.T("Sicherung mit Fehlern: {0}",
            string.Join("; ", results.Where(x => !x.Ok).Select(x => $"{x.Target}: {x.Message}"))));
        RaiseStatus(ok, ok ? L.T("Sicherung erstellt: {0}", name) : L.T("Sicherung mit Fehlern – siehe Einstellungen → Sicherung."));
        return BackupStatus;
    }
}
