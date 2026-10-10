using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.Update;
using BitaxeTuner.Core.Web;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

public sealed class MinerHubOptions
{
    /// <summary>GitHub-Repository für Update-Prüfungen.</summary>
    public string UpdateRepository { get; init; } = "Elemirus1996/BitaxeTuner";

    /// <summary>Nur für Tests: eigene Miner-Clients statt AxeOS.</summary>
    public Func<string, IMinerClient>? ClientFactory { get; init; }

    /// <summary>Online-Abfragen: Firmware-Releases (GitHub) und Wallet-Stände. Tests: aus, damit keine Netzzugriffe entstehen.</summary>
    public bool OnlineChecks { get; init; } = true;

    /// <summary>Nur für Tests: Uhr für die Auswertung nach jeder Runde (Haltezeiten der Regeln).</summary>
    public Func<DateTime>? Clock { get; init; }

    /// <summary>Rechner neu starten (Server auf dem Pi: systemctl reboot). Null = nicht verfügbar.</summary>
    public Func<Task>? SystemReboot { get; init; }

    /// <summary>Adresse der Browser-Oberfläche im Heimnetz (QR-Code auf dem E-Paper); vom Server gesetzt.</summary>
    public Func<string?>? WebUrl { get; init; }

    /// <summary>Nur für Tests: Lüfter-Hardware ersetzen (Parameter: eingestellter Port).</summary>
    public Func<string, Fans.IFanDevice>? FanDeviceFactory { get; init; }

    /// <summary>Tests: Display-Pico statt echter Hardware (Argument: Port bzw. WLAN-Adresse).</summary>
    public Func<string, Fans.IFanDevice>? DisplayDeviceFactory { get; init; }

    /// <summary>Tests: serielle Verbindung für „Pico für WLAN einrichten“ (Argument: Port).</summary>
    public Func<string, Fans.ILineTransport>? SerialTransportFactory { get; init; }

    /// <summary>Nur für Tests: Wartezeiten des Benchmarks ersetzen.</summary>
    public Func<TimeSpan, CancellationToken, Task>? BenchmarkDelay { get; init; }

    /// <summary>
    /// Datenordner (config.json, history.db, tax, tuning, snapshots). Ohne Angabe der aktuelle Datenordner
    /// der App (<see cref="DataPaths.Current"/>).
    /// </summary>
    public string? DataDirectory { get; init; }
}

