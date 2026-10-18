using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Themes;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.App.Views;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App;

/// <summary>
/// Hülle der zusammengeführten App: Geräteliste (Gesamt · Miner · Netzwerk · Steuer) links, rechts entweder
/// die Überwachungsansicht aus BitaxeMonitor oder – bei einem Miner – dessen Tabs (Überwachung, Live,
/// Benchmark, Ergebnisse, Vorher/Nachher, Protokoll). Es gibt genau eine <see cref="MonitorView"/>; sie wird
/// zwischen Hauptbereich und Geräte-Tab umgehängt, ihre Timer laufen unabhängig davon weiter.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MonitorView _monitor = new();
    private ComparisonView? _comparison;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // ---------- 0.9.14: Miner in der Seitenleiste per Ziehen sortieren (gilt überall wie im Browser) ----------

    private Point _dragStart;
    private NavItem? _dragItem;

    private void WireMinerDrag()
    {
        NavList.AllowDrop = true;
        NavList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(null);
            _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as NavItem;
        };
        NavList.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragItem is not { Kind: NavKind.Device } item) return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragItem = null;
            DragDrop.DoDragDrop(NavList, item, DragDropEffects.Move);
        };
        NavList.DragOver += (_, e) =>
        {
            var target = (e.OriginalSource as FrameworkElement)?.DataContext as NavItem;
            e.Effects = e.Data.GetData(typeof(NavItem)) is NavItem && target is { Kind: NavKind.Device } ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        NavList.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(NavItem)) is not NavItem { Host: { } src }) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not NavItem { Kind: NavKind.Device, Host: { } dst } || src == dst) return;
            var hosts = Vm.NavItems.Where(n => n.Kind == NavKind.Device && n.Host is not null).Select(n => n.Host!).ToList();
            hosts.Remove(src);
            hosts.Insert(Math.Max(0, hosts.IndexOf(dst) + (Vm.NavItems.ToList().FindIndex(n => n.Host == src) < Vm.NavItems.ToList().FindIndex(n => n.Host == dst) ? 1 : 0)), src);
            Vm.Host.Hub.ReorderDevices(hosts, L.T("Desktop"));
        };
    }

    public void Initialize(MainViewModel vm)
    {
        DataContext = vm;
        _monitor.Initialize(vm.Host);
        _monitor.SummaryChanged += vm.ApplySummary;
        // 0.9.14: Karte in der Gesamtansicht angeklickt → Miner in der Seitenleiste wählen
        _monitor.MinerRequested += host => NavList.SelectedItem = vm.NavItems.FirstOrDefault(n => string.Equals(n.Host, host, StringComparison.OrdinalIgnoreCase)) ?? NavList.SelectedItem;
        vm.Host.Polled += vm.OnPolled;
        vm.Host.Polled += () => AskWalletConsent(vm);
        vm.Rebuild();
        PlaceMonitor();
        if (vm.Host.ApplyWebView() is { Length: > 0 } web) vm.StatusText = web;
        _ = vm.CheckForUpdateAsync();
        UpdateSshButton();
        WireMinerDrag();
        ThemeManager.ApplyAppearance(vm.Host.Config.Appearance);
        // Einführung „Erste Schritte“: automatisch nur, solange noch kein Miner eingetragen ist
        var config = vm.Host.Config;
        var version = MainViewModel.CurrentVersion.ToString(3);
        if (Onboarding.ShouldShow(config) && config.Devices.Count == 0)
        {
            WhatsNew.MarkSeen(config, version); // neue Installation: „Erste Schritte“ statt „Neu in …“
            Loaded += (_, _) => Dispatcher.BeginInvoke(ShowOnboarding, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        // Nach einem Update einmal fragen, ob eine Einführung nur in die neuen Funktionen gewünscht ist
        else if (WhatsNew.ShouldAsk(config, version, server: false))
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => AskWhatsNew(version), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private bool _walletConsentAsked;

    /// <summary>
    /// Einmal fragen, bevor aus dem Pool-Benutzer erkannte Wallet-Adressen an mempool.space/Blockchair gehen (Audit P1).
    /// Selbst eingetragene Adressen brauchen keine Zustimmung.
    /// </summary>
    private void AskWalletConsent(MainViewModel vm)
    {
        if (_walletConsentAsked || !vm.Host.Hub.WalletConsentNeeded) return;
        _walletConsentAsked = true;
        Dispatcher.BeginInvoke(async () =>
        {
            var allow = MessageBox.Show(this,
                L.T("Deine Miner melden eine Wallet-Adresse im Pool-Benutzer. Soll BitaxeTuner Guthaben und Eingänge dieser Adressen bei mempool.space (BTC) bzw. Blockchair (BCH) abfragen? Dabei sehen diese Dienste die Adresse und deine IP-Adresse. Ohne Zustimmung wird nichts abgefragt; ändern kannst du das jederzeit in den Einstellungen."),
                L.T("Wallet-Guthaben abfragen?"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            await vm.Host.Hub.SetWalletConsentAsync(allow);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void AskWhatsNew(string version)
    {
        var config = Vm.Host.Config;
        var features = WhatsNew.Since(config.LastSeenVersion, version, server: false);
        WhatsNew.MarkSeen(config, version);
        config.Save();
        if (features.Count == 0) return;
        if (MessageBox.Show(this, L.T("BitaxeTuner wurde auf {0} aktualisiert. Kurze Einführung in die {1} neuen Funktionen?", version, features.Count),
                L.T("Neu in {0}", version), MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        new WhatsNewWindow(version, features, (section, owner) => OpenSettings(section, owner)) { Owner = this }.ShowDialog();
    }

    /// <summary>Einführung anzeigen; ihre Schritte öffnen die Einstellungen am passenden Abschnitt bzw. die Betriebsart.</summary>
    private void ShowOnboarding()
    {
        var host = Vm.Host;
        new OnboardingWindow(host.Config,
            (section, owner) => OpenSettings(section, owner),
            owner => new ServerModeWindow(host.Config, host) { Owner = owner }.ShowDialog()) { Owner = this }.ShowDialog();
    }

    /// <summary>Pool-Symbol in der Geräteliste: Nutzerseite des Pools im Browser öffnen.</summary>
    private void OnPoolLinkClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not Core.Monitoring.PoolQuickLink link) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Url.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        PlaceMonitor();
        UpdateLogVisibility();
    }

    // ---------- Miner-Logs: Live-Verbindung nur, solange der Tab sichtbar ist ----------

    private LogViewModel? _visibleLogs;

    private void OnDeviceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged von Listen/Tabellen innerhalb der Tabs steigt hier ebenfalls auf – nur die Tabs selbst zählen
        if (ReferenceEquals(e.OriginalSource, DeviceTabs)) UpdateLogVisibility();
    }

    private void UpdateLogVisibility()
    {
        var logs = (NavList.SelectedItem as NavItem)?.Device?.Logs;
        var target = logs is not null && ReferenceEquals(DeviceTabs.SelectedItem, LogsTab) ? logs : null;
        if (ReferenceEquals(target, _visibleLogs)) return;

        if (_visibleLogs is not null)
        {
            _visibleLogs.LinesAppended -= ScrollLogs;
            _visibleLogs.SetVisible(false);
        }
        _visibleLogs = target;
        if (target is not null)
        {
            target.LinesAppended += ScrollLogs;
            target.SetVisible(true);
            ScrollLogs();
        }
    }

    private void ScrollLogs()
    {
        if (_visibleLogs is { AutoScroll: true, Paused: false } && MinerLogList.Items.Count > 0)
            MinerLogList.ScrollIntoView(MinerLogList.Items[^1]);
    }

    private void PlaceMonitor()
    {
        if (DataContext is not MainViewModel) return;
        var nav = NavList.SelectedItem as NavItem;

        if (nav?.Kind == NavKind.Device && nav.Host is { } host)
        {
            // Von Gesamt/Netzwerk/Steuer kommend mit der Überwachung beginnen
            if (MonitorSlot.Content is null) DeviceTabs.SelectedIndex = 0;
            MainContent.Content = null;
            MonitorSlot.Content = _monitor;
            _monitor.Show(MonitorMode.Device, host);
            return;
        }

        MonitorSlot.Content = null;
        if (nav?.Kind == NavKind.Compare)
        {
            _comparison ??= new ComparisonView { DataContext = Vm.Comparison };
            MainContent.Content = _comparison;
            Vm.Comparison.Refresh();
            return;
        }
        MainContent.Content = _monitor;
        _monitor.Show(nav?.Kind switch
        {
            NavKind.Network => MonitorMode.Network,
            NavKind.Tax => MonitorMode.Tax,
            _ => MonitorMode.Aggregate,
        });
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings(null);

    /// <summary>0.9.24 Schnellsuche: Strg+K öffnet die Suche nach Ansichten, Minern und Einstellungsabschnitten.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.K && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            OpenQuickSearch();
        }
    }

    private void OpenQuickSearch()
    {
        var page = L.T("Seite");
        var miner = L.T("Miner");
        var setting = L.T("Einstellung");
        var items = new List<Core.Help.QuickSearchItem>();
        foreach (var n in Vm.NavItems)
            items.Add(new Core.Help.QuickSearchItem(n.Kind == NavKind.Device ? miner : page, n.Title, n.Sub, n));
        items.Add(new Core.Help.QuickSearchItem(page, L.T("Einstellungen"), "", "settings"));
        foreach (var s in Core.Help.QuickSearch.DesktopSettingsSections)
            items.Add(new Core.Help.QuickSearchItem(setting, L.T(s), L.T("Einstellungen"), "settings:" + s));
        var dlg = new QuickSearchWindow(items, setting) { Owner = this };
        dlg.ShowDialog();
        switch (dlg.Chosen?.Tag)
        {
            case NavItem n: Vm.SelectedNav = n; break;
            case "settings": OpenSettings(null); break;
            case string s when s.StartsWith("settings:", StringComparison.Ordinal): OpenSettings(L.T(s["settings:".Length..])); break;
        }
    }

    /// <summary>Einstellungen öffnen, optional direkt an einem Abschnitt (z. B. „Smart Plugs“).</summary>
    private async void OpenSettings(string? section, Window? owner = null)
    {
        var host = Vm.Host;
        var language = host.Config.Language;
        var dialog = new SettingsWindow(host.Config, host.DataDirectory, MoveDataDirectoryAsync,
            () => host.Hub.SendDailyReportAsync(DateTime.Now, markSent: false), host, section) { Owner = owner ?? this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ShowOnboardingRequested) _ = Dispatcher.BeginInvoke(ShowOnboarding);
        await host.Hub.ApplyPlugSettingsAsync();

        ThemeManager.Apply(host.Config.Theme);
        Vm.Rebuild();
        if (host.ApplyWebView() is { Length: > 0 } web) Vm.StatusText = web;
        await _monitor.ApplySettingsChangedAsync();
        PlaceMonitor();

        // Sprache: gilt ab dem nächsten Start (alle Fenster und Texte werden beim Start aufgebaut)
        if (host.Config.Language != language)
        {
            if (Vm.AnyRunning)
                MessageBox.Show(this, L.T("Die Sprache wird beim nächsten Start der App umgestellt."), L.T("Sprache"), MessageBoxButton.OK, MessageBoxImage.Information);
            else if (MessageBox.Show(this, L.T("Die Sprache wird nach einem Neustart der App umgestellt. Jetzt neu starten?"), L.T("Sprache"),
                         MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Services.ServerTransfer.Restart();
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        new ServerModeWindow(Vm.Host.Config, Vm.Host) { Owner = this }.ShowDialog();
        UpdateSshButton();
    }

    /// <summary>„SSH-Terminal“ oben nur, wenn ein Server bekannt ist (Adresse oder eigener SSH-Rechner).</summary>
    private void UpdateSshButton()
    {
        var s = Vm.Host.Config.Server;
        SshButton.Visibility = Services.SshKeyService.HostFor(s) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Konsole auf dem Server öffnen – Benutzer und Rechner wie unter „Betriebsart … → SSH-Terminal zum Server“.</summary>
    private async void OnSshClick(object sender, RoutedEventArgs e) =>
        await Services.SshKeyService.OpenForServerAsync(this, Vm.Host.Config.Server, () => OnModeClick(sender, e));

    /// <summary>Rechtliche Hinweise nach GPL-3.0 §5(d): Copyright, keine Gewähr, Lizenz, Quelltext.</summary>
    private void OnLicenseClick(object sender, RoutedEventArgs e)
    {
        var dir = AppContext.BaseDirectory;
        var notices = System.IO.Path.Combine(dir, "THIRD-PARTY-NOTICES.txt");
        var text = L.T("BitaxeTuner {0}\n", ViewModels.MainViewModel.CurrentVersion.ToString(3)) +
                   L.T("Copyright © 2026 BitaxeTuner contributors\n\n") +
                   L.T("Dieses Programm ist freie Software: Du kannst es unter den Bedingungen der GNU General Public License v3.0 ") +
                   L.T("weitergeben und/oder verändern.\n\n") +
                   L.T("Es wird OHNE JEDE GEWÄHR bereitgestellt, auch ohne die Gewähr der Marktreife oder der Eignung für einen ") +
                   L.T("bestimmten Zweck. Übertakten geschieht auf eigenes Risiko.\n\n") +
                   "Quelltext: https://github.com/Elemirus1996/BitaxeTuner\n" +
                   L.T("Lizenztext: LICENSE.txt · Enthaltene Komponenten anderer Urheber: THIRD-PARTY-NOTICES.txt (im Programmordner)\n\n") +
                   L.T("Hinweise zu den enthaltenen Komponenten jetzt öffnen?");
        if (MessageBox.Show(this, text, L.T("Lizenz"), MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var target = System.IO.File.Exists(notices) ? notices : "https://github.com/Elemirus1996/BitaxeTuner/blob/main/THIRD-PARTY-NOTICES.txt";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Lizenz")); }
    }

    private void OnSoakBatchClick(object sender, RoutedEventArgs e) =>
        new SoakBatchWindow(Vm.Host) { Owner = this }.ShowDialog();

    private void OnCopySettingsClick(object sender, RoutedEventArgs e) =>
        new CopySettingsWindow(Vm.Host) { Owner = this }.ShowDialog();

    private void OnPoolSwitchClick(object sender, RoutedEventArgs e) =>
        new Views.PoolSwitchWindow(Vm.Host, Vm.SelectedDevice?.Device) { Owner = this }.Show();

    private void OnPoolPresetsClick(object sender, RoutedEventArgs e) =>
        new Views.PoolPresetsWindow(Vm.Host) { Owner = this }.Show();

    private void OnReportClick(object sender, RoutedEventArgs e) =>
        new ReportWindow(Vm.Host) { Owner = this }.ShowDialog();

    private void OnSmartPlugsClick(object sender, RoutedEventArgs e) => OpenSettings(L.T("Smart Plugs"));

    private void OnJournalClick(object sender, RoutedEventArgs e) => new Views.JournalWindow(Vm.Host) { Owner = this }.Show();

    private void OnHelpClick(object sender, RoutedEventArgs e) => new Views.HelpWindow { Owner = this }.Show();

    /// <summary>0.9.16: Feature-Wunsch als GitHub-Issue (Vorlage mit Label „Kundenwunsch“ → Projekt-Board).</summary>
    private void OnWishClick(object sender, RoutedEventArgs e)
    {
        // Ohne Konto direkt in der App (Wunsch-Briefkasten), mit Konto wahlweise das GitHub-Formular
        var version = ViewModels.MainViewModel.CurrentVersion.ToString(3);
        var url = "https://github.com/" + Services.UpdateChecker.Repository + "/issues/new?template=feature_request.yml&labels=enhancement,Kundenwunsch&version="
                  + Uri.EscapeDataString(version);
        new Views.WishWindow(version, url) { Owner = this }.ShowDialog();
    }

    private void OnStoredLogsClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedDevice is { } d) new Views.StoredLogWindow(Vm.Host.Hub, d.Device) { Owner = this }.Show();
    }

    private void OnProfilesClick(object sender, RoutedEventArgs e) => new Views.ProfilesWindow(Vm.Host.Hub) { Owner = this }.Show();

    /// <summary>Datenordner umziehen: Abfragen anhalten, kopieren und prüfen, erst dann umschalten.</summary>
    private async Task<DataDirectoryMigrator.Result> MoveDataDirectoryAsync(string target)
    {
        var host = Vm.Host;
        if (Vm.AnyRunning)
            return new DataDirectoryMigrator.Result(false, L.T("Bitte zuerst alle Benchmarks stoppen."), []);

        // Miner-, Wallet- und Netzwerkabfragen ruhen. Schreibt der Steuer-Monitor zufällig während des Kopierens,
        // erkennt die SHA-256-Prüfung die Abweichung und es wird nicht umgeschaltet.
        _monitor.SetPaused(true);
        host.Config.Save();
        var result = await Task.Run(() => DataDirectoryMigrator.Migrate(DataPaths.Current, target, host.History, DataPaths.BootstrapFile));
        if (!result.Success) _monitor.SetPaused(false);
        return result;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || DataContext is not MainViewModel vm) return;

        if (vm.AnyRunning)
        {
            e.Cancel = true;
            var answer = MessageBox.Show(this,
                L.T("Es laufen noch Benchmarks. Beenden und die Einstellungen der Geräte wiederherstellen?"),
                L.T("BitaxeTuner beenden"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            IsEnabled = false;
            Title = L.T("BitaxeTuner – stelle Einstellungen wieder her …");
            await vm.StopAllAndWaitAsync();
            _closingConfirmed = true;
            _visibleLogs?.SetVisible(false);
            _monitor.Shutdown();
            Close();
            return;
        }

        _closingConfirmed = true;
        _visibleLogs?.SetVisible(false); // WebSocket-Platz auf dem Miner freigeben
        _monitor.Shutdown();
    }
}
