using System.Net.Http;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.25: Kann dieser Miner aktualisiert werden? <see cref="Reason"/> = warum nicht (null = ja).</summary>
public sealed record FirmwareCandidate(string Host, string Name, string? Installed, string? Target, bool Prerelease, bool Beta,
    string? ReleaseUrl, string? Reason)
{
    public bool CanUpdate => Reason is null;
}

/// <summary>0.9.25: Ein Miner im Firmware-Update – Status: waiting, backup, download, www, firmware, restart, verify, done, changed, failed, skipped, cancelled.</summary>
public sealed class FirmwareJobItem
{
    public string Host { get; init; } = "";
    public string Name { get; init; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string Status { get; set; } = "waiting";
    public string? Message { get; set; }
    /// <summary>Einstellungen, die nach dem Update anders sind als in der Sicherung davor (alt → neu).</summary>
    public List<SettingChange> Changes { get; set; } = [];
    public string? SnapshotFile { get; set; }
    public DateTime? Finished { get; set; }
}

/// <summary>0.9.25: Firmware-Update mehrerer Miner – einer nach dem anderen.</summary>
public sealed class FirmwareJob
{
    public DateTime Started { get; init; }
    public DateTime? Finished { get; set; }
    public string Who { get; init; } = "";
    public List<FirmwareJobItem> Items { get; init; } = [];
    public bool CancelRequested { get; set; }
    public bool Running => Finished is null;
}

/// <summary>
/// 0.9.25 Firmware-Update für alle Miner nacheinander (nur per Knopf): nur offizielle Releases von ESP-Miner (AxeOS) und
/// NerdQAxe – Vorabversionen nur bei Minern mit „Beta-Versionen erlauben“. Je Miner: Einstellungen sichern, Dateien laden
/// und prüfen (Größe, SHA-256 von GitHub, ESP32-Kennung), Weboberfläche (falls im Release) und Firmware aufspielen, auf die
/// neue Version warten, Hashrate prüfen und die Einstellungen mit der Sicherung vergleichen. Abweichungen werden nur
/// gemeldet – zurückgespielt wird erst nach Bestätigung. Bei einem Fehler hält der Ablauf an; die übrigen Miner bleiben
/// unangetastet. Watchdog und Offline-Meldung ruhen während des Updates (Wartungsfenster).
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan FirmwareRebootWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirmwareHashWait = TimeSpan.FromMinutes(10);

