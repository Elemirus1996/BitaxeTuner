using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.ViewModels;

/// <summary>
/// Ansicht eines Miners (Tabs Live, Benchmark, Ergebnisse, Vorher/Nachher, Automatik, Protokoll).
/// Logik und Zustand liegen im <see cref="HubDevice"/> bzw. <see cref="MinerHub"/>; hier bleiben Anzeige,
/// Eingaben und die Bestätigungsdialoge (alter → neuer Wert).
/// </summary>
public sealed partial class DeviceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 240;

    private readonly AppHost _host;
    private readonly HubDevice _device;
    private MinerHub _hub => _host.Hub;
    private bool _refreshing;
    private bool _assigningProfile;

    public DeviceViewModel(HubDevice device, AppHost host)
    {
        _device = device;
        _host = host;
        Settings = new BenchmarkSettings();
        AvailableProfiles = _hub.ProfilesFor(device);
        foreach (var line in device.LogLines) Log.Add(line);
        Logs = new LogViewModel(device.Connection);

        _host.TuningApplied += OnTuningApplied;
        _host.LogAlerts.Triggered += OnLogAlert;
        _device.LogAdded += OnLogAdded;
        _hub.DeviceChanged += OnDeviceChanged;
        _hub.SoakFinished += OnSoakFinished;
        _hub.Benchmarks.Progress += OnBenchmarkProgress;
        _hub.Benchmarks.StateChanged += OnBenchmarkStateChanged;
    }

    /// <summary>Laufzeitzustand im Hub.</summary>
    public HubDevice Device => _device;

    private void OnLogAlert(string host, LogLine line, string rule)
    {
        if (!string.Equals(host, Address, StringComparison.OrdinalIgnoreCase)) return;
        AddLog(L.T("Log-Alarm ({0}): {1} {2}", rule, line.Tag, line.Message));
    }

    private void OnLogAdded(string line) => Ui(() =>
    {
        Log.Add(line);
        while (Log.Count > HubDevice.MaxLogLines) Log.RemoveAt(0);
    });

    /// <summary>Miner-Logs (Tab "Miner-Logs").</summary>
    public LogViewModel Logs { get; }

    private void OnTuningApplied(TuningEvent e)
    {
        if (string.Equals(e.Host, Address, StringComparison.OrdinalIgnoreCase)) Logs.AddTuningMarker(e);
    }

    /// <summary>Gerät aus der gemeinsamen Geräteliste (config.json).</summary>
    public DeviceConfig Config => _device.Config;
    public IMinerClient Client => _device.Connection;
    public string Address => _device.Host;
    public bool IsSimulated => _device.IsSimulated;
    /// <summary>Alle Profile – das automatisch erkannte (evtl. angepasste) Profil ersetzt seinen Registry-Eintrag.</summary>
    [ObservableProperty] private IReadOnlyList<DeviceProfile> _availableProfiles = [];

    public ObservableCollection<double> HashHistory { get; } = [];
    public ObservableCollection<double> TempHistory { get; } = [];
    public ObservableCollection<StepResult> Results { get; } = [];
    public ObservableCollection<StepResult> RankedResults { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(EfficiencyText))]
    private MinerInfo? _info;

    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _status = L.T("Verbinde …");
    [ObservableProperty] private BenchmarkSettings _settings;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProfileNotes))]
    private DeviceProfile? _profile;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartBenchmarkCommand), nameof(StopBenchmarkCommand), nameof(TogglePauseCommand),
        nameof(ResumeBenchmarkCommand), nameof(ApplyBestCommand), nameof(ApplyResultCommand), nameof(ApplyManualCommand),
        nameof(RestoreSettingsCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _phaseText = L.T("Bereit");
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private double _phaseProgress;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _etaText = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ResumeBenchmarkCommand))]
    private BenchmarkSession? _session;

    [ObservableProperty] private RankingMode _selectedRanking = RankingMode.Balanced;
    [ObservableProperty] private double _balancedWeight = 0.5;
    [ObservableProperty] private string _heatmapMetric = "hashrate";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyBestCommand))]
    [NotifyPropertyChangedFor(nameof(BestSummary))]
    private StepResult? _bestResult;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyResultCommand))]
    private StepResult? _selectedResult;

    /// <summary>Manuelles Einstellen (Tab Live).</summary>
    [ObservableProperty] private string _manualFrequency = "";
    [ObservableProperty] private string _manualVoltage = "";

    public ObservableCollection<TuningComparisonRow> Comparisons { get; } = [];

    public bool IsIdle => !IsRunning;
    public string Title => _device.Title;
    public string Subtitle => Info is null ? Address
        : $"{Address} · {Info.DeviceModel ?? Info.AsicModel} · {MinerHub.FirmwareName(Info.Firmware)} {Info.FirmwareVersion}";
    public string? ProfileNotes => Profile?.Notes;
    public string EfficiencyText => Info?.EfficiencyJth is { } e ? $"{e:F2}" : "–";

    public string BestSummary => BestResult is { } b
        ? L.T("{0} MHz / {1} mV → {2:F1} GH/s · {3:F1} W · {4:F2} J/TH · max. {5:F1} °C", b.FrequencyMhz, b.CoreVoltageMv, b.AvgHashRateGh, b.AvgPowerW, b.EfficiencyJth, b.MaxChipTempC)
        : L.T("Noch keine stabilen Ergebnisse.");

    public string EstimatedDurationText => BenchmarkManager.EstimatedDurationText(Settings);

    public void Initialize()
    {
        SetProfile(_device.Profile);

        var last = _hub.Benchmarks.LatestSession(_device);
        if (last is not null)
        {
            Session = last;
            SetResults(last.Results);
        }
        if (_device.Benchmark is { } run) ApplyRunState(run);
        AutomationStatus = AutomationText(_device.AutomationStatus);
        SoakStatus = _device.SoakStatus;
        RefreshComparisons();
        LoadAutomation();
    }

    private DeviceConfig? _loadedConfig;

    /// <summary>Nach jeder zentralen Abfragerunde (UI-Thread).</summary>
    public void OnPolled()
    {
        if (!ReferenceEquals(_loadedConfig, Config)) LoadAutomation(); // nach "Einstellungen speichern" neue Kopie
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EstimatedDurationText));
        AutomationStatus = AutomationText(_device.AutomationStatus);
        SoakStatus = _device.SoakStatus;

        var state = _device.State;
        if (state.Online && state.Normalized is { } info)
        {
            IsOnline = true;
            // Während eines Benchmarks liefert die Engine die Live-Werte (gleiche Verbindung, kein Doppelabruf)
            if (!IsRunning) UpdateLive(info);
        }
        else
        {
            IsOnline = false;
            Status = _device.Connection.InMaintenance ? L.T("Neustart/Tuning …") : state.Error ?? L.T("Offline");
        }
    }

    /// <summary>Profil erkannt/gewählt, Automatik- oder Dauertest-Status im Hub geändert.</summary>
    private void OnDeviceChanged(HubDevice device)
    {
        if (!ReferenceEquals(device, _device)) return;
        Ui(() =>
        {
            AutomationStatus = AutomationText(device.AutomationStatus);
            SoakStatus = device.SoakStatus;
            OnPropertyChanged(nameof(SoakActive));
            if (!ReferenceEquals(Profile, device.Profile))
            {
                AvailableProfiles = _hub.ProfilesFor(device);
                SetProfile(device.Profile);
            }
        });
    }

    private void SetProfile(DeviceProfile profile)
    {
        _assigningProfile = true;
        try { Profile = profile; }
        finally { _assigningProfile = false; }
    }

    partial void OnProfileChanged(DeviceProfile? value)
    {
        if (value is null) return;
        // Vom Benutzer gewählt: dauerhaft in der gemeinsamen Geräteliste merken
        if (!_assigningProfile && !IsRunning) _hub.SetProfile(_device, value);
        if (IsRunning) return;
        Settings = BenchmarkSettings.FromProfile(value);
        if (Info is { FrequencyMhz: > 0 } i && i.FrequencyMhz >= value.MinFrequencyMhz && i.FrequencyMhz < value.MaxFrequencyMhz)
        {
            // Mit den aktuellen Werten des Geräts starten, wenn sie im Profilbereich liegen.
            Settings.StartFrequencyMhz = Math.Min(i.FrequencyMhz, value.DefaultFrequencyMhz);
        }
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(EstimatedDurationText));
    }

    partial void OnSelectedRankingChanged(RankingMode value) => UpdateRanking();
    partial void OnBalancedWeightChanged(double value) => UpdateRanking();

    [RelayCommand]
    private void ResetToProfile()
    {
        if (Profile is null) return;
        Settings = BenchmarkSettings.FromProfile(Profile);
        OnPropertyChanged(nameof(EstimatedDurationText));
    }

    /// <summary>Sofort abfragen ("Aktualisieren") – über die gemeinsame Verbindung, nie parallel zum Polling.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(EstimatedDurationText));
        if (_refreshing || IsRunning) return;
        _refreshing = true;
        try
        {
            var info = await Client.GetInfoAsync();
            UpdateLive(info);
            IsOnline = true;
        }
        catch (MinerApiException ex)
        {
            IsOnline = false;
            Status = L.T("Offline");
            AddLog(ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---------- Benchmark (läuft im Hub) ----------

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartBenchmark() => RunBenchmarkAsync(resume: false);

    private bool CanResume() => !IsRunning && Session is { IsFinished: false, Results.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeBenchmark() => RunBenchmarkAsync(resume: true);

    private async Task RunBenchmarkAsync(bool resume)
    {
        if (Profile is null) return;
        BenchmarkPlan plan;
        try
        {
            plan = await _hub.Benchmarks.PrepareAsync(_device, Settings, resume);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, L.T("Benchmark"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Einmalige Bestätigung für den ganzen Lauf (jeder einzelne Schritt wird in history.db protokolliert)
        if (MessageBox.Show(plan.ConfirmText, L.T("Benchmark"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (!resume) SetResults([]);

        try
        {
            await _hub.Benchmarks.RunAsync(_device, plan);
            if (_device.Benchmark?.Completed == true) Status = L.T("Benchmark fertig");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, L.T("Benchmark"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBenchmarkStateChanged(HubDevice device)
    {
        if (!ReferenceEquals(device, _device) || device.Benchmark is not { } run) return;
        Ui(() =>
        {
            var finished = IsRunning && !run.IsRunning;
            ApplyRunState(run);
            if (finished)
            {
                OnPropertyChanged(nameof(Session));
                ResumeBenchmarkCommand.NotifyCanExecuteChanged();
                RefreshComparisons();
            }
        });
    }

    private void ApplyRunState(BenchmarkRun run)
    {
        IsRunning = run.IsRunning;
        IsPaused = run.IsPaused;
        Session = run.Session;
        PhaseText = run.PhaseText;
        StepText = run.StepText;
        EtaText = run.EtaText;
        PhaseProgress = run.PhaseProgress;
        OverallProgress = run.OverallProgress;
    }

    private void OnBenchmarkProgress(HubDevice device, BenchmarkProgress p)
    {
        if (!ReferenceEquals(device, _device) || device.Benchmark is not { } run) return;
        Ui(() =>
        {
            if (p.Info is not null)
            {
                UpdateLive(p.Info);
                IsOnline = true;
            }
            if (p.CompletedStep is not null)
            {
                Results.Add(p.CompletedStep);
                UpdateRanking();
                OnPropertyChanged(nameof(Results));
            }
            ApplyRunState(run);
        });
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopBenchmark()
    {
        _hub.Benchmarks.Stop(_device);
        if (_device.Benchmark is { } run) PhaseText = run.PhaseText;
    }

    /// <summary>Bricht einen laufenden Benchmark ab und wartet, bis die Einstellungen wiederhergestellt sind.</summary>
    public Task StopAndWaitAsync() => _hub.Benchmarks.StopAndWaitAsync(_device);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void TogglePause()
    {
        _hub.Benchmarks.TogglePause(_device);
        if (_device.Benchmark is { } run) ApplyRunState(run);
    }

    // ---------- Einstellung anwenden ----------

    private bool CanApplyBest() => !IsRunning && BestResult is not null;

    [RelayCommand(CanExecute = nameof(CanApplyBest))]
    private Task ApplyBest() => ApplyAsync(BestResult!);

    private bool CanApplyResult() => !IsRunning && SelectedResult is { IsStable: true };

    [RelayCommand(CanExecute = nameof(CanApplyResult))]
    private Task ApplyResult() => ApplyAsync(SelectedResult!);

    private Task ApplyAsync(StepResult r) => ApplyValuesAsync(r.FrequencyMhz, r.CoreVoltageMv);

    private bool CanApplyManual() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplyManual))]
    private Task ApplyManual()
    {
        if (!int.TryParse(ManualFrequency.Trim(), out var f) || !int.TryParse(ManualVoltage.Trim(), out var mv))
        {
            MessageBox.Show(L.T("Bitte Frequenz (MHz) und Kernspannung (mV) als ganze Zahlen eingeben."), L.T("Manuell einstellen"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
        return ApplyValuesAsync(f, mv);
    }

    /// <summary>
    /// Frequenz/Spannung setzen – nur nach ausdrücklicher Bestätigung mit aktuellem und neuem Wert.
    /// Werte außerhalb der Profilgrenzen des ASIC-Modells lehnt der Hub ab.
    /// </summary>
    private async Task ApplyValuesAsync(int frequencyMhz, int coreVoltageMv, string? intro = null)
    {
        ChangePreview preview;
        try
        {
            preview = await _hub.PreviewChangeAsync(_device, frequencyMhz, coreVoltageMv, intro);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, L.T("Grenzwerte"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(preview.ConfirmText, L.T("Einstellung anwenden"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            await _hub.ApplyChangeAsync(_device, frequencyMhz, coreVoltageMv);
            RefreshComparisons();
        }
        catch (Exception ex) when (ex is MinerApiException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, L.T("Fehler"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Automatik: Voreinstellungen, Regeln ----------

    [ObservableProperty] private string _automationStatus = L.T("keine Automatik");

    private static string AutomationText(string status) => status.Length == 0 ? L.T("keine Automatik") : status;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private TuningPreset? _selectedPreset;
    [ObservableProperty] private ScheduleEntry? _selectedScheduleEntry;

    public ObservableCollection<TuningPreset> Presets { get; } = [];
    public ObservableCollection<ScheduleEntry> ScheduleEntries { get; } = [];
    public ThermalGuardRule ThermalGuard => Config.ThermalGuard;
    public PresetScheduleRule Schedule => Config.Schedule;
    public IReadOnlyList<Option<string>> ScheduleModes { get; } =
        [new("time", "Zeitplan (Wochentage/Uhrzeit)"), new("price", "Strompreis (Schwelle)")];

    public bool ScheduleIsPrice
    {
        get => Schedule.Mode == "price";
        set { Schedule.Mode = value ? "price" : "time"; OnPropertyChanged(); OnPropertyChanged(nameof(ScheduleIsTime)); }
    }
    public bool ScheduleIsTime
    {
        get => !ScheduleIsPrice;
        set => ScheduleIsPrice = !value;
    }

    private void LoadAutomation()
    {
        _loadedConfig = Config;
        Presets.Clear();
        foreach (var p in Config.Presets) Presets.Add(p);
        ScheduleEntries.Clear();
        foreach (var e in Config.Schedule.Entries) ScheduleEntries.Add(e);
        OnPropertyChanged(nameof(ThermalGuard));
        OnPropertyChanged(nameof(Schedule));
        OnPropertyChanged(nameof(ScheduleIsPrice));
        OnPropertyChanged(nameof(ScheduleIsTime));
        OnPropertyChanged(nameof(SoakActive));
    }

    private void StoreAutomation()
    {
        Config.Presets = Presets.ToList();
        Config.Schedule.Entries = ScheduleEntries.ToList();
        _host.Config.Save();
    }

    [RelayCommand]
    private void AddPresetFromCurrent()
    {
        if (Info is null) { AddLog(L.T("Kein aktueller Wert – Miner nicht erreichbar.")); return; }
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? L.T("{0} MHz", Info.FrequencyMhz) : NewPresetName.Trim();
        UpsertPreset(new TuningPreset(name, Info.FrequencyMhz, Info.CoreVoltageMv));
        NewPresetName = "";
    }

    /// <summary>"Hashrate" und "Effizienz" aus den besten stabilen Benchmark-Ergebnissen.</summary>
    [RelayCommand]
    private void AddPresetsFromBenchmark()
    {
        var hash = ResultRanking.Best(Results, RankingMode.MaxHashrate);
        var eff = ResultRanking.Best(Results, RankingMode.Efficiency);
        if (hash is null || eff is null)
        {
            MessageBox.Show(L.T("Es gibt noch keine stabilen Benchmark-Ergebnisse für dieses Gerät."), L.T("Voreinstellungen"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UpsertPreset(new TuningPreset(L.T("Hashrate"), hash.FrequencyMhz, hash.CoreVoltageMv));
        UpsertPreset(new TuningPreset(L.T("Effizienz"), eff.FrequencyMhz, eff.CoreVoltageMv));
    }

    private void UpsertPreset(TuningPreset preset)
    {
        if (MinerHub.CheckPreset(_device, preset) is { } error)
        {
            MessageBox.Show(error, L.T("Voreinstellungen"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var existing = Presets.FirstOrDefault(x => string.Equals(x.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Presets[Presets.IndexOf(existing)] = preset;
        else Presets.Add(preset);
        StoreAutomation();
        AddLog(L.T("Voreinstellung gespeichert: {0}", preset));
    }

    [RelayCommand]
    private void RemovePreset()
    {
        if (SelectedPreset is null) return;
        Presets.Remove(SelectedPreset);
        StoreAutomation();
    }

    [RelayCommand]
    private void AddScheduleEntry()
    {
        ScheduleEntries.Add(new ScheduleEntry { Days = 127, FromHour = 22, ToHour = 6, Preset = Presets.FirstOrDefault()?.Name ?? "" });
    }

    [RelayCommand]
    private void RemoveScheduleEntry()
    {
        if (SelectedScheduleEntry is not null) ScheduleEntries.Remove(SelectedScheduleEntry);
    }

    /// <summary>Regeln speichern. Geänderte Regeln verlieren dabei ihre Freigabe (Prüfsumme passt nicht mehr).</summary>
    [RelayCommand]
    private void SaveAutomation()
    {
        StoreAutomation();
        AddLog(L.T("Automatik-Einstellungen gespeichert") +
               (ThermalGuard.Enabled && !ThermalGuard.IsApproved(Address) || Schedule.Enabled && !Schedule.IsApproved(Address)
                   ? L.T(" – Freigabe erforderlich.") : "."));
    }

    [RelayCommand]
    private void ApproveThermalGuard()
    {
        StoreAutomation();
        Approve(ThermalGuard, MinerHub.ThermalGuardApprovalText(_device), L.T("Temperaturschutz"));
    }

    [RelayCommand]
    private void ApproveSchedule()
    {
        StoreAutomation();
        Approve(Schedule, _hub.ScheduleApprovalText(_device), L.T("Zeitplan"));
    }

    private void Approve(AutomationRule rule, string text, string label)
    {
        if (!rule.Enabled)
        {
            MessageBox.Show(L.T("{0} ist nicht eingeschaltet.", label), label, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(text, label + " freigeben", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _hub.ApproveRule(_device, rule, label);
    }

    // ---------- Dauertest ----------

    [ObservableProperty] private string _soakStatus = "";
    [ObservableProperty] private int _soakHours = 24;
    public IReadOnlyList<int> SoakHourOptions { get; } = [6, 12, 24, 48];
    public bool SoakActive => _device.SoakActive;

    [RelayCommand]
    private void StartSoak()
    {
        if (Info is null) { AddLog(L.T("Dauertest: Miner nicht erreichbar.")); return; }
        if (IsRunning) { AddLog(L.T("Dauertest: zuerst den Benchmark beenden.")); return; }
        string text;
        try { text = _hub.SoakConfirmText(_device, SoakHours); }
        catch (InvalidOperationException ex) { AddLog(ex.Message); return; }
        if (MessageBox.Show(text, L.T("Dauertest"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try { _hub.StartSoak(_device, SoakHours); }
        catch (InvalidOperationException ex) { AddLog(ex.Message); }
    }

    [RelayCommand]
    private void StopSoak() => _hub.StopSoak(_device);

    /// <summary>Dauertest im Hub beendet: bei Fehlschlag den Vorschlag (nur nach Bestätigung) anbieten.</summary>
    private void OnSoakFinished(HubDevice device, SoakResult result, SoakSuggestion? suggestion)
    {
        if (!ReferenceEquals(device, _device)) return;
        Ui(async () =>
        {
            SoakStatus = result.Message;
            OnPropertyChanged(nameof(SoakActive));
            if (suggestion is null) return;
            await ApplyValuesAsync(suggestion.FrequencyMhz, suggestion.CoreVoltageMv, suggestion.Reason);
        });
    }

    // ---------- Einstellungen sichern / wiederherstellen ----------

    /// <summary>Aktuelle Einstellungen des Miners sichern (&lt;Datenordner&gt;\snapshots).</summary>
    [RelayCommand]
    private async Task BackupSettings()
    {
        try
        {
            var snap = await _hub.BackupSettingsAsync(_device);
            MessageBox.Show(L.T("Einstellungen von {0} gesichert:\n{1}\n\n{2}\n\n", Title, snap.DisplayText, snap.FilePath) +
                            L.T("Hinweis: Die Datei enthält auch Pool-Benutzer (Wallet-Adresse). Pool-Passwörter liefert AxeOS nicht aus."),
                L.T("Einstellungen sichern"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is MinerApiException or System.Text.Json.JsonException or IOException)
        {
            MessageBox.Show(L.T("Sichern fehlgeschlagen: ") + ex.Message, L.T("Einstellungen sichern"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanRestore() => !IsRunning;

    /// <summary>Gesicherte Einstellungen zurückspielen – nur ausgewählte, geänderte Felder, nach Bestätigung.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreSettings()
    {
        IReadOnlyList<SettingsSnapshot> snapshots;
        string current;
        try
        {
            (snapshots, current) = await _hub.RestoreCandidatesAsync(_device);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, L.T("Wiederherstellen"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (MinerApiException ex)
        {
            MessageBox.Show(ex.Message, L.T("Wiederherstellen"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new Views.RestoreWindow(Title, snapshots.ToList(), snap => SettingsSnapshots.Diff(snap, current), Profile)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.Selected.Count == 0) return;

        try
        {
            await _hub.RestoreAsync(_device, dialog.Snapshot!, dialog.Selected);
            RefreshComparisons();
        }
        catch (Exception ex) when (ex is MinerApiException or InvalidOperationException)
        {
            MessageBox.Show(L.T("Wiederherstellen fehlgeschlagen: ") + ex.Message, L.T("Wiederherstellen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Vorher/Nachher ----------

    /// <summary>Je Tuning-Änderung Ø-Werte 60 min davor und danach (aus history.db, berechnet im Hub).</summary>
    [RelayCommand]
    private void RefreshComparisons()
    {
        Comparisons.Clear();
        try
        {
            foreach (var row in _hub.Comparisons(_device)) Comparisons.Add(row);
        }
        catch (Exception ex)
        {
            AddLog(L.T("Vorher/Nachher nicht verfügbar: ") + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Session is null || Session.Results.Count == 0)
        {
            MessageBox.Show(L.T("Es gibt noch keine Ergebnisse zum Exportieren."), L.T("Export"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = L.T("CSV-Datei (*.csv)|*.csv"),
            FileName = $"{Session.Hostname ?? "bitaxe"}_{Session.StartedAt:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            ResultStore.ExportCsv(Session, dlg.FileName);
            AddLog(L.T("Exportiert: {0}", dlg.FileName));
        }
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (IsSimulated) return;
        var url = Address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Address : $"http://{Address}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void UpdateLive(MinerInfo info)
    {
        Info = info;
        // Eingabefelder für "Manuell einstellen" mit den aktuellen Werten vorbelegen
        if (string.IsNullOrEmpty(ManualFrequency) && info.FrequencyMhz > 0) ManualFrequency = info.FrequencyMhz.ToString();
        if (string.IsNullOrEmpty(ManualVoltage) && info.CoreVoltageMv > 0) ManualVoltage = info.CoreVoltageMv.ToString();
        Push(HashHistory, info.HashRateGh);
        if (info.MaxChipTempC is { } t) Push(TempHistory, t);
        Status = IsRunning ? L.T("Benchmark · {0:F0} GH/s", info.HashRateGh) : L.T("{0:F0} GH/s · {1:F0} °C", info.HashRateGh, info.MaxChipTempC);
    }

    private static void Push(ObservableCollection<double> list, double value)
    {
        list.Add(value);
        while (list.Count > HistoryLength) list.RemoveAt(0);
    }

    private void SetResults(IEnumerable<StepResult> results)
    {
        Results.Clear();
        foreach (var r in results) Results.Add(r);
        UpdateRanking();
        OnPropertyChanged(nameof(Results));
    }

    private void UpdateRanking()
    {
        var ranked = ResultRanking.Rank(Results, SelectedRanking, BalancedWeight);
        RankedResults.Clear();
        foreach (var r in ranked) RankedResults.Add(r);
        BestResult = ranked.FirstOrDefault();
    }

    /// <summary>Zeile ins Geräteprotokoll des Hubs (erscheint über LogAdded auch hier).</summary>
    public void AddLog(string message) => _device.AddLog(message);

    public static string FormatDuration(TimeSpan t) => BenchmarkManager.FormatDuration(t);

    /// <summary>Hub-Ereignisse können von Hintergrund-Threads kommen (Benchmark-Protokoll, Log-Alarme).</summary>
    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        // Verbindung und Benchmark gehören dem Hub; hier nur die Anzeige abmelden
        _host.TuningApplied -= OnTuningApplied;
        _host.LogAlerts.Triggered -= OnLogAlert;
        _device.LogAdded -= OnLogAdded;
        _hub.DeviceChanged -= OnDeviceChanged;
        _hub.SoakFinished -= OnSoakFinished;
        _hub.Benchmarks.Progress -= OnBenchmarkProgress;
        _hub.Benchmarks.StateChanged -= OnBenchmarkStateChanged;
        Logs.Dispose();
    }
}
