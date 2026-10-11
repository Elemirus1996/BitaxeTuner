using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Laufendes Server-Update: Kennung, Version, Auslöser („Browser“, „Desktop-App“ …), Beginn und Schritt.</summary>
/// <param name="Phase">„backup“, „download“, „install“ oder „restart“.</param>
public sealed record UpdateRun(string Id, string Version, string By, DateTime StartedUtc, string Phase)
{
    /// <summary>Kommt aus der Sicherung nichts mehr (Fenster geschlossen, Absturz), gibt sich die Sperre selbst frei.</summary>
    public bool IsStale(DateTime nowUtc) => nowUtc - StartedUtc > (Phase == "backup" ? MinerHub.UpdateStaleBackup : MinerHub.UpdateStale);
}

/// <summary>
/// 0.9.15: Sperre für Server-Updates – egal ob aus Browser, Desktop-App oder einem zweiten Gerät ausgelöst, läuft immer nur
/// ein Update. Wer die Sperre hat, bekommt eine Kennung und darf damit weitermachen (Browser: erst Sicherung, dann
/// Installation); alle anderen sehen „läuft bereits“ und keinen Knopf. Nach einem Fehler wird freigegeben, nach dem
/// Neustart gibt es die Sperre ohnehin nicht mehr.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan UpdateStale = TimeSpan.FromHours(2);
    public static readonly TimeSpan UpdateStaleBackup = TimeSpan.FromMinutes(45);
    private readonly object _updateLock = new();
    private UpdateRun? _updateRun;

    /// <summary>Gerade laufendes Update (null = keines).</summary>
    public UpdateRun? RunningUpdate
    {
        get
        {
            lock (_updateLock)
                return _updateRun is { } r && !r.IsStale(DateTime.UtcNow) ? r : null;
        }
    }

    /// <summary>
    /// Update beginnen bzw. fortsetzen. Mit <paramref name="id"/> der eigenen Sperre geht es weiter; läuft ein anderes Update,
    /// ist das Ergebnis false und <paramref name="run"/> sagt, welches.
    /// </summary>
    public bool TryBeginUpdate(string version, string by, string? id, out UpdateRun run)
    {
        lock (_updateLock)
        {
            if (_updateRun is { } r && !r.IsStale(DateTime.UtcNow))
            {
                run = r;
                return id is not null && id == r.Id;
            }
            _updateRun = run = new UpdateRun(Guid.NewGuid().ToString("N"), version, by, DateTime.UtcNow, "backup");
        }
        LogEvent(null, EventCategories.System, L.T("Update auf {0} gestartet ({1}) – weitere Update-Knöpfe sind gesperrt, bis es durch ist.", version, by));
        return true;
    }

    public void SetUpdatePhase(string phase)
    {
        lock (_updateLock)
            if (_updateRun is { } r) _updateRun = r with { Phase = phase, StartedUtc = phase == "backup" ? r.StartedUtc : DateTime.UtcNow };
    }

    /// <summary>Sperre aufheben (nach Fehler). Mit <paramref name="id"/> nur die eigene. <paramref name="error"/> landet im Protokoll.</summary>
    public void EndUpdate(string? error, string? id = null)
    {
        UpdateRun? r;
        lock (_updateLock)
        {
            r = _updateRun;
            if (r is null || (id is not null && id != r.Id)) return;
            _updateRun = null;
        }
        LogEvent(null, EventCategories.System, L.T("Update auf {0} abgebrochen: {1} – Update-Knopf wieder frei.", r.Version, error ?? L.T("ohne Angabe")));
    }

    public static string UpdatePhaseText(string phase) => phase switch
    {
        "backup" => L.T("Sicherung"),
        "download" => L.T("Herunterladen und prüfen"),
        "install" => L.T("Installieren"),
        "restart" => L.T("Neustart"),
        _ => phase,
    };
}