    private static readonly Lazy<HttpClient> FirmwareHttp = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
        return http;
    });

    private FirmwareJob? _firmwareJob;

    /// <summary>Laufendes oder zuletzt beendetes Firmware-Update (null: in dieser Sitzung keins).</summary>
    public FirmwareJob? FirmwareJobState => _firmwareJob;

    /// <summary>Fortschritt des Firmware-Updates hat sich geändert.</summary>
    public event Action? FirmwareJobChanged;

    /// <summary>Repository, aus dem aktualisiert wird – immer das offizielle der erkannten Firmware, nie ein eingetragenes eigenes.</summary>
    public static string? FirmwareUpdateRepoOf(MinerState s) => s.Normalized?.Firmware switch
    {
        FirmwareKind.AxeOS => DefaultFirmwareRepo,
        FirmwareKind.NerdQAxe => NerdQAxeFirmwareRepo,
        _ => null,
    };

    public List<FirmwareCandidate> FirmwareCandidates() => Devices.Select(FirmwareCandidateOf).ToList();

    public FirmwareCandidate FirmwareCandidateOf(HubDevice d)
    {
        var s = d.State;
        var info = s.Normalized;
        var installed = info?.FirmwareVersion;
        var repo = FirmwareUpdateRepoOf(s);
        var rel = Firmware.Target(repo, d.Config.FirmwareBeta);
        string? reason = null;
        if (d.IsSimulated) reason = L.T("Demo-Miner");
        else if (!s.Online || info is null) reason = L.T("offline");
        else if (d.MonitorOnly) reason = L.T("nur überwachen ({0})", d.MonitorOnlyReason ?? "");
        else if (repo is null) reason = L.T("Firmware-Update gibt es nur für ESP-Miner (AxeOS) und NerdQAxe.");
        else if (rel is null) reason = L.T("Liste der Releases noch nicht geladen – Internetverbindung prüfen und später erneut versuchen.");
        else if (string.IsNullOrWhiteSpace(installed) || FirmwareChecker.IsNewer(rel.Tag, installed) is null) reason = L.T("Version des Miners unbekannt");
        else if (FirmwareChecker.IsNewer(rel.Tag, installed) == false) reason = L.T("aktuell");
        else if (FirmwareFiles.Select(info.Firmware, info.DeviceModel, rel, out var fileError) is null) reason = fileError;
        else if (d.IsBenchmarkRunning) reason = L.T("Benchmark läuft");
        else if (d.SoakActive) reason = L.T("Dauertest läuft");
        return new FirmwareCandidate(d.Host, d.Title, installed, rel?.Tag, rel?.Prerelease == true, d.Config.FirmwareBeta, rel?.Url, reason);
    }

    /// <summary>Firmware-Update für die gewählten Miner starten (einer nach dem anderen). Nicht aktualisierbare werden mit Grund übersprungen.</summary>
    public FirmwareJob StartFirmwareUpdate(IEnumerable<string> hosts, string who)
    {
        if (_firmwareJob is { Running: true }) throw new LocalizedException("Es läuft bereits ein Firmware-Update.");
        var wanted = hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<FirmwareJobItem>();
        foreach (var host in wanted)
        {
            if (Device(host) is not { } d) continue;
            var c = FirmwareCandidateOf(d);
            items.Add(new FirmwareJobItem
            {
                Host = d.Host, Name = d.Title, From = c.Installed, To = c.Target,
                Status = c.CanUpdate ? "waiting" : "skipped", Message = c.Reason,
            });
        }
        if (!items.Any(i => i.Status == "waiting")) throw new LocalizedException("Keiner der ausgewählten Miner kann aktualisiert werden.");
        var job = new FirmwareJob { Started = Now(), Who = who, Items = items };
        _firmwareJob = job;
        LogEvent(null, EventCategories.Settings, L.T("Firmware-Update gestartet ({0}): {1}", who,
            string.Join(", ", items.Where(i => i.Status == "waiting").Select(i => $"{i.Name} {i.From} → {i.To}"))));
        FirmwareJobChanged?.Invoke();
        _ = RunFirmwareJobAsync(job);
        return job;
    }

    /// <summary>Nach dem gerade laufenden Miner anhalten; die übrigen bleiben unverändert.</summary>
    public void CancelFirmwareUpdate(string who)
    {
        if (_firmwareJob is not { Running: true } job) return;
        job.CancelRequested = true;
        LogEvent(null, EventCategories.Settings, L.T("Firmware-Update: Abbruch angefordert ({0}) – der laufende Miner wird noch fertig.", who));
        FirmwareJobChanged?.Invoke();
    }

    private async Task RunFirmwareJobAsync(FirmwareJob job)
    {
        var downloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var item in job.Items.Where(i => i.Status == "waiting").ToList())
            {
                if (job.CancelRequested)
                {
                    SetItem(item, "cancelled", L.T("abgebrochen"));
                    continue;
                }
                if (await UpdateOneFirmwareAsync(item, downloads)) continue;
                foreach (var rest in job.Items.Where(i => i.Status == "waiting"))
                    SetItem(rest, "skipped", L.T("angehalten nach dem Fehler bei {0}", item.Name));
                break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var rest in job.Items.Where(i => i.Status is not ("done" or "changed" or "failed" or "skipped" or "cancelled")))
                SetItem(rest, "failed", ex.Message);
        }
        job.Finished = Now();
        var done = job.Items.Count(i => i.Status is "done" or "changed");
        var failed = job.Items.Where(i => i.Status == "failed").ToList();
        var summary = failed.Count == 0
            ? L.T("{0} Miner aktualisiert.", done)
            : L.T("{0} Miner aktualisiert, Fehler bei {1}: {2}", done, failed[0].Name, failed[0].Message ?? "");
        if (job.Items.Any(i => i.Status == "changed"))
            summary += " " + L.T("Bei {0} weichen Einstellungen von der Sicherung ab – bitte prüfen.", string.Join(", ", job.Items.Where(i => i.Status == "changed").Select(i => i.Name)));
        LogEvent(null, EventCategories.Settings, L.T("Firmware-Update beendet: {0}", summary));
        if (Config.Notifications.Wants(NotifyCategory.Maintenance))
            SendAlert(new Alert($"fwupdate:{job.Started:O}", failed.Count == 0 ? L.T("Firmware-Update fertig") : L.T("Firmware-Update angehalten"),
                summary, failed.Count == 0 ? NotifyPriority.Low : NotifyPriority.High, TimeSpan.FromMinutes(1), NotifyCategory.Maintenance));
        FirmwareJobChanged?.Invoke();
    }

    private void SetItem(FirmwareJobItem item, string status, string? message = null)
    {
        item.Status = status;
        if (message is not null) item.Message = message;
        if (status is "done" or "changed" or "failed" or "skipped" or "cancelled") item.Finished = Now();
        FirmwareJobChanged?.Invoke();
    }

    private Task FirmwareWait(TimeSpan t) => (Options.FirmwareDelay ?? ((x, ct) => Task.Delay(x, ct)))(t, CancellationToken.None);

    private Task<byte[]> FirmwareDownloadAsync(string url) =>
        Options.FirmwareDownload?.Invoke(url, CancellationToken.None) ?? FirmwareHttp.Value.GetByteArrayAsync(url);

    /// <summary>Ein Miner; false = Fehler (der Ablauf hält an).</summary>
    private async Task<bool> UpdateOneFirmwareAsync(FirmwareJobItem item, Dictionary<string, byte[]> downloads)
    {
        if (Device(item.Host) is not { } d)
        {
            SetItem(item, "skipped", L.T("nicht mehr in der Geräteliste"));
            return true;
        }
        // Stand kann sich seit dem Start geändert haben (offline, Benchmark …) – erneut prüfen
        var candidate = FirmwareCandidateOf(d);
        if (!candidate.CanUpdate)
        {
            SetItem(item, "skipped", candidate.Reason);
            return true;
        }
        var info = d.State.Normalized!;
        var release = Firmware.Target(FirmwareUpdateRepoOf(d.State), d.Config.FirmwareBeta)!;
        var files = FirmwareFiles.Select(info.Firmware, info.DeviceModel, release, out _)!;
        item.From = info.FirmwareVersion;
        item.To = release.Tag;

        using var maintenance = d.Connection.BeginMaintenance(L.T("Firmware-Update"));
        string Fail(string why)
        {
            SetItem(item, "failed", why);
            d.AddLog(L.T("Firmware-Update fehlgeschlagen: {0}", why), EventCategories.Settings);
            return why;
        }
        try
        {
            SetItem(item, "backup", L.T("Einstellungen sichern …"));
            var snapshot = await BackupSettingsAsync(d, L.T("vor Firmware-Update"));
            item.SnapshotFile = snapshot.FilePath;

            SetItem(item, "download", L.T("{0} laden und prüfen …", release.Tag));
            async Task<byte[]?> Load(FirmwareAsset asset, bool isFirmware)
            {
                if (!downloads.TryGetValue(asset.DownloadUrl, out var data))
                {
                    data = await FirmwareDownloadAsync(asset.DownloadUrl);
                    if (FirmwareFiles.Verify(asset, data, isFirmware) is { } bad) { Fail(bad); return null; }
                    downloads[asset.DownloadUrl] = data;
                }
                return data;
            }
            byte[]? www = null;
            if (files.Www is { } wwwAsset && (www = await Load(wwwAsset, false)) is null) return false;
            if (await Load(files.Firmware, true) is not { } firmware) return false;

            d.AddLog(L.T("Firmware-Update {0} → {1} gestartet ({2})", item.From ?? "?", release.Tag, files.Firmware.Name), EventCategories.Settings);
            if (www is not null)
            {
                SetItem(item, "www", L.T("Weboberfläche aufspielen …"));
                await d.Connection.UploadFirmwareAsync(true, www);
                if (await WaitFirmwareOnlineAsync(d, null) is null)
                {
                    Fail(L.T("Nach dem Aufspielen der Weboberfläche nicht mehr erreichbar."));
                    return false;
                }
            }
            SetItem(item, "firmware", L.T("Firmware aufspielen …"));
            await d.Connection.UploadFirmwareAsync(false, firmware);

            SetItem(item, "restart", L.T("Neustart – warte auf {0} …", release.Tag));
            await FirmwareWait(TimeSpan.FromSeconds(20));
            var after = await WaitFirmwareOnlineAsync(d, release.Tag);
            if (after is null)
            {
                var now = await TryInfoAsync(d);
                Fail(now is null
                    ? L.T("Nach {0} Minuten nicht wieder erreichbar – bitte Strom und WLAN prüfen.", (int)FirmwareRebootWait.TotalMinutes)
                    : L.T("Läuft weiter mit {0} – das Update wurde nicht übernommen.", now.FirmwareVersion ?? "?"));
                return false;
            }

            SetItem(item, "verify", L.T("Prüfe Hashrate …"));
            var hashing = after.HashRateGh > 0;
            for (var waited = TimeSpan.Zero; !hashing && waited < FirmwareHashWait; waited += TimeSpan.FromSeconds(30))
            {
                await FirmwareWait(TimeSpan.FromSeconds(30));
                hashing = (await TryInfoAsync(d))?.HashRateGh > 0;
            }
            if (!hashing)
            {
                Fail(L.T("Läuft mit {0}, liefert aber nach {1} Minuten keine Hashrate – Pool und Einstellungen prüfen.", release.Tag, (int)FirmwareHashWait.TotalMinutes));
                return false;
            }

            var changes = SettingsSnapshots.Diff(snapshot, await d.Connection.GetRawInfoAsync());
            item.Changes = changes;
            if (changes.Count > 0)
            {
                var list = string.Join(", ", changes.Select(c => $"{c.Label} {c.Saved} → {c.Current}"));
                d.AddLog(L.T("Firmware {0} läuft – Einstellungen weichen von der Sicherung ab: {1}", release.Tag, list), EventCategories.Settings);
                SetItem(item, "changed", L.T("Aktualisiert, aber Einstellungen geändert: {0}", list));
            }
            else
            {
                d.AddLog(L.T("Firmware {0} läuft, Einstellungen unverändert", release.Tag), EventCategories.Settings);
                SetItem(item, "done", L.T("Aktualisiert auf {0}", release.Tag));
            }
            DeviceChanged?.Invoke(d);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex.Message);
            return false;
        }
    }

    private static async Task<MinerInfo?> TryInfoAsync(HubDevice d)
    {
        try { return await d.Connection.GetInfoAsync(); }
        catch (MinerApiException) { return null; }
    }

    /// <summary>Wartet, bis der Miner antwortet (und mit <paramref name="tag"/> mindestens diese Version meldet); null = Zeit abgelaufen.</summary>
    private async Task<MinerInfo?> WaitFirmwareOnlineAsync(HubDevice d, string? tag)
    {
        for (var waited = TimeSpan.Zero; waited <= FirmwareRebootWait; waited += TimeSpan.FromSeconds(10))
        {
            if (await TryInfoAsync(d) is { } info
                && (tag is null || (info.FirmwareVersion is { } v && FirmwareChecker.IsNewer(tag, v) == false)))
                return info;
            await FirmwareWait(TimeSpan.FromSeconds(10));
        }
        return null;
    }

    /// <summary>
    /// Nach dem Update abweichende Einstellungen aus der Sicherung davor zurückspielen – nur nach Bestätigung in der
    /// Oberfläche (dort werden alt → neu gezeigt). Frequenz/Spannung laufen über den protokollierten Weg und die Grenzwerte.
    /// </summary>
    public async Task RestoreAfterFirmwareAsync(string host, string who)
    {
        var item = _firmwareJob?.Items.FirstOrDefault(i => string.Equals(i.Host, host, StringComparison.OrdinalIgnoreCase) && i.Status == "changed")
                   ?? throw new LocalizedException("Für diesen Miner gibt es nichts zurückzuspielen.");
        var d = Device(item.Host) ?? throw new LocalizedException("Miner nicht gefunden.");
        var snapshot = Snapshots.List(d.Host).FirstOrDefault(s => s.FilePath == item.SnapshotFile)
                       ?? throw new LocalizedException("Die Sicherung vor dem Update ist nicht mehr vorhanden.");
        var changes = SettingsSnapshots.Diff(snapshot, await d.Connection.GetRawInfoAsync());
        await RestoreAsync(d, snapshot, changes);
        d.AddLog(L.T("Einstellungen nach dem Firmware-Update zurückgespielt ({0})", who), EventCategories.Settings);
        item.Changes = [];
        SetItem(item, "done", L.T("Aktualisiert auf {0}, Einstellungen zurückgespielt", item.To ?? ""));
    }

    /// <summary>„Was ist los?“: Firmware-Update dieses Miners ist in den letzten 24 Stunden fehlgeschlagen.</summary>
    private MinerProblem? FirmwareProblemOf(HubDevice d)
    {
        var item = _firmwareJob?.Items.FirstOrDefault(i => string.Equals(i.Host, d.Host, StringComparison.OrdinalIgnoreCase));
        if (item is not { Status: "failed", Finished: { } at } || Now() - at > TimeSpan.FromHours(24)) return null;
        return new MinerProblem("firmware", "warn", L.T("Firmware-Update fehlgeschlagen"), item.Message ?? "",
            L.T("AxeOS des Miners im Browser öffnen und die Version prüfen; das Update lässt sich dort von Hand wiederholen. Die Einstellungen von vor dem Update sind gesichert (Geräteseite → Sicherungen)."),
            "device:backups");
    }
}
