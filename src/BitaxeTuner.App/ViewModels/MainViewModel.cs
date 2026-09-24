using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Discovery;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BitaxeTuner.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private ProfileRegistry _registry;
    private ResultStore _store;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _registry = ProfileRegistry.Load(settings.EffectiveDataDirectory);
        _store = new ResultStore(settings.EffectiveDataDirectory);
        VersionText = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    [ObservableProperty] private DeviceViewModel? _selectedDevice;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))] private string _newAddress = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanNetworkCommand))] private bool _isScanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _statusText = "Bereit";
    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private string? _updateUrl;

    public string VersionText { get; }
    public string DataDirectory => _settings.EffectiveDataDirectory;
    public bool AnyRunning => Devices.Any(d => d.IsRunning);

    public async Task InitializeAsync()
    {
        foreach (var address in _settings.DeviceAddresses.ToList())
            await AddAsync(address, save: false);
        SelectedDevice ??= Devices.FirstOrDefault();

        if (_settings.CheckForUpdates)
        {
            var update = await UpdateChecker.CheckAsync(VersionText);
            if (update is not null)
            {
                UpdateText = $"Update verfügbar: {update.Value.Version}";
                UpdateUrl = update.Value.Url;
            }
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewAddress);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddDevice()
    {
        var address = NewAddress.Trim();
        NewAddress = "";
        await AddAsync(address, save: true);
    }

    private async Task<DeviceViewModel?> AddAsync(string address, bool save)
    {
        var client = MinerClientFactory.Create(address, _registry);
        if (Devices.Any(d => string.Equals(d.Address, client.Address, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedDevice = Devices.First(d => string.Equals(d.Address, client.Address, StringComparison.OrdinalIgnoreCase));
            return SelectedDevice;
        }

        var vm = new DeviceViewModel(client, _registry, _store);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(AnyRunning));
                StopAllCommand.NotifyCanExecuteChanged();
            }
        };
        Devices.Add(vm);
        SelectedDevice = vm;
        if (save) SaveDeviceList();
        await vm.InitializeAsync();
        return vm;
    }

    [RelayCommand]
    private void RemoveDevice(DeviceViewModel? device)
    {
        device ??= SelectedDevice;
        if (device is null) return;
        if (device.IsRunning)
        {
            MessageBox.Show("Bitte zuerst den laufenden Benchmark stoppen.", "Gerät entfernen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Devices.Remove(device);
        device.Dispose();
        SelectedDevice = Devices.FirstOrDefault();
        SaveDeviceList();
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanNetwork()
    {
        IsScanning = true;
        ScanProgress = 0;
        StatusText = "Durchsuche lokales Netzwerk …";
        try
        {
            var found = await NetworkScanner.ScanAsync(new Progress<double>(p => ScanProgress = p * 100));
            var added = 0;
            foreach (var miner in found)
            {
                if (Devices.Any(d => d.Address == miner.Address)) continue;
                await AddAsync(miner.Address, save: false);
                added++;
            }
            SaveDeviceList();
            StatusText = found.Count == 0
                ? "Keine Miner gefunden. Tipp: IP-Adresse manuell eingeben."
                : $"{found.Count} Miner gefunden, {added} neu hinzugefügt.";
        }
        catch (Exception ex)
        {
            StatusText = $"Suche fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(AnyRunning))]
    private async Task StopAll()
    {
        StatusText = "Stoppe alle Benchmarks und stelle Einstellungen wieder her …";
        await Task.WhenAll(Devices.Select(d => d.StopAndWaitAsync()));
        StatusText = "Alle Benchmarks gestoppt.";
    }

    [RelayCommand]
    private void ChooseDataDirectory()
    {
        if (AnyRunning)
        {
            MessageBox.Show("Bitte zuerst alle Benchmarks stoppen.", "Datenordner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Datenordner für Ergebnisse und Profile wählen", InitialDirectory = DataDirectory };
        if (dlg.ShowDialog() != true) return;

        _settings.DataDirectory = dlg.FolderName;
        _settings.Save();
        _registry = ProfileRegistry.Load(_settings.EffectiveDataDirectory);
        _store = new ResultStore(_settings.EffectiveDataDirectory);
        OnPropertyChanged(nameof(DataDirectory));
        StatusText = $"Datenordner: {DataDirectory} – Geräte werden neu geladen.";
        _ = ReloadDevicesAsync();
    }

    private async Task ReloadDevicesAsync()
    {
        var addresses = Devices.Select(d => d.Address).ToList();
        foreach (var d in Devices) d.Dispose();
        Devices.Clear();
        foreach (var a in addresses) await AddAsync(a, save: false);
        SelectedDevice = Devices.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        System.IO.Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void EditProfiles()
    {
        var path = ProfileRegistry.WriteUserTemplate(DataDirectory);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusText = "Profile bearbeiten und danach den Datenordner neu wählen oder das Programm neu starten.";
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

    private void SaveDeviceList()
    {
        _settings.DeviceAddresses = Devices.Select(d => d.Address).ToList();
        _settings.Save();
    }
}