/// <summary>
/// Headless-Motor von BitaxeTuner: alle Funktionen, die rund um die Uhr laufen müssen – Abfrage, Verlauf,
/// Benachrichtigungen, Watchdog, Firmware-, Pool- und Wallet-Prüfung, Tagesbericht, Steuer-Erfassung,
/// Log-Alarme, Automatik-Regeln, Dauertests und Benchmarks. Läuft in der Desktop-App (Betriebsart „Lokal“)
/// oder im Server-Dienst; jeder Dienst existiert genau einmal (insbesondere ein Blockchair-Dienst mit
/// 9-Minuten-Cache und ein zentraler Abfragedienst je Miner).
///
/// Threading: <see cref="Start"/> merkt sich den <see cref="SynchronizationContext"/> des Aufrufers
/// (Desktop: UI-Thread, Server: <see cref="HubThread"/>). Alle Takte und Zustandsänderungen laufen dort
/// nacheinander – wie früher mit den DispatcherTimern der Oberfläche. Aufrufe von außen gehen über <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed partial class MinerHub : IDisposable
{
    private readonly string _tuningDirectory;
    private readonly Dictionary<string, HubDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _priceHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private SynchronizationContext? _context;
    private CancellationTokenSource? _loops;
    private bool _paused, _started, _disposed;

    public MinerHub(AppConfig config, MinerHubOptions? options = null)
    {
        options ??= new MinerHubOptions();
        Options = options;
        Config = config;
        var explicitDir = options.DataDirectory is { Length: > 0 } d ? Path.GetFullPath(d) : null;
        DataDirectory = explicitDir ?? DataPaths.Current;
        if (explicitDir is not null) config.FilePath ??= Path.Combine(explicitDir, "config.json");
        _tuningDirectory = Path.Combine(DataDirectory, "tuning");

        Maintenance = new MaintenanceTracker(options.Clock is { } clock ? () => clock().ToUniversalTime() : null);
        Profiles = ProfileRegistry.Load(_tuningDirectory);
        Results = new ResultStore(_tuningDirectory);
        var factory = options.ClientFactory ?? (host => MinerClientFactory.Create(host, Profiles));
        Polling = new MinerPollingService(Maintenance, factory);

        try
        {
            History = new HistoryStore(explicitDir is null ? null : Path.Combine(explicitDir, "history.db"));
        }
        catch (Exception ex)
        {
            History = null;
            HistoryError = ex.Message;
        }

        // Jede Frequenz-/Spannungsänderung landet mit Zeitstempel, altem und neuem Wert in history.db
        Polling.TuningApplied += e =>
        {
            if (SimulatedMinerClient.IsSimAddress(e.Host)) return;
            try { History?.AddTuningEvent(e); } catch { /* nicht kritisch */ }
            LogEvent(e.Host, EventCategories.Tuning, L.T("{0}: {1} → {2} MHz / {3} mV", e.SourceText,
                e.OldFrequencyMhz is { } of ? L.T("{0} MHz / {1} mV", of, e.OldCoreVoltageMv?.ToString() ?? "?") : "?", e.NewFrequencyMhz, e.NewCoreVoltageMv) +
                (string.IsNullOrWhiteSpace(e.Note) ? "" : $" ({e.Note})"));
            TuningApplied?.Invoke(e);
        };

        Notify = new NotificationService(() => Config.Notifications)
        {
            GroupsOf = GroupsOfHost,
            Muted = host => Maintenance.IsManual(host),
            QueueFile = Path.Combine(DataDirectory, "push-queue.json"),
        };
        InitWebPush();
        config.SaveFailed += OnConfigSaveFailed;
        Firmware = new FirmwareChecker();

        Blockchair = new BlockchairBlockchainService(config.BlockchairApiKey);
        CoinGecko = new CoinGeckoPriceService(config.CoinGeckoApiKey);
        WalletClient = new WalletClient();
        WalletClient.UseBlockchairForBch(Blockchair);
        NetworkClient = new NetworkClient();
        Odds = new SoloOddsService(Blockchair);
        Tax.TaxTime.Configure(config.TaxTimeZone);
        TaxRepository = new TaxLogRepository(explicitDir is null ? null : Path.Combine(explicitDir, "tax"));
        TaxMonitor = new WalletMonitorService(Blockchair, CoinGecko, TaxRepository) { Currency = () => Currencies.Of(Config).Code };
        TaxEditor = new TaxEditor(TaxMonitor, TaxRepository);

        Snapshots = new SettingsSnapshots(Path.Combine(DataDirectory, "snapshots"));
        PoolWatch = new PoolWatch();
        LogAlerts = new LogAlertService(() => Config, Maintenance, SendAlert);

        _priceHttp.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
        Prices = new PriceService(() => Config.PriceSource, _priceHttp);
        Automation = new AutomationEngine(Prices) { CentPerKwh = () => Currencies.Of(Config).CentPerKwh };
        WebView = new WebViewServer(() => Config.WebView.PinHash, () => WebStatusJson, UpgradeLegacyPin);
        Updates = new UpdateService(_updateHttp, options.UpdateRepository);
        Benchmarks = new BenchmarkManager(this);

        SyncDevices();
    }

    public AppConfig Config { get; }
    public MinerHubOptions Options { get; }
    public string DataDirectory { get; }

    public MaintenanceTracker Maintenance { get; }
    public MinerPollingService Polling { get; }
    public HistoryStore? History { get; }
    public string? HistoryError { get; }
    public ProfileRegistry Profiles { get; private set; }
    public ResultStore Results { get; }

    public NotificationService Notify { get; }
    public FirmwareChecker Firmware { get; }
    public BlockchairBlockchainService Blockchair { get; }
    public CoinGeckoPriceService CoinGecko { get; }
    public WalletClient WalletClient { get; }
    public NetworkClient NetworkClient { get; }
    public SoloOddsService Odds { get; }
    public TaxLogRepository TaxRepository { get; }
    public WalletMonitorService TaxMonitor { get; }

    /// <summary>Steuer-Bereich bearbeiten (Browser im Server-Betrieb).</summary>
    public TaxEditor TaxEditor { get; }

    /// <summary>Wallet-Adressen der Miner (Einstellung oder Stratum-User) für die Übernahme ins Steuer-Modul.</summary>
    public IEnumerable<(string Name, string Host, string Address)> MinerWalletCandidates() =>
        States.Where(s => !string.IsNullOrWhiteSpace(s.WalletAddress) && !SimulatedMinerClient.IsSimAddress(s.Config.Host))
              .Select(s => (s.Config.Name, s.Config.Host, s.WalletAddress!)).ToList();

    public SettingsSnapshots Snapshots { get; }
    public PoolWatch PoolWatch { get; }
    public LogAlertService LogAlerts { get; }
    public PriceService Prices { get; }
    public AutomationEngine Automation { get; }
    public WebViewServer WebView { get; }
    public UpdateService Updates { get; }
    public BenchmarkManager Benchmarks { get; }

    /// <summary>Zuletzt erzeugter Stand für die Handy-Ansicht (im Hub-Kontext gebaut, vom Webserver nur gelesen).</summary>
    public volatile string WebStatusJson = "{}";

    /// <summary>Geräte in der Reihenfolge der Konfiguration.</summary>
    public IReadOnlyList<HubDevice> Devices =>
        Polling.States.Select(s => _devices.Values.FirstOrDefault(d => ReferenceEquals(d.State, s))).OfType<HubDevice>().ToList();

    /// <summary>Gerät zur Adresse (wie in config.json oder wie vom Client normalisiert).</summary>
    public HubDevice? Device(string host) =>
        _devices.GetValueOrDefault(host.Trim()) ??
        _devices.Values.FirstOrDefault(d => string.Equals(d.Config.Host.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsRunning => _started && !_paused;

    // ---------- Ereignisse (im Hub-Kontext) ----------

    /// <summary>Nach jeder Abfragerunde, nachdem Verlauf, Meldungen, Watchdog und Automatik gelaufen sind.</summary>
    public event Action? Polled;

    /// <summary>Protokollierte Tuning-Änderung (kann auf einem Pool-Thread kommen).</summary>
    public event Action<TuningEvent>? TuningApplied;

    /// <summary>Statuszeile: (ok, Text).</summary>
    public event Action<bool, string>? StatusMessage;

    /// <summary>Geräteliste neu aufgebaut (hinzugefügt, entfernt, Einstellungen gespeichert).</summary>
    public event Action? DevicesChanged;

    /// <summary>Profil, Automatik- oder Dauertest-Status eines Geräts geändert.</summary>
    public event Action<HubDevice>? DeviceChanged;

    internal void RaiseStatus(bool ok, string text) => StatusMessage?.Invoke(ok, text);

    /// <summary>config.json ließ sich nicht schreiben (Audit E2): ins Protokoll und als Statusmeldung.</summary>
    private void OnConfigSaveFailed(string error)
    {
        var text = L.T("Einstellungen konnten nicht gespeichert werden: {0}", error);
        LogEvent(null, EventCategories.Settings, text);
        RaiseStatus(false, text);
    }
    internal void RaiseDeviceChanged(HubDevice device) => DeviceChanged?.Invoke(device);

    /// <summary>Gruppen eines Miners laut Einstellungen (leer, wenn unbekannt).</summary>
    public IReadOnlyCollection<string> GroupsOfHost(string host) =>
        Config.Devices.FirstOrDefault(d => string.Equals(d.Host.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))?.Groups ?? [];

    /// <summary>
    /// Eintrag im dauerhaften Protokoll (history.db). Host null = Server/allgemein. Simulierte Miner (Demo) werden
    /// wie im Verlauf nicht gespeichert. Fehler beim Schreiben sind nicht kritisch.
    /// </summary>
    public void LogEvent(string? host, string category, string message)
    {
        if (History is null || (host is not null && SimulatedMinerClient.IsSimAddress(host))) return;
        try { History.AddEvent(host, category, message, DateTime.Now); } catch { /* nicht kritisch */ }
    }

    // ---------- Geräteliste ----------

    /// <summary>Geräteliste aus config.json übernehmen (Start, Hinzufügen, Entfernen, Einstellungen gespeichert).</summary>
    public void SyncDevices()
    {
        Polling.Sync(Config.Devices);

        var states = Polling.States;
        foreach (var gone in _devices.Where(d => !states.Contains(d.Value.State)).Select(d => d.Key).ToList())
        {
            if (_devices[gone].IsBenchmarkRunning) Benchmarks.Stop(_devices[gone]);
            _devices.Remove(gone);
        }

        foreach (var state in states)
        {
            var connection = Polling.Connection(state.Config.Host)!;
            if (_devices.TryGetValue(connection.Address, out var existing))
            {
                existing.State = state;
                continue;
            }
            var device = new HubDevice(state, connection, Profiles.Generic.Clone());
            _devices[connection.Address] = device;
            InitDevice(device);
        }

        foreach (var d in _devices.Values) Maintenance.SetManual(d.Host, d.Config.MaintenanceMode);   // 0.9.11
        if (!_paused) SyncLogAlerts();
        DevicesChanged?.Invoke();
    }

    private void InitDevice(HubDevice device)
    {
        // Zeilen mit Kategorie dauerhaft in history.db (Protokoll; übersteht Neustarts und Updates)
        device.Logged += (category, message) => LogEvent(device.Host, category, message);
        device.AddLog(L.T("Gerät: {0} ({1})", device.Title, device.Host), category: null);

        // Manuell gewähltes Profil aus config.json, sonst Erkennung beim ersten Datenpunkt
        if (device.Config.ProfileId is { } id && Profiles.Profiles.FirstOrDefault(p => p.Id == id) is { } chosen)
        {
            device.Profile = chosen.Clone();
            device.ProfileResolved = true;
            device.ProfileKnown = true;
            device.AddLog(L.T("Profil aus den Einstellungen: „{0}“", chosen.Name), category: null);
        }

        var last = Results.LoadLatest(device.Host, null);
        if (last is not null)
            device.AddLog(L.T("Letzter Lauf vom {0:g} geladen ({1} Ergebnisse", last.StartedAt, last.Results.Count) +
                          (last.IsFinished ? ")." : L.T(", nicht abgeschlossen – kann fortgesetzt werden).")), category: null);
    }

    /// <summary>Log-Alarme und gespeicherte Miner-Logs an Geräteliste und Einstellungen angleichen.</summary>
    public void SyncLogAlerts()
    {
        var devices = Polling.States
            .Where(s => !IsSimulated(s.Config.Host))
            .Select(s => (s.Config, Polling.Connection(s.Config.Host)!)).ToList();
        LogAlerts.Sync(devices);
        MinerLogs.Sync(devices);
    }

    /// <summary>0.9.11: Miner-Logs speichern (je Miner einschaltbar).</summary>
    public MinerLogArchive MinerLogs => _minerLogs ??= new MinerLogArchive(() => History);
    private MinerLogArchive? _minerLogs;
    private DateTime _lastLogFlush = DateTime.MinValue, _lastLogPrune = DateTime.MinValue;

    /// <summary>Etwa alle 30 s gesammelte Log-Zeilen schreiben, stündlich Altes löschen.</summary>
    private void TickMinerLogs(DateTime now)
    {
        if (_minerLogs is null || History is null) return;
        if ((now - _lastLogFlush).TotalSeconds >= 30)
        {
            _lastLogFlush = now;
            _minerLogs.Flush();
        }
        if ((now - _lastLogPrune).TotalMinutes >= 60)
        {
            _lastLogPrune = now;
            try { History.PruneMinerLog(now.AddHours(-Math.Clamp(Config.MinerLogKeepHours, 1, 168))); } catch { /* nicht kritisch */ }
        }
    }

    /// <summary>Profile nach Bearbeiten der profiles.json neu laden; neue Auffälligkeiten (Audit N-S3) ins Protokoll.</summary>
    public void ReloadProfiles()
    {
        var known = Profiles.Problems.ToHashSet();
        Profiles = ProfileRegistry.Load(_tuningDirectory);
        foreach (var problem in Profiles.Problems.Where(p => !known.Contains(p)))
            LogEvent(null, EventCategories.Settings, problem);
    }

    /// <summary>Auswahl für ein Gerät: das erkannte (angepasste) Profil ersetzt seinen Registry-Eintrag.</summary>
    public IReadOnlyList<DeviceProfile> ProfilesFor(HubDevice device) =>
        device.MatchedProfile is { } m ? Profiles.Profiles.Select(p => p.Id == m.Id ? m : p).ToList() : Profiles.Profiles.ToList();

    /// <summary>Vom Benutzer gewähltes Profil dauerhaft in der Geräteliste merken.</summary>
    public void SetProfile(HubDevice device, DeviceProfile profile)
    {
        device.Profile = profile;
        device.ProfileResolved = true;
        device.ProfileKnown = true;
        device.Config.ProfileId = profile.Id;
        Config.Save();
        device.AddLog(L.T("Profil gewählt: „{0}“ (gespeichert)", profile.Name), EventCategories.Settings);
        RaiseDeviceChanged(device);
    }

    private async Task ResolveProfileAsync(HubDevice device, MinerInfo info)
    {
        device.ProfileResolved = true;
        AsicInfo? asic = null;
        try { asic = await device.Connection.GetAsicInfoAsync(); } catch (MinerApiException) { }
        var matched = Profiles.Match(info, asic);
        device.MatchedProfile = matched;
        device.Profile = matched;
        device.ProfileKnown = true;
        device.AddLog(L.T("Erkannt: {0} ({1} {2}) → Profil „{3}“", info.DeviceModel ?? info.AsicModel, FirmwareName(info.Firmware), info.FirmwareVersion, matched.Name), category: null);
        RaiseDeviceChanged(device);
    }

    public static string FirmwareName(FirmwareKind kind) => kind switch
    {
        FirmwareKind.AxeOS => "AxeOS",
        FirmwareKind.NerdQAxe => L.T("NerdQAxe-Firmware"),
        FirmwareKind.Simulated => L.T("Simulation"),
        _ => L.T("Firmware"),
    };

    public static bool IsSimulated(string host) => SimulatedMinerClient.IsSimAddress(host);

    // ---------- Takt ----------

    /// <summary>
    /// Takte starten (Miner-Abfrage, Wallets, Steuer-Monitor) und sofort eine Runde abfragen.
    /// Muss im Kontext aufgerufen werden, in dem der Hub laufen soll.
    /// </summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _context = SynchronizationContext.Current;
        InitMonitoring();
        // Lüfter laufen immer: ohne aktuelle Minerdaten (auch im Pausenzustand) gehen sie auf 100 %
        StartFanLoop();
        // Home Assistant bekommt auch im Pausenzustand Werte (Lüfter, Fühler, „pausiert“)
        if (Config.Mqtt.Enabled) _ = ApplyMqttSettingsAsync();
        LogEvent(null, EventCategories.System, L.T("Überwachung gestartet ({0} Miner)", Devices.Count));
        foreach (var problem in Profiles.Problems) LogEvent(null, EventCategories.Settings, problem);
        if (_paused) return; // z. B. Server pausiert, weil die Desktop-App gerade selbst abfragt
        RestartLoops();
        await PollNowAsync();
        // Wallets und Smart Plugs im Hintergrund (Netzabfrage) – der Start wartet nicht darauf
        _ = PollWalletsAsync();
        _ = PlugTickAsync();
        if (Config.PoolAccount.Enabled) _ = PoolAccountTickAsync();
    }

    /// <summary>
    /// Abfragen anhalten/fortsetzen (Datenordner-Umzug, Datenübertragung, Server während Desktop-Betrieb).
    /// Pausiert heißt: keine Miner-, Wallet- oder Steuerabfragen und keine Log-Verbindungen zu den Minern.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _loops?.Cancel();
            LogAlerts.Sync([]);
            _minerLogs?.Sync([]);
            TaxMonitor.Stop();
        }
        else
        {
            SyncLogAlerts();
            if (_started) RestartLoops();
        }
    }

    public bool IsPaused => _paused;

    /// <summary>Nach "Einstellungen speichern": Geräteliste, Takte und Caches neu, sofort abfragen.</summary>
    public async Task ApplySettingsChangedAsync()
    {
        Tax.TaxTime.Configure(Config.TaxTimeZone);
        SyncDevices();
        if (_started && !_paused) RestartLoops();
        _lastFirmwareRefresh = DateTime.MinValue;
        await PollNowAsync();
        // Audit I3: Wallets nur neu abfragen, wenn sich die Adressen geändert haben (nicht bei jedem Speichern)
        if (!LookupAddresses(States, Config.WalletLookupConsent).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_lastWalletAddresses))
            await PollWalletsAsync();
        await ApplyPoolAccountSettingsAsync();
    }

    private void RestartLoops()
    {
        _loops?.Cancel();
        _loops = new CancellationTokenSource();
        var ct = _loops.Token;
        _ = RunLoopAsync(() => TimeSpan.FromSeconds(Math.Clamp(Config.IntervalSeconds, 1, 300)), PollNowAsync, ct);
        _ = RunLoopAsync(() => TimeSpan.FromMinutes(Math.Clamp(Config.WalletPollMinutes, 1, 1440)), PollWalletsAsync, ct);
        _ = RunLoopAsync(() => PlugInterval, PlugTickAsync, ct);
        _ = RunLoopAsync(() => TimeSpan.FromMinutes(Math.Clamp(Config.PoolAccount.IntervalMinutes, 10, 240)),
            async () => { if (Config.PoolAccount.Enabled) await PoolAccountTickAsync(); }, ct);
        // Audit I3: Steuer-Wallets nicht bei jedem „Einstellungen speichern“ neu abfragen (Blockchair/CoinGecko) –
        // nur beim ersten Start, nach einer Pause oder wenn sich der Abstand geändert hat
        var taxInterval = TimeSpan.FromMinutes(Math.Clamp(Config.TaxPollMinutes, 1, 1440));
        if (TaxMonitor.RunningInterval != taxInterval) TaxMonitor.Start(taxInterval);
    }

    private async Task RunLoopAsync(Func<TimeSpan> interval, Func<Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Ohne ConfigureAwait: die Fortsetzung läuft wieder im Hub-Kontext
            try { await Task.Delay(interval(), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            try { await body(); }
            catch (Exception ex) { RaiseStatus(false, L.T("Interner Fehler: ") + ex.Message); }
        }
    }

    /// <summary>
    /// Audit S6: Nach erfolgreicher Anmeldung mit einer PIN im alten Format (SHA-256 ohne Salz) dieselbe PIN als PBKDF2
    /// speichern – im Hub-Kontext, weil Anmeldungen auf anderen Threads laufen.
    /// </summary>
    public void UpgradeLegacyPin(string pin) => _ = InvokeAsync(() =>
    {
        if (!Config.WebView.PinIsLegacy || !WebViewSettings.VerifyPin(pin, Config.WebView.PinHash)) return false;
        Config.WebView.PinHash = WebViewSettings.HashPin(pin);
        Config.Save();
        LogEvent(null, EventCategories.Settings, L.T("PIN der Ansicht ins sichere Format (PBKDF2) übernommen."));
        return true;
    });

    /// <summary>Aufruf von außen (Server-API, anderer Thread) im Hub-Kontext ausführen.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (_context is null || SynchronizationContext.Current == _context) return action();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    public Task InvokeAsync(Func<Task> action) => InvokeAsync(async () => { await action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> action) => InvokeAsync(() => Task.FromResult(action()));

    // ---------- Handy-Ansicht ----------

    /// <summary>Handy-Ansicht gemäß Einstellungen starten oder stoppen. Liefert eine Statusmeldung.</summary>
    public string ApplyWebView()
    {
        var w = Config.WebView;
        if (!w.Enabled || string.IsNullOrEmpty(w.PinHash))
        {
            WebView.Stop();
            return w.Enabled ? L.T("Handy-Ansicht: PIN fehlt – nicht gestartet.") : "";
        }
        if (WebView.IsRunning && WebView.Port == w.Port) return L.T("Handy-Ansicht: {0}", string.Join(" oder ", WebViewServer.LocalUrls(w.Port)));
        // Nur für Tests: BITAXETUNER_WEB_BIND=127.0.0.1 bindet ausschließlich lokal (kein Firewall-Dialog)
        var bind = Environment.GetEnvironmentVariable("BITAXETUNER_WEB_BIND") is { Length: > 0 } b &&
                   System.Net.IPAddress.TryParse(b, out var ip) ? ip : null;
        WebView.Start(w.Port, bind);
        return WebView.IsRunning
            ? bind is not null
                ? $"Handy-Ansicht (nur lokal, Test): http://{bind}:{w.Port}/"
                : L.T("Handy-Ansicht: {0}", string.Join(" oder ", WebViewServer.LocalUrls(w.Port)))
            : L.T("Handy-Ansicht nicht gestartet: {0}", WebView.LastError);
    }

    /// <summary>Meldung über den gemeinsamen Benachrichtigungsdienst (mit Sperrzeit je Schlüssel).</summary>
    public void SendAlert(Alert a) => _ = Notify.SendAsync(a.Key, a.Title, a.Message, a.Priority, a.Cooldown, a.Category, a.Host);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Config.SaveFailed -= OnConfigSaveFailed;
        _loops?.Cancel();
        _fanLoop?.Cancel();
        _mqttLoop?.Cancel();
        // außerhalb des Hub-Kontexts trennen (dieser Thread wartet hier)
        if (_mqtt is { } mqtt) try { Task.Run(() => mqtt.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3)); } catch { /* Broker weg */ }
        CloseFanDevice();
        _news?.Dispose();
        CloseExtraDisplays();
        foreach (var id in _plugClients.Keys.ToList()) ClosePlug(id);
        foreach (var d in _devices.Values) d.Benchmark?.Cts?.Cancel();
        WebView.Dispose();
        _priceHttp.Dispose();
        _updateHttp.Dispose();
        TaxMonitor.Dispose();
        _poolClient?.Dispose();
        LogAlerts.Dispose();
        _minerLogs?.Dispose();   // schreibt die letzten gesammelten Zeilen
        Polling.Dispose();
        History?.Dispose();
        Notify.Dispose();
        Firmware.Dispose();
        WalletClient.Dispose();
        NetworkClient.Dispose();
        Blockchair.Dispose();
        CoinGecko.Dispose();
    }
}
