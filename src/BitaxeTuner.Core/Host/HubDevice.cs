using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Core.Host;

/// <summary>Vorschlag nach einem fehlgeschlagenen Dauertest (wird erst nach Bestätigung angewendet).</summary>
public sealed record SoakSuggestion(int FrequencyMhz, int CoreVoltageMv, string Reason);

/// <summary>
/// Laufzeitzustand eines Miners im <see cref="MinerHub"/>: erkanntes Profil, Automatik- und Dauertest-Status,
/// Benchmark und Geräteprotokoll. Desktop (DeviceViewModel) und Server-API lesen hieraus; die Oberfläche hält
/// keine eigene Logik mehr.
/// </summary>
public sealed class HubDevice
{
    public const int MaxLogLines = 2000;

    private readonly object _logLock = new();
    private readonly List<string> _log = new();

    internal HubDevice(MinerState state, MinerConnection connection, DeviceProfile profile)
    {
        State = state;
        Connection = connection;
        Profile = profile;
    }

    public MinerState State { get; internal set; }
    public MinerConnection Connection { get; }
    public string Host => Connection.Address;

    /// <summary>Gerät aus der gemeinsamen Geräteliste (config.json) – nach "Einstellungen speichern" eine neue Kopie.</summary>
    public DeviceConfig Config => State.Config;
    public MinerInfo? Info => State.Online ? State.Normalized : null;
    public bool IsSimulated => Connection.Inner is SimulatedMinerClient;
    public string Title => !string.IsNullOrWhiteSpace(Config.Name) ? Config.Name : State.Normalized?.DisplayName ?? Host;

    /// <summary>Aktives Profil (erkannt oder vom Benutzer gewählt).</summary>
    public DeviceProfile Profile { get; internal set; }

    /// <summary>Automatisch erkanntes Profil (evtl. mit Gerätewerten angepasst), ersetzt seinen Registry-Eintrag in der Auswahl.</summary>
    public DeviceProfile? MatchedProfile { get; internal set; }
    public bool ProfileResolved { get; internal set; }

    /// <summary>
    /// Profil steht fest (aus den Einstellungen, erkannt oder gewählt). Vorher gilt nur der Platzhalter „Generisch“ –
    /// Automatik und Dauertest warten so lange, sonst schlagen dessen enge Grenzen fälschlich an.
    /// </summary>
    public bool ProfileKnown { get; internal set; }

    /// <summary>Zustand der Automatik-Regeln; leer = keine Regel eingeschaltet.</summary>
    public string AutomationStatus { get; internal set; } = "";
    public string SoakStatus { get; internal set; } = "";
    public bool SoakActive => Config.Soak is not null;

    /// <summary>Offener Vorschlag nach fehlgeschlagenem Dauertest.</summary>
    public SoakSuggestion? PendingSuggestion { get; internal set; }

    /// <summary>Nach einer bestätigten Änderung: Dauertest starten, sobald der Miner wieder läuft (Stunden, Zeitpunkt).</summary>
    public (int Hours, DateTime Since)? PendingSoak { get; internal set; }

    /// <summary>Laufender oder zuletzt beendeter Benchmark (null: in dieser Sitzung keiner gestartet).</summary>
    public BenchmarkRun? Benchmark { get; internal set; }
    public bool IsBenchmarkRunning => Benchmark?.IsRunning == true;

    /// <summary>Neue Protokollzeile (fertig formatiert, kann auf einem beliebigen Thread kommen).</summary>
    public event Action<string>? LogAdded;

    public IReadOnlyList<string> LogLines
    {
        get { lock (_logLock) return _log.ToList(); }
    }

    /// <summary>Für das dauerhaften Protokoll: Kategorie und Text (nur Zeilen mit Kategorie).</summary>
    public event Action<string, string>? Logged;

    /// <param name="category">Kategorie für das dauerhafte Protokoll (<see cref="EventCategories"/>); null = nur hier in der Sitzung.</param>
    public void AddLog(string message, string? category = EventCategories.Other)
    {
        if (category is not null) Logged?.Invoke(category, message);
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_logLock)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        LogAdded?.Invoke(line);
    }

    internal SoakMonitor? SoakMonitor { get; set; }
    internal bool Applying { get; set; }
}
