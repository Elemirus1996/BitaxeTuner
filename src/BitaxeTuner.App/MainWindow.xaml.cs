using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Themes;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.App.Views;
using BitaxeTuner.Core.Config;

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

    public void Initialize(MainViewModel vm)
    {
        DataContext = vm;
        _monitor.Initialize(vm.Host);
        _monitor.SummaryChanged += vm.ApplySummary;
        vm.Host.Polled += vm.OnPolled;
        vm.Rebuild();
        PlaceMonitor();
        if (vm.Host.ApplyWebView() is { Length: > 0 } web) vm.StatusText = web;
        _ = vm.CheckForUpdateAsync();
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

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var host = Vm.Host;
        var dialog = new SettingsWindow(host.Config, host.DataDirectory, MoveDataDirectoryAsync,
            () => host.Hub.SendDailyReportAsync(DateTime.Now, markSent: false)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        ThemeManager.Apply(host.Config.Theme);
        Vm.Rebuild();
        if (host.ApplyWebView() is { Length: > 0 } web) Vm.StatusText = web;
        await _monitor.ApplySettingsChangedAsync();
        PlaceMonitor();
    }

    private void OnModeClick(object sender, RoutedEventArgs e) =>
        new ServerModeWindow(Vm.Host.Config, Vm.Host) { Owner = this }.ShowDialog();

    /// <summary>Datenordner umziehen: Abfragen anhalten, kopieren und prüfen, erst dann umschalten.</summary>
    private async Task<DataDirectoryMigrator.Result> MoveDataDirectoryAsync(string target)
    {
        var host = Vm.Host;
        if (Vm.AnyRunning)
            return new DataDirectoryMigrator.Result(false, "Bitte zuerst alle Benchmarks stoppen.", []);

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
                "Es laufen noch Benchmarks. Beenden und die Einstellungen der Geräte wiederherstellen?",
                "BitaxeTuner beenden", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            IsEnabled = false;
            Title = "BitaxeTuner – stelle Einstellungen wieder her …";
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
