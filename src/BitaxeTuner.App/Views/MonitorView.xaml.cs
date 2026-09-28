using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BitaxeTuner.App.Services;
using BitaxeTuner.App.Tax.ViewModels;
using BitaxeTuner.App.Themes;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.App.Views;

/// <summary>Was die Überwachungsansicht gerade zeigt (vormals die Auswahl in der Geräteliste von BitaxeMonitor).</summary>
public enum MonitorMode
{
    Aggregate,
    Device,
    Network,
    Tax,
}

/// <summary>Kurztexte und Statusfarben für die Einträge der Geräteliste im Hauptfenster.</summary>
public sealed record MonitorSummary(string AggregateSub, Brush AggregateDot, IReadOnlyDictionary<string, (string Sub, Brush Dot, PoolQuickLink? PoolLink)> Devices);

/// <summary>
/// Überwachung aus BitaxeMonitor: Gesamt-, Einzel-, Netzwerk- und Steueransicht, Wallets, Verlauf,
/// Benachrichtigungen, Watchdog, Tray. Der Miner-Abruf läuft zentral über <see cref="MinerPollingService"/>;
/// die Geräteliste liegt im Hauptfenster, das über <see cref="Show"/> die Ansicht wählt.
/// </summary>
public partial class MonitorView : UserControl
{
    private AppHost _host = null!;

    // Dienste kommen aus dem AppHost – jeweils genau eine Instanz für die ganze App
    private WalletClient _walletClient => _host.WalletClient;
    private NetworkClient _networkClient => _host.NetworkClient;
    private TaxLogRepository _taxRepository => _host.TaxRepository;
    private BlockchairBlockchainService _blockchair => _host.Blockchair;
    private WalletMonitorService _taxMonitor => _host.TaxMonitor;
    private TaxViewModel _taxViewModel = null!;
    private AppConfig _config => _host.Config;

    private IReadOnlyList<MinerState> _states => _host.Polling.States;
    // Live-Summe, Wallet-Stände und Fehler pflegt der Hub (laufen auch ohne Fenster weiter)
    private IReadOnlyList<Sample> _aggHistory => _host.Hub.AggregateHistory;
    private IReadOnlyDictionary<string, WalletInfo> _wallets => _host.Hub.Wallets;
    private IReadOnlyDictionary<string, string> _walletErrors => _host.Hub.WalletErrors;

    private readonly ObservableCollection<BlockRow> _blockRows = new();
    private readonly ObservableCollection<PoolRow> _poolRows = new();
    private string _poolPeriod = "1w";
    private DateTime _blocksFetched = DateTime.MinValue;

    private readonly DispatcherTimer _networkTimer = new();
    private bool _networkBusy, _started;

    private MonitorMode _mode = MonitorMode.Aggregate;
    private string? _selectedHost;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    // Farben aus dem aktiven Design (dunkel wie BitaxeMonitor oder hell)
    private static Brush Green => ThemeManager.Brush("OkBrush");
    private static Brush Red => ThemeManager.Brush("DangerBrush");
    private static Brush Gold => ThemeManager.Brush("WarnBrush");
    private static Brush Normal => ThemeManager.Brush("TextBrush");
    private static Brush Grey => ThemeManager.Brush("IdleBrush");

    /// <summary>Neue Kurztexte für die Geräteliste nach jeder Abfrage.</summary>
    public event Action<MonitorSummary>? SummaryChanged;

    public MonitorView()
    {
        InitializeComponent();
    }

