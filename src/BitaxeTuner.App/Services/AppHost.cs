using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.Web;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Kompositionskern der Desktop-App. Alle Dienste und die gesamte 24/7-Logik liegen im <see cref="MinerHub"/>
/// (BitaxeTuner.Core) – derselbe Motor läuft auch im Server-Dienst. Jeder Dienst existiert genau einmal,
/// insbesondere nur EIN <see cref="BlockchairBlockchainService"/> (9-Minuten-Cache) und nur EIN
/// <see cref="MinerPollingService"/> für alle AxeOS-Abfragen.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AppHost(AppConfig config)
    {
        Hub = new MinerHub(config, new MinerHubOptions { UpdateRepository = UpdateChecker.Repository });
        Hub.Polled += () => Polled?.Invoke();
        Hub.TuningApplied += e => TuningApplied?.Invoke(e);
    }

    /// <summary>Der Motor (Betriebsart „Lokal“).</summary>
    public MinerHub Hub { get; }

    public AppConfig Config => Hub.Config;
    public string DataDirectory => Hub.DataDirectory;

    public MaintenanceTracker Maintenance => Hub.Maintenance;
    public MinerPollingService Polling => Hub.Polling;
    public HistoryStore? History => Hub.History;
    public string? HistoryError => Hub.HistoryError;
    public ProfileRegistry Profiles => Hub.Profiles;
    public ResultStore Results => Hub.Results;

    public NotificationService Notify => Hub.Notify;
    public FirmwareChecker Firmware => Hub.Firmware;
    public BlockchairBlockchainService Blockchair => Hub.Blockchair;
    public CoinGeckoPriceService CoinGecko => Hub.CoinGecko;
    public WalletClient WalletClient => Hub.WalletClient;
    public NetworkClient NetworkClient => Hub.NetworkClient;
    public SoloOddsService Odds => Hub.Odds;
    public TaxLogRepository TaxRepository => Hub.TaxRepository;
    public WalletMonitorService TaxMonitor => Hub.TaxMonitor;

    public SettingsSnapshots Snapshots => Hub.Snapshots;
    public PoolWatch PoolWatch => Hub.PoolWatch;
    public LogAlertService LogAlerts => Hub.LogAlerts;
    public PriceService Prices => Hub.Prices;
    public AutomationEngine Automation => Hub.Automation;
    public WebViewServer WebView => Hub.WebView;
    public Core.Update.UpdateService Updates => Hub.Updates;

    /// <summary>Handy-Ansicht gemäß Einstellungen starten oder stoppen. Liefert eine Statusmeldung.</summary>
    public string ApplyWebView() => Hub.ApplyWebView();

    /// <summary>Meldung über den gemeinsamen Benachrichtigungsdienst (mit Sperrzeit je Schlüssel).</summary>
    public void SendAlert(Alert a) => Hub.SendAlert(a);

    /// <summary>Nach jeder Abfragerunde (UI-Thread). Überwachung und Tuning-Ansichten hängen sich hier an.</summary>
    public event Action? Polled;

    /// <summary>Protokollierte Tuning-Änderung (kann auf einem Pool-Thread kommen).</summary>
    public event Action<TuningEvent>? TuningApplied;

    /// <summary>Hinweise aus dem Start (Migration, Sicherung) für die Statuszeile.</summary>
    public List<string> StartupNotes { get; } = new();

    public void ReloadProfiles() => Hub.ReloadProfiles();

    public static bool IsSimulated(string host) => MinerHub.IsSimulated(host);

    public void Dispose() => Hub.Dispose();
}
