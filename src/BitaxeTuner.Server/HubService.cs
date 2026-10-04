using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Server;

/// <summary>
/// Betreibt den <see cref="MinerHub"/> auf einem eigenen <see cref="HubThread"/> – alle Takte und
/// Zustandsänderungen laufen dort nacheinander. API-Aufrufe kommen über <see cref="RunAsync{T}(Func{MinerHub, Task{T}})"/>.
/// Beim Beenden des Dienstes werden laufende Benchmarks gestoppt und die Einstellungen der Miner wiederhergestellt.
/// </summary>
public sealed class HubService : IHostedService, IDisposable
{
    private readonly HubThread _thread = new();
    private readonly ILogger<HubService> _log;
    private readonly MinerHubOptions? _options;
    private readonly string _stateFile;
    private Task? _stopping;
    private int _disposed;

    public HubService(ServerSettings settings, ILogger<HubService> log, MinerHubOptions? options = null)
    {
        _log = log;
        _options = options;
        Settings = settings;
        Directory.CreateDirectory(settings.DataDirectory);
        _stateFile = Path.Combine(settings.DataDirectory, "server-state.json");
        // Ein Prozess = ein Datenordner: alle Pfade (auch config.json) zeigen dorthin
        DataPaths.Override(settings.DataDirectory);
        Hub = _thread.RunAsync(CreateHub).GetAwaiter().GetResult();
    }

    public ServerSettings Settings { get; }

    /// <summary>Aktueller Motor. Wird nach einer Datenübernahme ersetzt (<see cref="HubReplaced"/>).</summary>
    public MinerHub Hub { get; private set; }

    /// <summary>Neuer Motor nach Datenübernahme (im Hub-Kontext).</summary>
    public event Action<MinerHub>? HubReplaced;

