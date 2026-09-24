using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BitaxeTuner.App.ViewModels;

public sealed partial class DeviceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 240;
    private const int MaxLogLines = 2000;
    private const int SimulationSpeedup = 30;

    private readonly ProfileRegistry _registry;
    private readonly ResultStore _store;
    private readonly DispatcherTimer _pollTimer;
    private CancellationTokenSource? _cts;
    private BenchmarkEngine? _engine;
    private Task? _runTask;
    private bool _polling;

    public DeviceViewModel(IMinerClient client, ProfileRegistry registry, ResultStore store)
    {
        Client = client;
        _registry = registry;
        _store = store;
        Settings = new BenchmarkSettings();
        AvailableProfiles = registry.Profiles;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public IMinerClient Client { get; }
    public string Address => Client.Address;
    public bool IsSimulated => Client is SimulatedMinerClient;
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
    [ObservableProperty] private string _status = "Verbinde …";
    [ObservableProperty] private BenchmarkSettings _settings;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProfileNotes))]
    private DeviceProfile? _profile;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartBenchmarkCommand), nameof(StopBenchmarkCommand), nameof(TogglePauseCommand),
        nameof(ResumeBenchmarkCommand), nameof(ApplyBestCommand), nameof(ApplyResultCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _phaseText = "Bereit";
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

    public bool IsIdle => !IsRunning;
    public string Title => Info?.DisplayName ?? Address;
    public string Subtitle => Info is null ? Address
        : $"{Address} · {Info.DeviceModel ?? Info.AsicModel} · {FirmwareName(Info.Firmware)} {Info.FirmwareVersion}";
    public string? ProfileNotes => Profile?.Notes;
    public string EfficiencyText => Info?.EfficiencyJth is { } e ? $"{e:F2}" : "–";

    public string BestSummary => BestResult is { } b
        ? $"{b.FrequencyMhz} MHz / {b.CoreVoltageMv} mV → {b.AvgHashRateGh:F1} GH/s · {b.AvgPowerW:F1} W · {b.EfficiencyJth:F2} J/TH · max. {b.MaxChipTempC:F1} °C"
        : "Noch keine stabilen Ergebnisse.";

    public string EstimatedDurationText
    {
        get
        {
            var total = TimeSpan.FromTicks(Settings.EstimatedStepDuration.Ticks * Settings.EstimatedSteps);
            return $"≈ {Settings.EstimatedSteps} Schritte à {Settings.EstimatedStepDuration.TotalMinutes:F0} min – bis zu {FormatDuration(total)}";
        }
    }

    public async Task InitializeAsync()
    {
        AddLog($"Gerät hinzugefügt: {Address}");
        await RefreshAsync();
        if (Info is not null)
        {
            AsicInfo? asic = null;
            try { asic = await Client.GetAsicInfoAsync(); } catch (MinerApiException) { }
            var matched = _registry.Match(Info, asic);
            AvailableProfiles = _registry.Profiles.Select(p => p.Id == matched.Id ? matched : p).ToList();
            Profile = matched;
            AddLog($"Erkannt: {Info.DeviceModel ?? Info.AsicModel} ({FirmwareName(Info.Firmware)}) → Profil „{Profile.Name}“");
        }
        else
        {
            Profile = AvailableProfiles.First(p => p.Id == _registry.Generic.Id);
        }

        var last = _store.LoadLatest(Address, Info?.Hostname);
        if (last is not null)
        {
            Session = last;
            SetResults(last.Results);
            AddLog($"Letzter Lauf vom {last.StartedAt:g} geladen ({last.Results.Count} Ergebnisse" +
                   (last.IsFinished ? ")." : ", nicht abgeschlossen – kann fortgesetzt werden)."));
        }
        _pollTimer.Start();
    }

    partial void OnProfileChanged(DeviceProfile? value)
    {
        if (value is null || IsRunning) return;
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

    [RelayCommand]
    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(EstimatedDurationText));
        if (_polling || IsRunning) return;
        _polling = true;
        try
        {
            var info = await Client.GetInfoAsync();
            UpdateLive(info);
            IsOnline = true;
            Status = $"{info.HashRateGh:F0} GH/s · {info.MaxChipTempC:F0} °C";
        }
        catch (MinerApiException ex)
        {
            IsOnline = false;
            Status = "Offline";
            if (Info is null) AddLog(ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartBenchmark() => RunBenchmarkAsync(resume: false);

    private bool CanResume() => !IsRunning && Session is { IsFinished: false, Results.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeBenchmark() => RunBenchmarkAsync(resume: true);

    private async Task RunBenchmarkAsync(bool resume)
    {
        if (Profile is null) return;
        var settings = resume && Session is not null ? Session.Settings : Settings.Clone();
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, errors), "Ungültige Einstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!resume)
        {
            var msg = $"Benchmark für {Title} starten?\n\n" +
                      $"Frequenz: {settings.StartFrequencyMhz} → {settings.MaxFrequencyMhz} MHz (Schritt {settings.FrequencyStepMhz})\n" +
                      $"Spannung: {settings.StartVoltageMv} → {settings.MaxVoltageMv} mV (Schritt {settings.VoltageStepMv})\n" +
                      $"Grenzen: Chip {settings.MaxChipTempC} °C · VR {settings.MaxVrTempC} °C · {settings.MaxPowerW} W\n" +
                      $"{EstimatedDurationText}\n\n" +
                      "Übertakten geschieht auf eigenes Risiko. Stelle sicher, dass Netzteil und Kühlung ausreichen.";
            if (MessageBox.Show(msg, "Benchmark starten", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            Session = new BenchmarkSession
            {
                DeviceAddress = Address,
                Hostname = Info?.Hostname,
                DeviceModel = Info?.DeviceModel ?? Profile.Name,
                ProfileId = Profile.Id,
                Settings = settings,
            };
            SetResults([]);
        }

        var session = Session!;
        _pollTimer.Stop();
        IsRunning = true;
        IsPaused = false;
        _cts = new CancellationTokenSource();
        _engine = new BenchmarkEngine(Client, Profile)
        {
            // Simulation läuft 30× schneller, damit ein kompletter Lauf in wenigen Minuten sichtbar ist.
            Delay = IsSimulated ? (t, ct) => Task.Delay(t / SimulationSpeedup, ct) : Task.Delay,
            Progress = new Progress<BenchmarkProgress>(OnProgress),
            Log = msg => Application.Current.Dispatcher.BeginInvoke(() => AddLog(msg)),
            StepCompleted = s => _store.SaveAsync(s),
        };

        var started = DateTime.Now;
        var run = _engine.RunAsync(session, _cts.Token);
        _runTask = run;
        try
        {
            await run;
            PhaseText = "Fertig";
            Status = "Benchmark fertig";
        }
        catch (OperationCanceledException)
        {
            PhaseText = "Abgebrochen";
        }
        catch (Exception ex)
        {
            PhaseText = "Fehler";
            AddLog($"Fehler: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _engine = null;
            _cts.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(Session));
            ResumeBenchmarkCommand.NotifyCanExecuteChanged();
            StepText = session.FinishReason ?? "";
            EtaText = $"Dauer: {FormatDuration(DateTime.Now - started)}";
            _pollTimer.Start();
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopBenchmark()
    {
        PhaseText = "Stoppe – stelle Einstellungen wieder her …";
        _cts?.Cancel();
    }

    /// <summary>Bricht einen laufenden Benchmark ab und wartet, bis die Einstellungen wiederhergestellt sind.</summary>
    public async Task StopAndWaitAsync()
    {
        if (!IsRunning || _runTask is null) return;
        _cts?.Cancel();
        try { await _runTask; } catch { /* Abbruch erwartet */ }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void TogglePause()
    {
        if (_engine is null) return;
        _engine.IsPaused = !_engine.IsPaused;
        IsPaused = _engine.IsPaused;
        PhaseText = IsPaused ? "Pausiert" : PhaseText;
    }

    private bool CanApplyBest() => !IsRunning && BestResult is not null;

    [RelayCommand(CanExecute = nameof(CanApplyBest))]
    private Task ApplyBest() => ApplyAsync(BestResult!);

    private bool CanApplyResult() => !IsRunning && SelectedResult is { IsStable: true };

    [RelayCommand(CanExecute = nameof(CanApplyResult))]
    private Task ApplyResult() => ApplyAsync(SelectedResult!);

    private async Task ApplyAsync(StepResult r)
    {
        if (MessageBox.Show($"{r.FrequencyMhz} MHz / {r.CoreVoltageMv} mV auf {Title} anwenden?" +
                            (Settings.RestartAfterApply ? "\nDas Gerät wird dazu neu gestartet." : ""),
                "Einstellung anwenden", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            await Client.ApplySettingsAsync(r.FrequencyMhz, r.CoreVoltageMv);
            if (Settings.RestartAfterApply) await Client.RestartAsync();
            AddLog($"Angewendet: {r.FrequencyMhz} MHz / {r.CoreVoltageMv} mV");
        }
        catch (MinerApiException ex)
        {
            MessageBox.Show(ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Session is null || Session.Results.Count == 0)
        {
            MessageBox.Show("Es gibt noch keine Ergebnisse zum Exportieren.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"{Session.Hostname ?? "bitaxe"}_{Session.StartedAt:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            ResultStore.ExportCsv(Session, dlg.FileName);
            AddLog($"Exportiert: {dlg.FileName}");
        }
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        if (IsSimulated) return;
        var url = Address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Address : $"http://{Address}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnProgress(BenchmarkProgress p)
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

        PhaseText = p.Phase switch
        {
            BenchmarkPhase.Preparing => "Vorbereitung",
            BenchmarkPhase.Applying => "Einstellung wird gesetzt",
            BenchmarkPhase.Restarting => "Neustart – warte auf Gerät",
            BenchmarkPhase.WarmingUp => "Aufwärmen",
            BenchmarkPhase.Measuring => "Messung läuft",
            BenchmarkPhase.Restoring => "Stelle Einstellung wieder her",
            BenchmarkPhase.Finished => "Fertig",
            BenchmarkPhase.Cancelled => "Abgebrochen",
            BenchmarkPhase.Failed => "Fehler",
            _ => PhaseText,
        };
        if (IsPaused) PhaseText = "Pausiert";
        if (p.FrequencyMhz > 0)
            StepText = $"Schritt {p.StepIndex} von ca. {p.EstimatedSteps}: {p.FrequencyMhz} MHz / {p.CoreVoltageMv} mV";
        if (p.Message is not null && p.Phase is BenchmarkPhase.Restoring or BenchmarkPhase.Finished or BenchmarkPhase.Failed or BenchmarkPhase.Cancelled)
            StepText = p.Message;

        PhaseProgress = p.Phase is BenchmarkPhase.WarmingUp or BenchmarkPhase.Measuring ? p.PhaseProgress * 100 : PhaseProgress;
        if (p.EstimatedSteps > 0)
        {
            var within = p.Phase == BenchmarkPhase.WarmingUp ? p.PhaseProgress * 0.15 : p.Phase == BenchmarkPhase.Measuring ? 0.15 + p.PhaseProgress * 0.85 : 0;
            OverallProgress = Math.Min(100, (Math.Max(0, p.StepIndex - 1) + within) / p.EstimatedSteps * 100);
            if (Session is { } s && p.Phase is BenchmarkPhase.WarmingUp or BenchmarkPhase.Measuring)
            {
                var remainingSteps = Math.Max(0, p.EstimatedSteps - p.StepIndex) + (1 - within);
                var timeScale = IsSimulated ? SimulationSpeedup : 1;
                EtaText = $"Restzeit höchstens ≈ {FormatDuration(TimeSpan.FromTicks((long)(s.Settings.EstimatedStepDuration.Ticks * remainingSteps / timeScale)))}";
            }
        }
    }

    private void UpdateLive(MinerInfo info)
    {
        Info = info;
        Push(HashHistory, info.HashRateGh);
        if (info.MaxChipTempC is { } t) Push(TempHistory, t);
        Status = IsRunning ? $"Benchmark · {info.HashRateGh:F0} GH/s" : $"{info.HashRateGh:F0} GH/s · {info.MaxChipTempC:F0} °C";
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

    public void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private static string FirmwareName(FirmwareKind kind) => kind switch
    {
        FirmwareKind.AxeOS => "AxeOS",
        FirmwareKind.NerdQAxe => "NerdQAxe-Firmware",
        FirmwareKind.Simulated => "Simulation",
        _ => "Firmware",
    };

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min" : $"{Math.Max(0, (int)t.TotalMinutes)} min";

    public void Dispose()
    {
        _pollTimer.Stop();
        _cts?.Cancel();
        (Client as IDisposable)?.Dispose();
    }
}