    private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    /// <summary>Einmalig vom Hauptfenster aufgerufen (entspricht dem Konstruktor des alten MainWindow).</summary>
    public void Initialize(AppHost host)
    {
        _host = host;
        _taxViewModel = new TaxViewModel(_taxMonitor, _taxRepository, MinerAddressCandidates);
        TaxView.DataContext = _taxViewModel;
        InitFeatures();

        BlockList.ItemsSource = _blockRows;
        PoolList.ItemsSource = _poolRows;

        // Abfrage, Wallets, Meldungen und Automatik laufen im Hub; die Ansicht zeigt nur an
        _host.Hub.Polled += OnHubPolled;
        _host.Hub.StatusMessage += SetStatus;
        _host.Hub.WalletsUpdated += OnWalletsUpdated;
        _host.Hub.PayoutDetected += ShowPayout;
        _networkTimer.Tick += async (_, _) =>
        {
            if (NetworkPanel.Visibility != Visibility.Visible) return;
            await PollNetworkAsync(false);
            await RefreshSoloOddsAsync();
        };
        SizeChanged += (_, _) => RenderSelected();
        ThemeManager.Changed += RenderSelected;
        _host.TuningApplied += _ => Dispatcher.BeginInvoke(() => { _markerCache.Clear(); RenderSelected(); });

        // Die Ansicht wird zwischen Hauptbereich und Geräte-Tab umgehängt – Start nur einmal
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            AttachWindow();
            StartTimers();
            // Der Hub übernimmt den Kontext des UI-Threads: alle Takte laufen hier nacheinander (wie zuvor die DispatcherTimer)
            await _host.Hub.StartAsync();
        };
    }

    /// <summary>Beim Beenden (vormals Closed-Handler des MainWindow).</summary>
    public void Shutdown()
    {
        _networkTimer.Stop();
        _host.Hub.SetPaused(true);
        _host.Hub.Polled -= OnHubPolled;
        _host.Hub.StatusMessage -= SetStatus;
        _host.Hub.WalletsUpdated -= OnWalletsUpdated;
        _host.Hub.PayoutDetected -= ShowPayout;
        ThemeManager.Changed -= RenderSelected;
        _taxViewModel.Dispose();
        DisposeFeatures();
    }

    /// <summary>Abfragen anhalten/fortsetzen (z. B. während eines Datenordner-Umzugs).</summary>
    public void SetPaused(bool paused)
    {
        _host.Hub.SetPaused(paused);
        if (paused) _networkTimer.Stop();
        else StartTimers();
    }

    // ---------- Auswahl (Geräteliste liegt im Hauptfenster) ----------

    /// <summary>Ansicht wählen: Gesamt, ein Miner, Netzwerk oder Steuer.</summary>
    public void Show(MonitorMode mode, string? host = null)
    {
        _mode = mode;
        _selectedHost = mode == MonitorMode.Device ? host : null;
        if (_host is null) return;

        var state = SelectedState();
        WalletBox.Text = state?.Config.WalletAddress ?? "";
        WalletBox.IsEnabled = state is not null;
        RenderSelected();

        if (mode == MonitorMode.Network)
        {
            _ = PollNetworkAsync(false);
            _ = RefreshSoloOddsAsync();
        }
    }

    /// <summary>Kurztexte je Eintrag der Geräteliste (vormals UpdateDeviceList).</summary>
    private void UpdateDeviceList()
    {
        var online = _states.Count(s => s.Online);
        var totalGh = _states.Where(s => s.Online).Sum(s => s.Info!.hashRate);

        var devices = new Dictionary<string, (string, Brush, PoolQuickLink?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _states)
        {
            var maintenance = _host.Maintenance.IsActive(s.Config.Host);
            devices[s.Config.Host] = (
                s.Online
                    ? $"{FormatHash(s.Info!.hashRate)} · {s.Info.temp.ToString("0", De)} °C"
                    : maintenance ? "Neustart/Tuning …" : s.Error ?? "offline",
                s.Online ? Green : maintenance ? Gold : Red,
                s.Online && !MinerHub.IsSimulated(s.Config.Host) ? PoolQuickLinks.For(s.Info) : null);
        }

        SummaryChanged?.Invoke(new MonitorSummary(
            $"{online}/{_states.Count} online · {FormatHash(totalGh)}",
            _states.Count == 0 ? Grey : online == _states.Count ? Green : online == 0 ? Red : Gold,
            devices));
    }

    private MinerState? SelectedState()
    {
        if (_mode != MonitorMode.Device || _selectedHost is null) return null;
        return _states.FirstOrDefault(s => string.Equals(s.Config.Host, _selectedHost, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Timer / Abfrage ----------

    private void StartTimers()
    {
        // Nur die Netzwerk-Ansicht hat einen eigenen Takt (reine Anzeige); Miner, Wallets und Steuer laufen im Hub
        _networkTimer.Interval = TimeSpan.FromMinutes(2);
        _networkTimer.Start();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _host.Hub.ClearLiveHistory();
        RenderSelected();
    }

    private async void WalletButton_Click(object sender, RoutedEventArgs e) => await _host.Hub.PollWalletsAsync();

    private void WalletBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var state = SelectedState();
        if (state is null) return;

        var value = WalletBox.Text.Trim();
        if (value == state.Config.WalletAddress) return;

        state.Config.WalletAddress = value;
        _config.Save();
    }

    /// <summary>Nach jeder Abfragerunde des Hubs (UI-Thread): Anzeige aktualisieren.</summary>
    private void OnHubPolled()
    {
        UpdateDeviceList();
        UpdateTray();
        RenderSelected();
    }

    private static string Shorten(Exception ex) => ex switch
    {
        TaskCanceledException => "Zeitüberschreitung",
        HttpRequestException => "keine Verbindung",
        _ => ex.Message
    };

    private void SetStatus(bool ok, string text)
    {
        StatusDot.Fill = ok ? Green : Red;
        StatusText.Text = text;
    }

    // ---------- Wallets ----------

    private void OnWalletsUpdated()
    {
        WalletStatus.Text = _host.Hub.WalletStatusText;
        RenderSelected();
    }

    /// <summary>Eingang auf einer Miner-Wallet, die das Steuer-Modul nicht selbst meldet.</summary>
    private void ShowPayout(PayoutNotice p)
    {
        OwnerWindow.Activate();
        MessageBox.Show(OwnerWindow,
            $"Eingang auf {ShortAddress(p.Address)}" +
            (p.MinerName is not null ? $" ({p.MinerName})" : "") + "\n\n" +
            $"Betrag: {FormatBtc(p.AmountSat)}\n" +
            $"Status: {(p.Confirmed ? "bestätigt" : "unbestätigt (Mempool)")}\n" +
            $"TXID: {p.TxId}",
            "Auszahlung eingegangen", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Rendering ----------

    private void RenderSelected()
    {
        if (_host is null) return;
        var network = _mode == MonitorMode.Network;
        var tax = _mode == MonitorMode.Tax;

        NetworkPanel.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        TaxView.Visibility = tax ? Visibility.Visible : Visibility.Collapsed;
        MinerPanel.Visibility = network || tax ? Visibility.Collapsed : Visibility.Visible;

        if (network)
        {
            ViewTitle.Text = "Netzwerk – Blöcke, Pools und Solo-Chancen";
            RenderSoloOdds();
            return;
        }
        if (tax)
        {
            ViewTitle.Text = "Steuer – Dokumentation der Zuflüsse";
            return;
        }

        var state = SelectedState();
        if (state is null) RenderAggregate();
        else RenderSingle(state);
    }

    private void SetTile(TextBlock label, TextBlock value, TextBlock sub,
                         string labelText, string valueText, string subText, Brush? brush = null)
    {
        label.Text = labelText;
        value.Text = valueText;
        sub.Text = subText;
        if (brush is not null) value.Foreground = brush;
    }

    private void RenderAggregate()
    {
        var online = _states.Where(s => s.Online).ToList();
        ViewTitle.Text = $"Gesamt – {online.Count} von {_states.Count} Miner online";
        var onFallback = online.Where(s => s.Info!.isUsingFallbackStratum != 0).Select(s => s.Config.Name).ToList();
        PoolStatusText.Text = online.Count == 0 ? "" : onFallback.Count == 0
            ? "Pools: alle Miner auf dem Primär-Pool"
            : $"Pools: FALLBACK aktiv bei {string.Join(", ", onFallback)}";

        if (online.Count == 0)
        {
            foreach (var (l, v, s) in Tiles()) SetTile(l, v, s, l.Text, "–", "", Normal);
            T1Value.Foreground = Green;
            T10Value.Foreground = Gold;
            FooterText.Text = "kein Miner erreichbar";
            DrawCharts(ChartData(HistoryStore.AggregateHost, _aggHistory), "Gesamt", HistoryStore.AggregateHost);
            RenderWalletAggregate();
            return;
        }

        var infos = online.Select(s => s.Info!).ToList();
        var hash = infos.Sum(i => i.hashRate);
        var expected = infos.Sum(i => i.expectedHashrate);
        var power = infos.Sum(i => i.power);
        var th = hash / 1000.0;

        SetTile(T1Label, T1Value, T1Sub, "HASHRATE GESAMT", FormatHash(hash),
                expected > 0 ? $"erwartet {FormatHash(expected)}" : "", Green);

        SetTile(T2Label, T2Value, T2Sub, "ASIC TEMP MAX", infos.Max(i => i.temp).ToString("0.0", De) + " °C",
                $"Ø {infos.Average(i => i.temp).ToString("0.0", De)} °C", TempBrush(infos.Max(i => i.temp)));

        var vr = infos.Where(i => i.vrTemp > 0).Select(i => i.vrTemp).ToList();
        SetTile(T3Label, T3Value, T3Sub, "VR TEMP MAX",
                vr.Count > 0 ? vr.Max().ToString("0", De) + " °C" : "–",
                infos.Any(i => i.overheat_mode != 0) ? "OVERHEAT AKTIV" : "", Normal);

        SetTile(T4Label, T4Value, T4Sub, "LEISTUNG GESAMT", power.ToString("0.0", De) + " W",
                $"Ø {(power / online.Count).ToString("0.0", De)} W je Miner", Normal);

        SetTile(T5Label, T5Value, T5Sub, "EFFIZIENZ",
                th > 0.01 ? (power / th).ToString("0.0", De) : "–", "J/TH gesamt", Normal);

        var offline = _states.Where(s => !s.Online).Select(s => s.Config.Name).ToList();
        SetTile(T6Label, T6Value, T6Sub, "MINER ONLINE", $"{online.Count}/{_states.Count}",
                offline.Count > 0 ? "offline: " + string.Join(", ", offline) : "alle erreichbar",
                offline.Count > 0 ? Red : Green);

        var acc = infos.Sum(i => i.sharesAccepted);
        var rej = infos.Sum(i => i.sharesRejected);
        SetTile(T7Label, T7Value, T7Sub, "SHARES GESAMT", acc.ToString("N0", De),
                acc + rej > 0 ? $"{(rej / (acc + rej) * 100).ToString("0.00", De)} % abgelehnt" : "", Normal);

        SetTile(T8Label, T8Value, T8Sub, "STROM PRO TAG",
                (power * 24 / 1000.0).ToString("0.00", De) + " kWh", "bei aktueller Last", Normal);


        var fans = infos.Where(i => i.fanrpm > 0).ToList();
        SetTile(T9Label, T9Value, T9Sub, "LÜFTER MAX",
                fans.Count > 0 ? fans.Max(i => i.fanrpm).ToString("N0", De) + " rpm" : "–",
                fans.Count > 0 ? $"Ø {fans.Average(i => i.fanspeed).ToString("0", De)} %" : "", Normal);

        var best = online
            .Select(s => (s.Config.Name, Value: Difficulty.Parse(s.Info!.bestDiff)))
            .OrderByDescending(x => x.Value)
            .FirstOrDefault();
        var record = AggregateRecord();
        SetTile(T10Label, T10Value, T10Sub, "BEST DIFF",
                best.Value > 0 ? Difficulty.Format(best.Value) : "–",
                record.Sub.Length > 0 ? $"{record.Value} · {record.Sub}" : (best.Value > 0 ? best.Name : ""), Gold);

        var longest = online.OrderByDescending(s => s.Info!.uptimeSeconds).First();
        var availability = AggregateAvailabilityText();
        SetTile(T11Label, T11Value, T11Sub, "LÄNGSTE UPTIME", FormatUptime(longest.Info!.uptimeSeconds),
                availability.Length > 0 ? $"{longest.Config.Name} · {availability}" : longest.Config.Name, Normal);

        SetTile(T12Label, T12Value, T12Sub, "BLÖCKE GEFUNDEN",
                infos.Sum(i => i.blockFound).ToString("N0", De), "alle Miner zusammen",
                infos.Sum(i => i.blockFound) > 0 ? Green : Normal);

        SetCostTiles(power, "gesamt");

        FooterText.Text = string.Join("   |   ", online.Select(s =>
            $"{s.Config.Name}: {FormatHash(s.Info!.hashRate)} / {s.Info.power.ToString("0", De)} W / {s.Info.temp.ToString("0", De)} °C"));

        BlockFoundValue.Text = infos.Sum(i => i.blockFound).ToString("N0", De);
        BlockFoundSub.Text = "alle Miner";

        DrawCharts(ChartData(HistoryStore.AggregateHost, _aggHistory), "Gesamt", HistoryStore.AggregateHost);
        RenderWalletAggregate();
    }

    private void RenderSingle(MinerState state)
    {
        ViewTitle.Text = state.Online
            ? $"{state.Config.Name} – {state.Config.Host}"
            : $"{state.Config.Name} – {state.Error}";

        if (!state.Online)
        {
            foreach (var (l, v, s) in Tiles()) SetTile(l, v, s, l.Text, "–", "", Normal);
            FooterText.Text = state.LastOk is null
                ? "noch nie erreicht"
                : $"zuletzt erreicht: {state.LastOk:dd.MM.yyyy HH:mm:ss}";
            PoolStatusText.Text = "";
            DrawCharts(ChartData(state.Config.Host, state.History), state.Config.Name, state.Config.Host);
            RenderWalletSingle(state);
            return;
        }

        PoolStatusText.Text = PoolText(state);

        var i = state.Info!;
        var th = i.hashRate / 1000.0;

        SetTile(T1Label, T1Value, T1Sub, "HASHRATE", FormatHash(i.hashRate),
                i.expectedHashrate > 0 ? $"erwartet {FormatHash(i.expectedHashrate)}" : "", Green);
        SetTile(T2Label, T2Value, T2Sub, "ASIC TEMP", i.temp.ToString("0.0", De) + " °C",
                i.temptarget > 0 ? $"Ziel {i.temptarget} °C" : "", TempBrush(i.temp));
        SetTile(T3Label, T3Value, T3Sub, "VR TEMP", i.vrTemp > 0 ? i.vrTemp.ToString("0", De) + " °C" : "–",
                i.overheat_mode != 0 ? "OVERHEAT AKTIV" : "", i.overheat_mode != 0 ? Red : Normal);
        SetTile(T4Label, T4Value, T4Sub, "LEISTUNG", i.power.ToString("0.0", De) + " W",
                $"{(i.voltage / 1000.0).ToString("0.00", De)} V / {(i.current / 1000.0).ToString("0.00", De)} A", Normal);
        SetTile(T5Label, T5Value, T5Sub, "EFFIZIENZ", th > 0.01 ? (i.power / th).ToString("0.0", De) : "–", "J/TH", Normal);
        SetTile(T6Label, T6Value, T6Sub, "FREQUENZ", i.frequency.ToString("0", De) + " MHz", i.AsicModel ?? "", Normal);
        SetTile(T7Label, T7Value, T7Sub, "CORE VOLTAGE", i.coreVoltageActual.ToString("0", De) + " mV",
                $"Soll {i.coreVoltage.ToString("0", De)} mV", Normal);
        SetTile(T8Label, T8Value, T8Sub, "LÜFTER", i.fanrpm > 0 ? i.fanrpm.ToString("N0", De) + " rpm" : "–",
                $"{i.fanspeed.ToString("0", De)} %" + (i.autofanspeed != 0 ? " (auto)" : " (manuell)"), Normal);

        var total = i.sharesAccepted + i.sharesRejected;
        SetTile(T9Label, T9Value, T9Sub, "SHARES", i.sharesAccepted.ToString("N0", De),
                total > 0 ? $"{(i.sharesRejected / total * 100).ToString("0.00", De)} % abgelehnt" : "keine", Normal);
        var recordText = RecordText(state.Config.Host);
        SetTile(T10Label, T10Value, T10Sub, "BEST DIFF", i.bestDiff ?? "–",
                recordText.Length > 0 ? recordText : $"Session {i.bestSessionDiff ?? "–"}", Gold);
        var availabilityText = AvailabilityText(state.Config.Host);
        SetTile(T11Label, T11Value, T11Sub, "UPTIME", FormatUptime(i.uptimeSeconds),
                availabilityText.Length > 0 ? availabilityText : (i.freeHeap > 0 ? $"{i.freeHeap / 1024} KB frei" : ""), Normal);
        SetTile(T12Label, T12Value, T12Sub, "WLAN", i.wifiRSSI != 0 ? i.wifiRSSI + " dBm" : "–", i.wifiStatus ?? "", Normal);

        SetCostTiles(i.power, "");

        BlockFoundValue.Text = i.blockFound.ToString("N0", De);
        BlockFoundSub.Text = "laut Miner";
        BlockFoundValue.Foreground = i.blockFound > 0 ? Green : Normal;

        FooterText.Text = string.Join("   |   ", new[]
        {
            i.hostname ?? "-",
            $"Board {i.boardVersion ?? "?"}",
            FirmwareText(state),
            $"Pool {i.stratumURL}:{i.stratumPort}" + (i.isUsingFallbackStratum != 0 ? " (Fallback)" : ""),
            i.stratumUser ?? ""
        });

        DrawCharts(ChartData(state.Config.Host, state.History), state.Config.Name, state.Config.Host);
        RenderWalletSingle(state);
    }

    private IEnumerable<(TextBlock, TextBlock, TextBlock)> Tiles()
    {
        yield return (T1Label, T1Value, T1Sub);
        yield return (T2Label, T2Value, T2Sub);
        yield return (T3Label, T3Value, T3Sub);
        yield return (T4Label, T4Value, T4Sub);
        yield return (T5Label, T5Value, T5Sub);
        yield return (T6Label, T6Value, T6Sub);
        yield return (T7Label, T7Value, T7Sub);
        yield return (T8Label, T8Value, T8Sub);
        yield return (T9Label, T9Value, T9Sub);
        yield return (T10Label, T10Value, T10Sub);
        yield return (T11Label, T11Value, T11Sub);
        yield return (T12Label, T12Value, T12Sub);
        yield return (T13Label, T13Value, T13Sub);
        yield return (T14Label, T14Value, T14Sub);
    }

    // ---------- Wallet-Anzeige ----------

    private void RenderWalletSingle(MinerState state)
    {
        WalletBoxLabel.Text = "WALLET-ADRESSE (leer = aus Stratum-User)";
        var address = state.WalletAddress;

        if (string.IsNullOrWhiteSpace(address))
        {
            ShowWallet("–", "keine Adresse", "–", "", "–", "");
            return;
        }

        if (_walletErrors.TryGetValue(address, out var err))
        {
            ShowWallet("–", err, "–", "", "–", ShortAddress(address));
            return;
        }

        if (!_wallets.TryGetValue(address, out var w))
        {
            ShowWallet("…", ShortAddress(address), "…", "", "…", "");
            return;
        }

        ShowWallet(
            FormatBtc(w.BalanceSat),
            w.UnconfirmedSat != 0 ? $"unbestätigt {FormatBtc(w.UnconfirmedSat)}" : ShortAddress(w.Address),
            w.LastIncomingSat is null ? "keine" : FormatBtc(w.LastIncomingSat.Value),
            w.LastIncomingSat is null ? "noch kein Eingang"
                : w.LastIncomingConfirmed
                    ? (w.LastIncomingTime?.ToString("dd.MM.yyyy HH:mm", De) ?? "bestätigt")
                    : "im Mempool, unbestätigt",
            w.TxCount.ToString("N0", De),
            "ein- und ausgehend");
    }

    private void RenderWalletAggregate()
    {
        WalletBoxLabel.Text = "WALLET-ADRESSE (nur bei Einzelauswahl)";
        WalletBox.IsEnabled = false;

        var infos = _states
            .Select(s => s.WalletAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => _wallets.TryGetValue(a!, out var w) ? w : null)
            .Where(w => w is not null)
            .Select(w => w!)
            .ToList();

        if (infos.Count == 0)
        {
            ShowWallet("–", "keine Daten", "–", "", "–", "");
            return;
        }

        var latest = infos
            .Where(w => w.LastIncomingSat is not null)
            .OrderByDescending(w => w.LastIncomingConfirmed ? w.LastIncomingTime ?? DateTime.MinValue : DateTime.MaxValue)
            .FirstOrDefault();

        ShowWallet(
            FormatBtc(infos.Sum(w => w.BalanceSat)),
            $"{infos.Count} Adresse(n)",
            latest?.LastIncomingSat is null ? "keine" : FormatBtc(latest.LastIncomingSat.Value),
            latest?.LastIncomingSat is null ? "noch kein Eingang"
                : latest.LastIncomingConfirmed
                    ? (latest.LastIncomingTime?.ToString("dd.MM.yyyy HH:mm", De) ?? "bestätigt")
                    : "im Mempool, unbestätigt",
            infos.Sum(w => w.TxCount).ToString("N0", De),
            "ein- und ausgehend");
    }

    private void ShowWallet(string balance, string balanceSub, string payout, string payoutSub,
                            string txCount, string txSub)
    {
        BalanceValue.Text = balance;
        BalanceSub.Text = balanceSub;
        PayoutValue.Text = payout;
        PayoutSub.Text = payoutSub;
        TxCountValue.Text = txCount;
        TxCountSub.Text = txSub;
    }

    // ---------- Formatierung ----------

    private static Brush TempBrush(double t) => t switch
    {
        >= 70 => Red,
        >= 62 => Gold,
        _ => Normal
    };

    private static string FormatHash(double gh)
        => gh >= 1000 ? (gh / 1000.0).ToString("0.00", De) + " TH/s" : gh.ToString("0", De) + " GH/s";

    private static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";
    }

    private static string FormatBtc(long sat)
    {
        var btc = sat / 100_000_000.0;
        return Math.Abs(btc) >= 0.001
            ? btc.ToString("0.00000000", De) + " BTC"
            : sat.ToString("N0", De) + " sat";
    }

    private static string ShortAddress(string a)
        => a.Length > 16 ? a[..8] + "…" + a[^6..] : a;

    // ---------- Charts ----------

    private void DrawCharts(IReadOnlyList<Sample> history, string scope, string host)
    {
        var markers = history.Count >= 2 ? MarkersFor(host, history[0].Time, history[^1].Time) : [];
        DrawChart(HashChart, $"Hashrate {scope} (TH/s)", history, s => s.HashRateGh / 1000.0,
                  ThemeManager.Color("ChartHashColor"), "0.00", markers, labels: true);
        DrawChart(TempChart, scope == "Gesamt" ? "ASIC-Temperatur max (°C)" : "ASIC-Temperatur (°C)",
                  history, s => s.Temp, ThemeManager.Color("ChartTempColor"), "0.0", markers);
        DrawChart(PowerChart, $"Leistung {scope} (W)", history, s => s.Power,
                  ThemeManager.Color("ChartPowerColor"), "0.0", markers);
        // Effizienz aus Hashrate und Leistung (Minuten ohne Hashrate, z. B. nach einem Neustart, ausgelassen)
        DrawChart(EffChart, $"Effizienz {scope} (J/TH)", history.Where(s => s.HashRateGh >= 1).ToList(),
                  s => s.Power / (s.HashRateGh / 1000.0), ThemeManager.Color("ChartEffColor"), "0.0", markers);
    }

    private void DrawChart(Canvas canvas, string title, IReadOnlyList<Sample> history,
                           Func<Sample, double> selector, Color color, string format,
                           IReadOnlyList<Core.Monitoring.TuningEvent>? markers = null, bool labels = false)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 40 || h < 30) return;

        var muted = ThemeManager.Brush("MutedTextBrush");
        canvas.Children.Add(Text(title, 2, 0, 11, muted));

        if (history.Count < 2)
        {
            canvas.Children.Add(Text("warte auf Daten…", 2, 18, 11, muted));
            return;
        }

        var data = history.Select(selector).ToList();

        double left = 4, right = w - 62, top = 20, bottom = h - 14;
        if (right <= left || bottom <= top) return;

        double min = data.Min(), max = data.Max();
        if (max - min < 1e-9) { min -= 1; max += 1; }
        var pad = (max - min) * 0.12;
        min -= pad; max += pad;

        var lineBrush = ThemeManager.Brush("CardBorderBrush");
        for (var k = 0; k <= 3; k++)
        {
            var y = top + (bottom - top) * k / 3.0;
            canvas.Children.Add(new Line
            {
                X1 = left, X2 = right, Y1 = y, Y2 = y,
                Stroke = lineBrush, StrokeThickness = 1, SnapsToDevicePixels = true
            });
        }

        double X(int idx) => left + (right - left) * idx / (data.Count - 1.0);
        double Y(double v) => bottom - (v - min) / (max - min) * (bottom - top);

        var points = new PointCollection(data.Select((v, idx) => new Point(X(idx), Y(v))));

        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection(points) { new Point(right, bottom), new Point(left, bottom) },
            Fill = new SolidColorBrush(Color.FromArgb(0x2A, color.R, color.G, color.B))
        });

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round
        });

        canvas.Children.Add(Text(max.ToString(format, De), right + 6, top - 6, 10, muted));
        canvas.Children.Add(Text(min.ToString(format, De), right + 6, bottom - 6, 10, muted));
        canvas.Children.Add(Text(data[^1].ToString(format, De), right + 6, (top + bottom) / 2 - 9, 15,
                                 new SolidColorBrush(color)));

        var span = history[^1].Time - history[0].Time;
        canvas.Children.Add(Text($"{SpanText(span)} · {data.Count} Werte",
                                 left, bottom + 1, 9, muted));

        DrawMarkers(canvas, history, markers, X, top, bottom, labels);
    }

    // ---------- Markierungen der Tuning-Änderungen ----------

    private readonly Dictionary<string, (DateTime At, List<Core.Monitoring.TuningEvent> Events)> _markerCache = new();

    /// <summary>Tuning-Änderungen im Zeitraum aus history.db, 15 s gecacht ("*" = alle Miner).</summary>
    private IReadOnlyList<Core.Monitoring.TuningEvent> MarkersFor(string host, DateTime from, DateTime to)
    {
        if (_host.History is null) return [];
        var key = $"{host}|{_chartRange}";
        if (_markerCache.TryGetValue(key, out var c) && (DateTime.Now - c.At).TotalSeconds < 15)
            return c.Events;

        List<Core.Monitoring.TuningEvent> events;
        try { events = _host.History.QueryTuningEvents(host == HistoryStore.AggregateHost ? "*" : host, from.AddMinutes(-1), to.AddMinutes(1)); }
        catch { events = []; }
        _markerCache[key] = (DateTime.Now, events);
        return events;
    }

    /// <summary>
    /// Senkrechte Linien an den Zeitpunkten der Änderungen. Manuelle Änderungen und Wiederherstellungen kräftig,
    /// einzelne Benchmark-Schritte dezent. Tooltip mit Zeit, Quelle und altem/neuem Wert.
    /// </summary>
    private void DrawMarkers(Canvas canvas, IReadOnlyList<Sample> history, IReadOnlyList<Core.Monitoring.TuningEvent>? markers,
                             Func<int, double> x, double top, double bottom, bool labels)
    {
        if (markers is null || markers.Count == 0 || history.Count < 2) return;
        var brush = ThemeManager.Brush("MarkerBrush");

        foreach (var e in markers)
        {
            if (e.Time < history[0].Time || e.Time > history[^1].Time) continue;
            var idx = 0;
            while (idx < history.Count - 1 && history[idx].Time < e.Time) idx++;
            var px = x(idx);
            var strong = e.Source != Core.Api.TuningSource.Benchmark;

            var line = new Line
            {
                X1 = px, X2 = px, Y1 = top, Y2 = bottom,
                Stroke = brush,
                StrokeThickness = strong ? 1.6 : 1,
                Opacity = strong ? 0.95 : 0.35,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                ToolTip = $"{e.Time.ToString("dd.MM. HH:mm", De)} · {e.SourceText}\n{e.ChangeText}" +
                          (e.Host != "*" && _states.FirstOrDefault(s => s.Config.Host == e.Host) is { } st ? $"\n{st.Config.Name}" : ""),
            };
            canvas.Children.Add(line);
            // Breitere, unsichtbare Fläche, damit der Tooltip leicht zu treffen ist
            canvas.Children.Add(new Rectangle
            {
                Width = 8, Height = Math.Max(1, bottom - top), Fill = Brushes.Transparent, ToolTip = line.ToolTip,
                Margin = new Thickness(px - 4, top, 0, 0),
            });

            if (labels && strong)
                canvas.Children.Add(Text($"{e.NewFrequencyMhz} MHz / {e.NewCoreVoltageMv} mV", px + 3, top, 9, brush));
        }
    }

    private static TextBlock Text(string s, double x, double y, double size, Brush brush)
    {
        var tb = new TextBlock { Text = s, FontSize = size, Foreground = brush };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    // ---------- Netzwerk: Blöcke und Pools ----------

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e) => await PollNetworkAsync(true);

    private async void Period_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string period)
        {
            _poolPeriod = period;
            await PollNetworkAsync(true);
        }
    }

    private async Task PollNetworkAsync(bool force)
    {
        if (_networkBusy) return;
        if (!force && (DateTime.Now - _blocksFetched).TotalSeconds < 60) return;

        _networkBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            try
            {
                var blocks = await _networkClient.GetBlocksAsync(cts.Token);
                ShowBlocks(blocks);
                _blocksFetched = DateTime.Now;
                BlocksStatus.Text = $"Stand {DateTime.Now.ToString("HH:mm:ss", De)} · Quelle mempool.space";
            }
            catch (Exception ex)
            {
                BlocksStatus.Text = "Blöcke: " + Shorten(ex);
            }

            try
            {
                var pools = await _networkClient.GetPoolsAsync(_poolPeriod, cts.Token);
                ShowPools(pools);
            }
            catch (Exception ex)
            {
                PoolStatus.Text = "Pools: " + Shorten(ex);
            }
        }
        finally
        {
            _networkBusy = false;
        }
    }

    private void ShowBlocks(List<BlockDto> blocks)
    {
        _blockRows.Clear();
        foreach (var b in blocks)
        {
            var age = DateTime.Now - b.Time;
            var ageText = age.TotalMinutes < 60
                ? $"vor {Math.Max(0, (int)age.TotalMinutes)} min"
                : $"vor {(int)age.TotalHours} h";

            _blockRows.Add(new BlockRow
            {
                Height = b.Height.ToString("N0", De),
                Time = $"{b.Time.ToString("HH:mm", De)}\n{ageText}",
                Pool = b.Pool,
                Details = $"{b.TxCount.ToString("N0", De)} TX · {FormatBtc(b.RewardSat)} · {(b.SizeBytes / 1_000_000.0).ToString("0.00", De)} MB",
                PoolBrush = IsOwnPool(b.Pool, b.PoolSlug) ? Gold : Normal
            });
        }
    }

    private void ShowPools(List<PoolDto> pools)
    {
        _poolRows.Clear();
        if (pools.Count == 0)
        {
            PoolStatus.Text = "keine Daten";
            return;
        }

        var total = pools.Sum(p => (double)p.BlockCount);
        var max = pools.Max(p => p.BlockCount);
        var rank = 0;

        foreach (var p in pools.Take(30))
        {
            rank++;
            var own = IsOwnPool(p.Name, p.Slug);
            _poolRows.Add(new PoolRow
            {
                Rank = rank.ToString(),
                Name = p.Name,
                Blocks = p.BlockCount.ToString("N0", De),
                Share = (p.BlockCount / total * 100).ToString("0.0", De) + " %",
                BarWidth = max > 0 ? Math.Max(2, 190.0 * p.BlockCount / max) : 2,
                BarBrush = own ? Gold : new SolidColorBrush(Color.FromRgb(0x6E, 0x9F, 0xE8))
            });
        }

        PoolStatus.Text = $"{PeriodLabel(_poolPeriod)} · {total.ToString("N0", De)} Blöcke gesamt · " +
                          $"{pools.Count} Pools · gelb = dein Pool";
    }

    private static string PeriodLabel(string p) => p switch
    {
        "24h" => "letzte 24 Stunden",
        "1w" => "letzte Woche",
        "1m" => "letzter Monat",
        "1y" => "letztes Jahr",
        "all" => "gesamter Zeitraum",
        _ => p
    };

    /// <summary>Heuristischer Abgleich des Pool-Namens mit den Stratum-URLs der eigenen Miner.</summary>
    private bool IsOwnPool(string poolName, string slug)
    {
        var candidates = _states
            .Select(s => s.Info?.stratumURL)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => Normalize(u!))
            .ToList();

        if (candidates.Count == 0) return false;

        var name = Normalize(poolName);
        var sl = Normalize(slug);

        return candidates.Any(c =>
            (name.Length >= 4 && (c.Contains(name) || name.Contains(c))) ||
            (sl.Length >= 4 && (c.Contains(sl) || sl.Contains(c))));
    }

    private static string Normalize(string s)
        => new(s.ToLowerInvariant()
                .Replace("stratum+tcp://", "")
                .Replace("www.", "")
                .Where(char.IsLetterOrDigit)
                .ToArray());

    // ---------- Einstellungen ----------

    /// <summary>Nach "Speichern" im Einstellungsfenster (vormals SettingsButton_Click).</summary>
    public async Task ApplySettingsChangedAsync()
    {
        _networkTimer.Stop();
        StartTimers();

        // Geänderte Miner, Repositories oder Coins wirken sofort
        _chartCache.Clear();
        _availabilityCache.Clear();

        await _host.Hub.ApplySettingsChangedAsync();
    }

    // ---------- Stromkosten ----------

    private double CostPerDay(double watt) => watt * 24 / 1000.0 * _config.ElectricityCtPerKwh / 100.0;

    private string Money(double value) => value.ToString("0.00", De) + " " + _config.Currency;

    private void SetCostTiles(double watt, string scope)
    {
        var day = CostPerDay(watt);
        SetTile(T13Label, T13Value, T13Sub, "STROMKOSTEN/TAG", Money(day),
                $"{(watt * 24 / 1000.0).ToString("0.00", De)} kWh {scope}", Normal);
        SetTile(T14Label, T14Value, T14Sub, "STROMKOSTEN/MONAT", Money(day * 30.44),
                $"{_config.ElectricityCtPerKwh.ToString("0.##", De)} ct/kWh · Jahr {Money(day * 365)}", Normal);
    }

    // ---------- Steuer-Modul ----------

    /// <summary>
    /// Adressen aller Miner für die Übernahme ins Steuer-Modul: manueller
    /// Override aus den Einstellungen oder aus dem Stratum-User abgeleitet.
    /// </summary>
    private IEnumerable<MinerAddressCandidate> MinerAddressCandidates()
    {
        foreach (var s in _states)
        {
            var address = s.WalletAddress;
            if (!string.IsNullOrWhiteSpace(address))
                yield return new MinerAddressCandidate(s.Config.Name, s.Config.Host, address);
        }
    }
}