    private MinerHub CreateHub()
    {
        var file = Path.Combine(Settings.DataDirectory, "config.json");
        var config = AppConfig.Load(file);
        config.FilePath = file;
        Core.I18n.Loc.Configure(config.Language); // Sprache für Push, Tagesbericht, E-Paper, Protokoll
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = Settings.DataDirectory,
            ClientFactory = _options?.ClientFactory,
            OnlineChecks = _options?.OnlineChecks ?? true,
            BenchmarkDelay = _options?.BenchmarkDelay,
            FanDeviceFactory = _options?.FanDeviceFactory,
            SystemReboot = _options?.SystemReboot ?? DefaultReboot(),
            // QR-Code auf dem E-Paper: diese Oberfläche unter der ersten Adresse im Heimnetz
            WebUrl = () => Core.Discovery.NetworkScanner.LocalIPv4Addresses().FirstOrDefault(Security.NetworkRules.IsPrivate) is { } ip
                ? $"{(Settings.Https ? "https" : "http")}://{ip}:{Settings.Port}/" : null,
            Clock = _options?.Clock,
        });
        if (hub.HistoryError is { } error) _log.LogError("Verlaufsdatenbank nicht verfügbar: {Error}", error);
        // Audit N-E2: gleiche Warnung (z. B. „0/3 online“ nachts) nur bei Änderung loggen – nicht bei jedem Abfragetakt
        // (~17 000 Zeilen am Tag auf der SD-Karte). Die Uhrzeit am Ende zählt dabei nicht als Änderung.
        string? lastWarning = null;
        hub.StatusMessage += (ok, text) =>
        {
            if (ok) { lastWarning = null; return; }
            var key = WarningKey(text);
            if (key == lastWarning) return;
            lastWarning = key;
            _log.LogWarning("{Text}", text);
        };
        if (LoadPaused()) hub.SetPaused(true);
        return hub;
    }

    /// <summary>Statusmeldung ohne angehängte Uhrzeit („… – 22:38:08“) – zum Erkennen gleicher Warnungen.</summary>
    internal static string WarningKey(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s*[–-]\s*\d{1,2}:\d{2}(:\d{2})?\s*$", "");

    /// <summary>
    /// Rechner-Neustart nur bei der Installation per install.sh/Pi-Image: Der Dienst (ohne Root-Rechte) legt
    /// &lt;Datenordner&gt;/reboot-request an, die systemd-Pfad-Unit „bitaxetuner-reboot.path“ startet daraufhin neu.
    /// Windows-Dienst, Docker und von Hand gestartete Server starten nur den Pico neu.
    /// </summary>
    private Func<Task>? DefaultReboot()
    {
        if (!OperatingSystem.IsLinux() || ServerUpdater.DetectKind() != InstallKind.LinuxPackage) return null;
        return async () =>
        {
            _log.LogWarning("Neustart des Rechners angefordert (Taste 4 / Browser).");
            await Task.Delay(1500); // Protokoll und Antworten noch rausgeben
            await File.WriteAllTextAsync(Path.Combine(Settings.DataDirectory, "reboot-request"), DateTime.Now.ToString("O"));
        };
    }

    /// <summary>Im Hub-Kontext ausführen (einziger erlaubter Zugriff auf Hub-Zustände von außen).</summary>
    public Task<T> RunAsync<T>(Func<MinerHub, Task<T>> action) => _thread.RunAsync(() => action(Hub));

    public Task<T> RunAsync<T>(Func<MinerHub, T> action) => _thread.RunAsync(() => action(Hub));

    public Task RunAsync(Func<MinerHub, Task> action) => _thread.RunAsync(() => action(Hub));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _thread.RunAsync(() => Hub.StartAsync());
        _log.LogInformation("Motor gestartet: {Count} Miner, Datenordner {Dir}{Paused}", Hub.Devices.Count, Settings.DataDirectory,
            Hub.IsPaused ? " – PAUSIERT (Desktop-App fragt selbst ab)" : "");
    }

    // ---------- Pause (Schutz gegen Doppelbetrieb mit der Desktop-App) ----------

    /// <summary>Motor pausieren/fortsetzen; bleibt über Neustarts des Dienstes erhalten.</summary>
    public Task SetPausedAsync(bool paused) => _thread.RunAsync(async () =>
    {
        if (paused && Hub.Benchmarks.AnyRunning) await Hub.Benchmarks.StopAllAsync();
        Hub.SetPaused(paused);
        if (!paused) await Hub.PollNowAsync();
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(new { paused }));
        _log.LogInformation(paused ? "Motor pausiert." : "Motor läuft wieder.");
        return true;
    });

    private bool LoadPaused()
    {
        try
        {
            return File.Exists(_stateFile) &&
                   JsonDocument.Parse(File.ReadAllText(_stateFile)).RootElement.TryGetProperty("paused", out var p) && p.GetBoolean();
        }
        catch { return false; }
    }

    // ---------- Datenübernahme ----------

    /// <summary>Leer = keine Geräte, kein Verlauf, keine Zuflüsse. Nur dann ist eine Übernahme ohne Rückfrage erlaubt.</summary>
    public Task<bool> IsEmptyAsync() => RunAsync(h =>
        h.Config.Devices.Count == 0 &&
        (h.History?.CountRows().GetValueOrDefault("samples") ?? 0) == 0 &&
        h.TaxMonitor.LoadRewards().Count == 0);

    /// <summary>
    /// Geprüfte Daten aus <paramref name="stagingDirectory"/> übernehmen: Benchmarks stoppen, Motor anhalten und freigeben,
    /// Datenordner sichern und ersetzen, Motor neu starten. Liefert den Sicherungsordner.
    /// </summary>
    public async Task<string> ReplaceDataAsync(string stagingDirectory)
    {
        string backup = "";
        await _thread.RunAsync(async () =>
        {
            await Hub.Benchmarks.StopAllAsync();
            Hub.SetPaused(true);
            Hub.Config.Save();
            Hub.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                backup = DataArchive.Apply(stagingDirectory, Settings.DataDirectory, (old, fresh) =>
                {
                    fresh.Server = new ServerConnectionSettings(); // Verbindungsdaten der Desktop-App gehören nicht auf den Server
                    fresh.Backup = old.Backup;                      // Sicherungsziele gehören zum Gerät (Pfade, NAS)
                    fresh.Mqtt = old.Mqtt;                          // MQTT-Verbindung ebenso (Passwort liegt ohnehin getrennt)
                });
            }
            finally
            {
                // Auch bei einem Fehler weiterlaufen (mit dem, was jetzt im Ordner liegt – die Sicherung bleibt erhalten)
                Hub = CreateHub();
                HubReplaced?.Invoke(Hub);
                await Hub.StartAsync();
            }
            return true;
        });
        _log.LogWarning("Daten übernommen. Vorheriger Stand gesichert in {Backup}", backup);
        return backup;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _stopping ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        await _thread.RunAsync(async () =>
        {
            Hub.SetPaused(true);
            if (Hub.Benchmarks.AnyRunning)
            {
                _log.LogInformation("Stoppe laufende Benchmarks und stelle die Einstellungen wieder her …");
                await Hub.Benchmarks.StopAllAsync();
            }
            Hub.Config.Save();
        });
    }

    public void Dispose()
    {
        // Der Container gibt den Dienst ggf. zweimal frei (Singleton und Hosted Service)
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            // Erst ein laufendes Stoppen (Benchmarks wiederherstellen) abschließen lassen
            _stopping?.Wait(TimeSpan.FromSeconds(60));
            _thread.RunAsync(() => { Hub.Dispose(); return true; }).Wait(TimeSpan.FromSeconds(10));
        }
        catch { /* beim Beenden egal */ }
        _thread.Dispose();
    }
}
