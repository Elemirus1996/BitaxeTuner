using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using BitaxeTuner.App.Services;
using BitaxeTuner.App.Themes;
using BitaxeTuner.App.Views;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Discovery;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.ViewModels;

public enum NavKind
{
    Aggregate,
    Device,
    Network,
    Tax,
    Compare,
}

/// <summary>Eintrag der Geräteliste links (wie in BitaxeMonitor: Gesamt, je Miner, Netzwerk, Steuer).</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(NavKind kind, string title, string sub, DeviceViewModel? device = null)
    {
        Kind = kind;
        _title = title;
        _sub = sub;
        Device = device;
    }

    public NavKind Kind { get; }
    public DeviceViewModel? Device { get; }
    public string? Host => Device?.Address;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _sub;
    [ObservableProperty] private Brush _dot = Brushes.Gray;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PoolLinkToolTip))] private PoolQuickLink? _poolLink;

    public string? PoolLinkToolTip => PoolLink is { } p ? L.T("{0}: Pool-Statistik öffnen", p.Pool) : null;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dictionary<string, DeviceViewModel> _devices = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(AppHost host)
    {
        _host = host;
        VersionText = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");
        if (host.StartupNotes.Count > 0) StatusText = host.StartupNotes[^1];
    }

    public AppHost Host => _host;
    public ComparisonViewModel Comparison => _comparison ??= new ComparisonViewModel(_host);
    private ComparisonViewModel? _comparison;
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public IEnumerable<DeviceViewModel> Devices => _devices.Values;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedDevice))]
    [NotifyCanExecuteChangedFor(nameof(RemoveDeviceCommand))]
    private NavItem? _selectedNav;

    public DeviceViewModel? SelectedDevice => SelectedNav?.Device;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))] private string _newAddress = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanNetworkCommand))] private bool _isScanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _statusText = L.T("Bereit");
    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private string? _updateUrl;

    public string VersionText { get; }
    public string DataDirectory => _host.DataDirectory;
    public bool AnyRunning => _devices.Values.Any(d => d.IsRunning);

    /// <summary>Geräteliste aus config.json übernehmen (Start, Hinzufügen, Entfernen, Einstellungen gespeichert).</summary>
    public void Rebuild()
    {
        _host.Hub.SyncDevices();
        var selectedKind = SelectedNav?.Kind ?? NavKind.Aggregate;
        var selectedHost = SelectedNav?.Host;

        var hubDevices = _host.Hub.Devices;
        foreach (var gone in _devices.Where(d => !hubDevices.Contains(d.Value.Device)).Select(d => d.Key).ToList())
        {
            _devices[gone].Dispose();
            _devices.Remove(gone);
        }

        foreach (var device in hubDevices)
        {
            if (_devices.ContainsKey(device.Config.Host)) continue;
            var vm = new DeviceViewModel(device, _host);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeviceViewModel.IsRunning))
                {
                    OnPropertyChanged(nameof(AnyRunning));
                    StopAllCommand.NotifyCanExecuteChanged();
                }
            };
            vm.Initialize();
            _devices[device.Config.Host] = vm;
        }

        NavItems.Clear();
        NavItems.Add(new NavItem(NavKind.Aggregate, L.T("Gesamt"), L.T("alle Miner")) { Dot = ThemeManager.Brush("IdleBrush") });
        foreach (var state in _host.Polling.States)
        {
            var vm = _devices[state.Config.Host];
            NavItems.Add(new NavItem(NavKind.Device, state.Config.Name, state.Config.Host, vm) { Dot = ThemeManager.Brush("IdleBrush") });
        }
        NavItems.Add(new NavItem(NavKind.Network, L.T("Netzwerk"), L.T("Blöcke & Pool-Ranking")) { Dot = ThemeManager.Brush("InfoBrush") });
        NavItems.Add(new NavItem(NavKind.Compare, L.T("Vergleich"), L.T("Miner nebeneinander")) { Dot = ThemeManager.Brush("AccentBrush") });
        NavItems.Add(new NavItem(NavKind.Tax, L.T("Steuer"), L.T("Zuflüsse dokumentieren")) { Dot = ThemeManager.Brush("WarnBrush") });

        SelectedNav = NavItems.FirstOrDefault(n => n.Kind == selectedKind &&
                                                   (selectedKind != NavKind.Device || string.Equals(n.Host, selectedHost, StringComparison.OrdinalIgnoreCase)))
                      ?? NavItems[0];
        OnPropertyChanged(nameof(Devices));
    }

    /// <summary>Nach jeder zentralen Abfragerunde: Live-Werte der Tuning-Ansichten aktualisieren.</summary>
    public void OnPolled()
    {
        foreach (var vm in _devices.Values) vm.OnPolled();
    }

    /// <summary>Kurztexte aus der Überwachung in die Geräteliste übernehmen.</summary>
    public void ApplySummary(MonitorSummary summary)
    {
        foreach (var item in NavItems)
        {
            switch (item.Kind)
            {
                case NavKind.Aggregate:
                    item.Sub = summary.AggregateSub;
                    item.Dot = summary.AggregateDot;
                    break;
                case NavKind.Device when item.Host is { } host && summary.Devices.TryGetValue(host, out var d):
                    item.Title = item.Device!.Title;
                    item.Sub = d.Sub;
                    item.Dot = d.Dot;
                    item.PoolLink = d.PoolLink;
                    break;
            }
        }
    }

    // ---------- Updates (GitHub-Releases) ----------

    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private Core.Update.UpdateInfo? _pendingUpdate;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))] private bool _isUpdating;

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Beim Start und danach alle 6 h (wenn in den Einstellungen erlaubt).</summary>
    public async Task CheckForUpdateAsync()
    {
        if (_updateTimer is null)
        {
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            _updateTimer.Start();
        }
        if (!_host.Config.CheckForUpdates) return;
        await RunUpdateCheckAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckUpdatesNow() => RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        var result = await _host.Updates.CheckAsync(CurrentVersion);
        if (result.Status == Core.Update.UpdateCheckStatus.UpdateAvailable && result.Update is { } u)
        {
            _pendingUpdate = u;
            UpdateText = L.T("Update {0} installieren", u.Tag);
            UpdateUrl = u.ReleaseUrl;
            InstallUpdateCommand.NotifyCanExecuteChanged();
            if (_host.Config.NotifiedAppVersion != u.Tag && _host.Notify.Enabled && _host.Config.Notifications.OnMaintenance)
            {
                _host.Config.NotifiedAppVersion = u.Tag;
                _host.Config.Save();
                _host.SendAlert(new Core.Monitoring.Alert($"app-update:{u.Tag}", L.T("BitaxeTuner {0} verfügbar", u.Tag),
                    L.T("Installiert ist {0}. Installation per Klick in der App.", VersionText), Core.Monitoring.NotifyPriority.Low, TimeSpan.FromDays(30)));
            }
        }
        // Automatische Prüfung bleibt still (z. B. solange das Repository privat ist); nur bei "Jetzt prüfen" melden
        if (manual || result.Status == Core.Update.UpdateCheckStatus.UpdateAvailable)
            StatusText = result.Message;
    }

    private bool CanInstallUpdate() => _pendingUpdate is not null && !IsUpdating;

    /// <summary>
    /// Setup vom Release laden, Prüfsumme kontrollieren, laufende Benchmarks sauber beenden (Einstellungen werden
    /// wiederhergestellt), still installieren. Das Setup startet die App danach neu.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (_pendingUpdate is not { } u) return;
        var notes = u.Notes.Length > 900 ? u.Notes[..900] + " …" : u.Notes;
        var text = L.T("BitaxeTuner {0} installieren? (installiert: {1})\n\n", u.Tag, VersionText) +
                   (notes.Length > 0 ? notes + "\n\n" : "") +
                   L.T("Setup: {0} ({1:0.0} MB), Prüfsumme wird kontrolliert.\n", u.SetupName, u.SetupSize / 1024.0 / 1024.0) +
                   (AnyRunning ? L.T("Laufende Benchmarks werden gestoppt und die ursprünglichen Einstellungen wiederhergestellt.\n") : "") +
                   L.T("Die App wird beendet, aktualisiert und danach neu gestartet. Deine Daten bleiben unverändert.");
        if (MessageBox.Show(text, L.T("Update installieren"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        IsUpdating = true;
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), L.T("BitaxeTuner-Update"));
            var file = await _host.Updates.DownloadAsync(u, dir,
                new Progress<double>(p => StatusText = L.T("Lade {0} … {1:P0}", u.SetupName, p)));
            StatusText = L.T("Prüfsumme in Ordnung – beende laufende Vorgänge …");
            await StopAllAndWaitAsync();
            _host.Config.Save();

            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            StatusText = L.T("Update fehlgeschlagen: ") + ex.Message;
            MessageBox.Show(ex.Message, L.T("Update"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewAddress);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddDevice()
    {
        var host = NewAddress.Trim();
        if (_host.Config.Devices.Any(d => string.Equals(d.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(L.T("Dieser Host ist bereits eingetragen."), L.T("Gerät hinzufügen"));
            return;
        }
        var name = NewName.Trim();
        _host.Config.Devices.Add(new DeviceConfig { Name = name.Length > 0 ? name : host, Host = host });
        _host.Config.Save();
        NewAddress = "";
        NewName = "";
        Rebuild();
        SelectedNav = NavItems.FirstOrDefault(n => string.Equals(n.Host, host, StringComparison.OrdinalIgnoreCase)) ?? SelectedNav;
    }

    private bool CanRemove() => SelectedNav?.Kind == NavKind.Device;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveDevice()
    {
        if (SelectedDevice is not { } device) return;
        if (device.IsRunning)
        {
            MessageBox.Show(L.T("Bitte zuerst den laufenden Benchmark stoppen."), L.T("Gerät entfernen"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(L.T("\"{0}\" entfernen?\n\nVerlauf in history.db und Steuerdaten bleiben erhalten.", device.Title), L.T("Gerät entfernen"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _host.Config.Devices.RemoveAll(d => string.Equals(d.Host.Trim(), device.Address, StringComparison.OrdinalIgnoreCase));
        _host.Config.Save();
        SelectedNav = NavItems[0];
        Rebuild();
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanNetwork()
    {
        IsScanning = true;
        ScanProgress = 0;
        StatusText = L.T("Durchsuche lokales Netzwerk …");
        try
        {
            var found = await NetworkScanner.ScanAsync(new Progress<double>(p => ScanProgress = p * 100));
            var added = 0;
            foreach (var miner in found)
            {
                if (_host.Config.Devices.Any(d => string.Equals(d.Host.Trim(), miner.Address, StringComparison.OrdinalIgnoreCase))) continue;
                _host.Config.Devices.Add(new DeviceConfig { Name = miner.Info.Hostname ?? miner.Address, Host = miner.Address });
                added++;
            }
            if (added > 0)
            {
                _host.Config.Save();
                Rebuild();
            }
            StatusText = found.Count == 0
                ? L.T("Keine Miner gefunden. Tipp: IP-Adresse manuell eingeben.")
                : L.T("{0} Miner gefunden, {1} neu hinzugefügt.", found.Count, added);
        }
        catch (Exception ex)
        {
            StatusText = L.T("Suche fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(AnyRunning))]
    private async Task StopAll()
    {
        StatusText = L.T("Stoppe alle Benchmarks und stelle Einstellungen wieder her …");
        await Task.WhenAll(_devices.Values.Select(d => d.StopAndWaitAsync()));
        StatusText = L.T("Alle Benchmarks gestoppt.");
    }

    public Task StopAllAndWaitAsync() => Task.WhenAll(_devices.Values.Select(d => d.StopAndWaitAsync()));

    [RelayCommand]
    private void OpenDataDirectory()
    {
        System.IO.Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (UpdateUrl is not null)
            Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenProjectPage() =>
        Process.Start(new ProcessStartInfo(UpdateChecker.ProjectUrl) { UseShellExecute = true });

    partial void OnSelectedNavChanged(NavItem? value) => RemoveDeviceCommand.NotifyCanExecuteChanged();
}
