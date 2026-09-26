using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Web;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Kompositionskern der zusammengeführten App: jeder Dienst existiert genau einmal.
/// Insbesondere gibt es nur EINEN <see cref="BlockchairBlockchainService"/> (9-Minuten-Cache,
/// gemeinsames Rate-Limit für Steuer-Modul, Wallet-Panel und Difficulty) und nur EINEN
/// <see cref="MinerPollingService"/> für alle AxeOS-Abfragen.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AppHost(AppConfig config)
    {
        Config = config;
        DataDirectory = DataPaths.Current;

        Maintenance = new MaintenanceTracker();
        Profiles = ProfileRegistry.Load(DataPaths.TuningDirectory);
        Results = new ResultStore(DataPaths.TuningDirectory);
        Polling = new MinerPollingService(Maintenance, host => MinerClientFactory.Create(host, Profiles));
        Polling.Sync(config.Devices);

        try
        {
            History = new HistoryStore();
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
            TuningApplied?.Invoke(e);
        };

        Notify = new NotificationService(() => Config.Notifications);
        Firmware = new FirmwareChecker();

        Blockchair = new BlockchairBlockchainService(config.BlockchairApiKey);
        CoinGecko = new CoinGeckoPriceService(config.CoinGeckoApiKey);
        WalletClient = new WalletClient();
        WalletClient.UseBlockchairForBch(Blockchair);
        NetworkClient = new NetworkClient();
        Odds = new SoloOddsService(Blockchair);
        TaxRepository = new TaxLogRepository();
        TaxMonitor = new WalletMonitorService(Blockchair, CoinGecko, TaxRepository);

        Snapshots = new SettingsSnapshots(DataPaths.SnapshotsDirectory);
        PoolWatch = new PoolWatch();
        LogAlerts = new LogAlertService(() => Config, Maintenance, SendAlert);
        SyncLogAlerts();

        _priceHttp.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
        Prices = new PriceService(() => Config.PriceSource, _priceHttp);
        Automation = new AutomationEngine(Prices);
        WebView = new WebViewServer(() => Config.WebView.PinHash, () => WebStatusJson);
        Updates = new Core.Update.UpdateService(_updateHttp, UpdateChecker.Repository);
    }

    private readonly System.Net.Http.HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    public Core.Update.UpdateService Updates { get; }

    private readonly System.Net.Http.HttpClient _priceHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public PriceService Prices { get; }
    public AutomationEngine Automation { get; }
    public WebViewServer WebView { get; }

    /// <summary>Zuletzt erzeugter Stand für die Handy-Ansicht (wird auf dem UI-Thread gebaut, vom Webserver nur gelesen).</summary>
    public volatile string WebStatusJson = "{}";

    /// <summary>Handy-Ansicht gemäß Einstellungen starten oder stoppen. Liefert eine Statusmeldung.</summary>
    public string ApplyWebView()
    {
        var w = Config.WebView;
        if (!w.Enabled || string.IsNullOrEmpty(w.PinHash))
        {
            WebView.Stop();
            return w.Enabled ? "Handy-Ansicht: PIN fehlt – nicht gestartet." : "";
        }
        if (WebView.IsRunning && WebView.Port == w.Port) return $"Handy-Ansicht: {string.Join(" oder ", WebViewServer.LocalUrls(w.Port))}";
        // Nur für Tests: BITAXETUNER_WEB_BIND=127.0.0.1 bindet ausschließlich lokal (kein Firewall-Dialog)
        var bind = Environment.GetEnvironmentVariable("BITAXETUNER_WEB_BIND") is { Length: > 0 } b &&
                   System.Net.IPAddress.TryParse(b, out var ip) ? ip : null;
        WebView.Start(w.Port, bind);
        return WebView.IsRunning
            ? bind is not null
                ? $"Handy-Ansicht (nur lokal, Test): http://{bind}:{w.Port}/"
                : $"Handy-Ansicht: {string.Join(" oder ", WebViewServer.LocalUrls(w.Port))}"
            : $"Handy-Ansicht nicht gestartet: {WebView.LastError}";
    }

    /// <summary>Meldung über den gemeinsamen Benachrichtigungsdienst (mit Sperrzeit je Schlüssel).</summary>
    public void SendAlert(Alert a) => _ = Notify.SendAsync(a.Key, a.Title, a.Message, a.Priority, a.Cooldown);

    /// <summary>Log-Alarme an Geräteliste und Einstellungen angleichen (nach Start, Hinzufügen, Einstellungen).</summary>
    public void SyncLogAlerts() =>
        LogAlerts.Sync(Polling.States
            .Where(s => !IsSimulated(s.Config.Host))
            .Select(s => (s.Config, Polling.Connection(s.Config.Host)!)));

    public AppConfig Config { get; }
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

    public SettingsSnapshots Snapshots { get; }
    public PoolWatch PoolWatch { get; }
    public LogAlertService LogAlerts { get; }

    /// <summary>Nach jeder Abfragerunde (UI-Thread). Überwachung und Tuning-Ansichten hängen sich hier an.</summary>
    public event Action? Polled;

    /// <summary>Protokollierte Tuning-Änderung (kann auf einem Pool-Thread kommen).</summary>
    public event Action<TuningEvent>? TuningApplied;

    /// <summary>Hinweise aus dem Start (Migration, Sicherung) für die Statuszeile.</summary>
    public List<string> StartupNotes { get; } = new();

    public void RaisePolled() => Polled?.Invoke();

    public void ReloadProfiles() => Profiles = ProfileRegistry.Load(DataPaths.TuningDirectory);

    public static bool IsSimulated(string host) => SimulatedMinerClient.IsSimAddress(host);

    public void Dispose()
    {
        WebView.Dispose();
        _priceHttp.Dispose();
        _updateHttp.Dispose();
        TaxMonitor.Dispose();
        LogAlerts.Dispose();
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
