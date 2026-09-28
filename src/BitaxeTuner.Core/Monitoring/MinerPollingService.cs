using System.Net.Http;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Zentrale AxeOS-Abfrage für alle Miner. Ersetzt die getrennten Poll-Schleifen von BitaxeMonitor
/// (MainWindow.PollAsync) und BitaxeTuner (Timer je Gerät). Jeder Miner hat genau eine
/// <see cref="MinerConnection"/>; Überwachung, Watchdog und Tuning teilen sie sich.
///
/// <see cref="PollOnceAsync"/> ruft die Geräte parallel ab (Timeout wie bisher 15 s gesamt),
/// aktualisiert die <see cref="MinerState"/>s aber erst danach im Kontext des Aufrufers –
/// vom UI-Thread aufgerufen, laufen alle Zustandsänderungen dort (wie im BitaxeMonitor).
/// </summary>
public sealed class MinerPollingService : IDisposable
{
    private readonly Func<string, IMinerClient> _clientFactory;
    private readonly List<(MinerState State, MinerConnection Connection)> _devices = new();
    private bool _busy;

    public MinerPollingService(MaintenanceTracker maintenance, Func<string, IMinerClient> clientFactory)
    {
        Maintenance = maintenance;
        _clientFactory = clientFactory;
    }

    public MaintenanceTracker Maintenance { get; }

    /// <summary>Für jede Tuning-Änderung eines beliebigen Miners.</summary>
    public event Action<TuningEvent>? TuningApplied;

    public IReadOnlyList<MinerState> States => _devices.Select(d => d.State).ToList();

    public MinerConnection? Connection(string host) =>
        _devices.FirstOrDefault(d => string.Equals(d.State.Config.Host, host, StringComparison.OrdinalIgnoreCase)).Connection;

    public MinerState? State(string host) =>
        _devices.FirstOrDefault(d => string.Equals(d.State.Config.Host, host, StringComparison.OrdinalIgnoreCase)).State;

    /// <summary>
    /// An die (geänderte) Geräteliste angleichen. Vorhandene Zustände und Verläufe bleiben erhalten,
    /// die Reihenfolge folgt der Konfiguration.
    /// </summary>
    public void Sync(IReadOnlyList<DeviceConfig> devices)
    {
        // Abgleich über den Host: das Einstellungsfenster arbeitet auf Kopien der DeviceConfig,
        // ein Referenzvergleich würde sonst nach jedem Speichern alle Live-Verläufe verwerfen.
        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        foreach (var gone in _devices.Where(d => !devices.Any(c => Same(c.Host, d.State.Config.Host))).ToList())
        {
            _devices.Remove(gone);
            gone.Connection.TuningApplied -= OnTuningApplied;
            gone.Connection.Dispose();
        }

        foreach (var device in devices)
        {
            var existing = _devices.FindIndex(d => Same(d.State.Config.Host, device.Host));
            if (existing >= 0)
            {
                _devices[existing].State.Config = device; // Name, Wallet, Coin usw. übernehmen
                continue;
            }
            _devices.Add((new MinerState(device), CreateConnection(device.Host)));
        }

        _devices.Sort((a, b) => IndexOf(devices, a.State.Config).CompareTo(IndexOf(devices, b.State.Config)));
    }

    private static int IndexOf(IReadOnlyList<DeviceConfig> list, DeviceConfig item)
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
        return int.MaxValue;
    }

    private MinerConnection CreateConnection(string host)
    {
        var connection = new MinerConnection(_clientFactory(host.Trim()), Maintenance);
        connection.TuningApplied += OnTuningApplied;
        return connection;
    }

    private void OnTuningApplied(TuningEvent e) => TuningApplied?.Invoke(e);

    /// <summary>Alle Miner einmal abfragen. Gibt false zurück, wenn noch eine Runde läuft.</summary>
    public async Task<bool> PollOnceAsync()
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            var snapshot = _devices.ToList();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var results = await Task.WhenAll(snapshot.Select(d => FetchAsync(d.Connection, cts.Token)));

            // Zurück im Kontext des Aufrufers: Zustände aktualisieren
            var now = DateTime.Now;
            for (var i = 0; i < snapshot.Count; i++)
            {
                var state = snapshot[i].State;
                var (info, error) = results[i];
                if (info is not null)
                {
                    state.Normalized = info;
                    state.Info = info.Details ?? SystemInfo.FromMinerInfo(info);
                    state.Error = null;
                    state.LastOk = now;
                    state.History.Add(new Sample(now, state.Info.hashRate, state.Info.temp, state.Info.power));
                }
                else
                {
                    state.Error = error;
                }
            }
            return true;
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<(MinerInfo? Info, string? Error)> FetchAsync(MinerConnection connection, CancellationToken ct)
    {
        try
        {
            return (await connection.GetInfoAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (null, Shorten(ex));
        }
    }

    /// <summary>Kurztext wie im BitaxeMonitor.</summary>
    public static string Shorten(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => L.T("Zeitüberschreitung"),
        HttpRequestException => L.T("keine Verbindung"),
        MinerApiException { InnerException: TaskCanceledException } => L.T("Zeitüberschreitung"),
        MinerApiException { InnerException: HttpRequestException } => L.T("keine Verbindung"),
        _ => ex.Message
    };

    public void Dispose()
    {
        foreach (var d in _devices)
        {
            d.Connection.TuningApplied -= OnTuningApplied;
            d.Connection.Dispose();
        }
        _devices.Clear();
    }
}
