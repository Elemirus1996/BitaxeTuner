using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BitaxeTuner.App.ViewModels;

/// <summary>Vorher/Nachher-Vergleich einer Tuning-Änderung aus history.db.</summary>
public sealed record TuningComparisonRow(TuningEvent Event, WindowAverage? Before, WindowAverage? After)
{
    public DateTime Time => Event.Time;
    public string Source => Event.SourceText;
    public string Change => Event.ChangeText;
    public string BeforeText => Format(Before);
    public string AfterText => Format(After);
    public string DeltaText => Before is null || After is null ? "–"
        : $"{After.HashRateGh - Before.HashRateGh:+0;-0;0} GH/s · {After.Temp - Before.Temp:+0.0;-0.0;0.0} °C · " +
          (Before.EfficiencyJth is { } b && After.EfficiencyJth is { } a ? $"{a - b:+0.00;-0.00;0.00} J/TH" : "–");

    private static string Format(WindowAverage? w) => w is null ? "keine Daten"
        : $"{w.HashRateGh:0} GH/s · {w.Temp:0.0} °C · {(w.EfficiencyJth is { } e ? $"{e:0.00} J/TH" : "–")} ({w.Minutes} min)";
}

public sealed partial class DeviceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 240;
    private const int MaxLogLines = 2000;
    private const int SimulationSpeedup = 30;

    private readonly AppHost _host;
    private readonly MinerConnection _connection;
    private ProfileRegistry _registry => _host.Profiles;
    private ResultStore _store => _host.Results;
    private CancellationTokenSource? _cts;
    private BenchmarkEngine? _engine;
    private Task? _runTask;
    private bool _refreshing;
    private bool _profileResolved;
    private bool _assigningProfile;

    /// <param name="connection">Gemeinsame Verbindung aus dem zentralen Polling – kein eigener Abruf-Timer mehr.</param>
    public DeviceViewModel(MinerConnection connection, DeviceConfig config, AppHost host)
    {
        _connection = connection;
        _host = host;
        Config = config;
        Settings = new BenchmarkSettings();
        AvailableProfiles = host.Profiles.Profiles;
        Logs = new LogViewModel(connection);
        _host.TuningApplied += OnTuningApplied;
        _host.LogAlerts.Triggered += OnLogAlert;
    }

    private void OnLogAlert(string host, LogLine line, string rule)
    {
        if (!string.Equals(host, Address, StringComparison.OrdinalIgnoreCase)) return;
        Application.Current?.Dispatcher.BeginInvoke(() => AddLog($"Log-Alarm ({rule}): {line.Tag} {line.Message}"));
    }

    /// <summary>Miner-Logs (Tab "Miner-Logs").</summary>
    public LogViewModel Logs { get; }

    private void OnTuningApplied(TuningEvent e)
    {
        if (string.Equals(e.Host, Address, StringComparison.OrdinalIgnoreCase)) Logs.AddTuningMarker(e);
    }

    /// <summary>Gerät aus der gemeinsamen Geräteliste (config.json).</summary>
    public DeviceConfig Config { get; private set; }
    public IMinerClient Client => _connection;
    public string Address => _connection.Address;
    public bool IsSimulated => _connection.Inner is SimulatedMinerClient;
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
        nameof(ResumeBenchmarkCommand), nameof(ApplyBestCommand), nameof(ApplyResultCommand), nameof(ApplyManualCommand),
        nameof(RestoreSettingsCommand))]
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

    /// <summary>Manuelles Einstellen (Tab Live).</summary>
    [ObservableProperty] private string _manualFrequency = "";
    [ObservableProperty] private string _manualVoltage = "";

    public ObservableCollection<TuningComparisonRow> Comparisons { get; } = [];

    public bool IsIdle => !IsRunning;
    public string Title => !string.IsNullOrWhiteSpace(Config.Name) ? Config.Name : Info?.DisplayName ?? Address;
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

    public void Initialize()
    {
        AddLog($"Gerät: {Title} ({Address})");

        // Manuell gewähltes Profil aus config.json, sonst Erkennung beim ersten Datenpunkt
        if (Config.ProfileId is { } id && _registry.Profiles.FirstOrDefault(p => p.Id == id) is { } chosen)
        {
            SetProfile(chosen.Clone(), fromUser: false);
            _profileResolved = true;
            AddLog($"Profil aus den Einstellungen: „{chosen.Name}“");
        }
        else
        {
            SetProfile(AvailableProfiles.First(p => p.Id == _registry.Generic.Id), fromUser: false);
        }

        var last = _store.LoadLatest(Address, Info?.Hostname);
        if (last is not null)
        {
            Session = last;
            SetResults(last.Results);
            AddLog($"Letzter Lauf vom {last.StartedAt:g} geladen ({last.Results.Count} Ergebnisse" +
                   (last.IsFinished ? ")." : ", nicht abgeschlossen – kann fortgesetzt werden)."));
        }
        RefreshComparisons();
        LoadAutomation();
    }

    /// <summary>Nach jeder zentralen Abfragerunde (UI-Thread).</summary>
    public void OnPolled(MinerState state)
    {
        if (!ReferenceEquals(Config, state.Config))
        {
            Config = state.Config;   // nach "Einstellungen speichern" neue Kopie
            LoadAutomation();
        }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EstimatedDurationText));

        if (state.Online && state.Normalized is { } info)
        {
            IsOnline = true;
            // Während eines Benchmarks liefert die Engine die Live-Werte (gleiche Verbindung, kein Doppelabruf)
            if (!IsRunning) UpdateLive(info);
            if (!_profileResolved) _ = ResolveProfileAsync(info);
        }
        else
        {
            IsOnline = false;
            Status = _connection.InMaintenance ? "Neustart/Tuning …" : state.Error ?? "Offline";
        }
    }

    private async Task ResolveProfileAsync(MinerInfo info)
    {
        _profileResolved = true;
        AsicInfo? asic = null;
        try { asic = await Client.GetAsicInfoAsync(); } catch (MinerApiException) { }
        var matched = _registry.Match(info, asic);
        AvailableProfiles = _registry.Profiles.Select(p => p.Id == matched.Id ? matched : p).ToList();
        SetProfile(matched, fromUser: false);
        AddLog($"Erkannt: {info.DeviceModel ?? info.AsicModel} ({FirmwareName(info.Firmware)} {info.FirmwareVersion}) → Profil „{matched.Name}“");
    }

    private void SetProfile(DeviceProfile profile, bool fromUser)
    {
        _assigningProfile = !fromUser;
        try { Profile = profile; }
        finally { _assigningProfile = false; }
    }

    partial void OnProfileChanged(DeviceProfile? value)
    {
        if (value is null) return;
        if (!_assigningProfile && !IsRunning)
        {
            // Vom Benutzer gewählt: dauerhaft in der gemeinsamen Geräteliste merken
            Config.ProfileId = value.Id;
            _host.Config.Save();
            AddLog($"Profil gewählt: „{value.Name}“ (gespeichert)");
        }
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
            Status = "Offline";
            AddLog(ex.Message);
        }
        finally
        {
            _refreshing = false;
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

        // Profilgrenzen je ASIC-Modell: der Suchbereich darf sie nicht überschreiten
        if (settings.MaxFrequencyMhz > Profile.MaxFrequencyMhz || settings.MaxVoltageMv > Profile.MaxVoltageMv ||
            settings.StartFrequencyMhz < Profile.MinFrequencyMhz || settings.MinVoltageMv < Profile.MinVoltageMv)
        {
            MessageBox.Show($"Der Suchbereich liegt außerhalb der Grenzen für {Profile.Name}:\n" +
                            $"Frequenz {Profile.MinFrequencyMhz}–{Profile.MaxFrequencyMhz} MHz, Spannung {Profile.MinVoltageMv}–{Profile.MaxVoltageMv} mV.",
                "Grenzwerte", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Einmalige Bestätigung für den ganzen Lauf (jeder einzelne Schritt wird in history.db protokolliert)
        var overclock = false;
        try { overclock = await Client.WillEnableOverclockAsync(settings.MaxFrequencyMhz, settings.MaxVoltageMv); }
        catch (MinerApiException) { }
        var current = Info is { } ci ? $"{ci.FrequencyMhz} MHz / {ci.CoreVoltageMv} mV" : "unbekannt (Gerät nicht erreichbar)";
        var restore = settings.RestoreMode == RestoreMode.Best
            ? $"beste Einstellung ({BenchmarkEngine.RankingName(settings.RestoreRanking)}); ohne stabiles Ergebnis die aktuelle"
            : "aktuelle Einstellung (" + current + ")";
        var msg = $"{(resume ? "Benchmark fortsetzen" : "Benchmark starten")} für {Title}?\n\n" +
                  $"Aktuell: {current}\n" +
                  $"Frequenz: {settings.StartFrequencyMhz} → {settings.MaxFrequencyMhz} MHz (Schritt {settings.FrequencyStepMhz})\n" +
                  $"Spannung: {settings.StartVoltageMv} → {settings.MaxVoltageMv} mV (Schritt {settings.VoltageStepMv})\n" +
                  $"Grenzen: Chip {settings.MaxChipTempC} °C · VR {settings.MaxVrTempC} °C · {settings.MaxPowerW} W\n" +
                  $"Profilgrenzen {Profile.Name}: {Profile.MinFrequencyMhz}–{Profile.MaxFrequencyMhz} MHz, {Profile.MinVoltageMv}–{Profile.MaxVoltageMv} mV\n" +
                  $"Am Ende gesetzt: {restore}\n" +
                  $"{EstimatedDurationText}\n" +
                  (overclock ? "\nHinweis: Für Werte außerhalb der AxeOS-Auswahlliste wird „overclockEnabled“ eingeschaltet.\n" : "") +
                  "\nWährend des Laufs pausiert der Watchdog für diesen Miner, Offline-Meldungen für die Neustarts entfallen.\n" +
                  "Übertakten geschieht auf eigenes Risiko. Stelle sicher, dass Netzteil und Kühlung ausreichen.";
        if (MessageBox.Show(msg, "Benchmark", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await TryAutoBackupAsync("vor Benchmark");

        if (!resume)
        {
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
        // Wartungsfenster für die gesamte Laufzeit (+3 min Nachlauf): Watchdog und Offline-Meldungen ruhen
        using var maintenance = _connection.BeginMaintenance("Benchmark");
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
            RefreshComparisons();
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

    private Task ApplyAsync(StepResult r) => ApplyValuesAsync(r.FrequencyMhz, r.CoreVoltageMv);

    private bool CanApplyManual() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplyManual))]
    private Task ApplyManual()
    {
        if (!int.TryParse(ManualFrequency.Trim(), out var f) || !int.TryParse(ManualVoltage.Trim(), out var mv))
        {
            MessageBox.Show("Bitte Frequenz (MHz) und Kernspannung (mV) als ganze Zahlen eingeben.", "Manuell einstellen",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
        return ApplyValuesAsync(f, mv);
    }

    /// <summary>
    /// Frequenz/Spannung setzen – nur nach ausdrücklicher Bestätigung mit aktuellem und neuem Wert.
    /// Werte außerhalb der Profilgrenzen des ASIC-Modells werden abgelehnt.
    /// </summary>
    private async Task ApplyValuesAsync(int frequencyMhz, int coreVoltageMv, string? intro = null)
    {
        if (Profile is { } p && (frequencyMhz < p.MinFrequencyMhz || frequencyMhz > p.MaxFrequencyMhz ||
                                 coreVoltageMv < p.MinVoltageMv || coreVoltageMv > p.MaxVoltageMv))
        {
            MessageBox.Show($"{frequencyMhz} MHz / {coreVoltageMv} mV liegt außerhalb der Grenzen für {p.Name}:\n" +
                            $"Frequenz {p.MinFrequencyMhz}–{p.MaxFrequencyMhz} MHz, Spannung {p.MinVoltageMv}–{p.MaxVoltageMv} mV.",
                "Grenzwerte", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MinerInfo? now = null;
        try { now = await Client.GetInfoAsync(); } catch (MinerApiException) { }
        var overclock = false;
        try { overclock = await Client.WillEnableOverclockAsync(frequencyMhz, coreVoltageMv); } catch (MinerApiException) { }
        var restart = _host.Config.RestartAfterApply;

        var text = (intro is null ? "" : intro + "\n\n") + $"Einstellung für {Title} ändern?\n\n" +
                   $"Frequenz:      {(now is null ? "?" : now.FrequencyMhz.ToString())} MHz  →  {frequencyMhz} MHz\n" +
                   $"Kernspannung:  {(now is null ? "?" : now.CoreVoltageMv.ToString())} mV  →  {coreVoltageMv} mV\n\n" +
                   $"Grenzen {Profile?.Name}: {Profile?.MinFrequencyMhz}–{Profile?.MaxFrequencyMhz} MHz, {Profile?.MinVoltageMv}–{Profile?.MaxVoltageMv} mV\n" +
                   (overclock ? "Der Wert liegt außerhalb der AxeOS-Auswahlliste – „overclockEnabled“ wird eingeschaltet.\n" : "") +
                   (restart ? "Das Gerät wird danach neu gestartet (Watchdog und Offline-Meldung pausieren).\n" : "") +
                   "\nDie Änderung wird mit Zeitstempel in history.db protokolliert.";
        if (MessageBox.Show(text, "Einstellung anwenden", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await TryAutoBackupAsync("vor manueller Änderung");

        try
        {
            await Client.ApplySettingsAsync(frequencyMhz, coreVoltageMv, TuningSource.Manual);
            if (restart) await Client.RestartAsync();
            AddLog($"Angewendet: {(now is null ? "?" : $"{now.FrequencyMhz} MHz / {now.CoreVoltageMv} mV")} → {frequencyMhz} MHz / {coreVoltageMv} mV");
            RefreshComparisons();
        }
        catch (MinerApiException ex)
        {
            MessageBox.Show(ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Automatik: Voreinstellungen, Regeln ----------

    [ObservableProperty] private string _automationStatus = "keine Automatik";
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
        Presets.Clear();
        foreach (var p in Config.Presets) Presets.Add(p);
        ScheduleEntries.Clear();
        foreach (var e in Config.Schedule.Entries) ScheduleEntries.Add(e);
        OnPropertyChanged(nameof(ThermalGuard));
        OnPropertyChanged(nameof(Schedule));
        OnPropertyChanged(nameof(ScheduleIsPrice));
        OnPropertyChanged(nameof(ScheduleIsTime));
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
        if (Info is null) { AddLog("Kein aktueller Wert – Miner nicht erreichbar."); return; }
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"{Info.FrequencyMhz} MHz" : NewPresetName.Trim();
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
            MessageBox.Show("Es gibt noch keine stabilen Benchmark-Ergebnisse für dieses Gerät.", "Voreinstellungen",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UpsertPreset(new TuningPreset("Hashrate", hash.FrequencyMhz, hash.CoreVoltageMv));
        UpsertPreset(new TuningPreset("Effizienz", eff.FrequencyMhz, eff.CoreVoltageMv));
    }

    private void UpsertPreset(TuningPreset preset)
    {
        if (Profile is { } p && (preset.FrequencyMhz < p.MinFrequencyMhz || preset.FrequencyMhz > p.MaxFrequencyMhz ||
                                 preset.CoreVoltageMv < p.MinVoltageMv || preset.CoreVoltageMv > p.MaxVoltageMv))
        {
            MessageBox.Show($"{preset} liegt außerhalb der Grenzen für {p.Name}.", "Voreinstellungen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var existing = Presets.FirstOrDefault(x => string.Equals(x.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Presets[Presets.IndexOf(existing)] = preset;
        else Presets.Add(preset);
        StoreAutomation();
        AddLog($"Voreinstellung gespeichert: {preset}");
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
        AddLog("Automatik-Einstellungen gespeichert" +
               (ThermalGuard.Enabled && !ThermalGuard.IsApproved(Address) || Schedule.Enabled && !Schedule.IsApproved(Address)
                   ? " – Freigabe erforderlich." : "."));
    }

    [RelayCommand]
    private void ApproveThermalGuard()
    {
        StoreAutomation();
        var g = ThermalGuard;
        var text = $"Temperaturschutz für {Title} freigeben?\n\n" +
                   $"Wenn die Chiptemperatur über {g.MaxChipTempC:0.#} °C oder die VR-Temperatur über {g.MaxVrTempC:0.#} °C liegt " +
                   $"(durchgehend {g.Minutes} min), senkt die App die Frequenz um {g.StepMhz} MHz, nie unter {g.MinFrequencyMhz} MHz. " +
                   "Die Kernspannung bleibt unverändert.\n" +
                   (g.Recover ? $"Ist der Miner {g.RecoverMinutes} min mindestens 5 °C unter den Grenzen, geht sie schrittweise zurück bis zur ursprünglichen Frequenz.\n" : "") +
                   "\nJede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet. Ändert sich die Regel, ist eine neue Freigabe nötig.";
        Approve(g, text, "Temperaturschutz");
    }

    [RelayCommand]
    private void ApproveSchedule()
    {
        StoreAutomation();
        var s = Schedule;
        string body;
        if (s.Mode == "price")
        {
            body = $"Strompreis ({_host.Prices.SourceName}) ≤ {s.ThresholdCt:0.##} ct/kWh → {PresetText(s.CheapPreset)}\n" +
                   $"sonst → {PresetText(s.ExpensivePreset)}";
        }
        else
        {
            body = string.Join("\n", s.Entries.Select(e => $"{e.DaysText} {e.FromHour:00}–{e.ToHour:00} Uhr → {PresetText(e.Preset)}")) +
                   $"\nsonst → {(string.IsNullOrWhiteSpace(s.DefaultPreset) ? "keine Änderung" : PresetText(s.DefaultPreset))}";
        }
        var text = $"{(s.Mode == "price" ? "Strompreis-Regel" : "Zeitplan")} für {Title} freigeben?\n\n{body}\n\n" +
                   $"Zwischen zwei automatischen Änderungen liegen mindestens {AutomationEngine.MinGap.TotalMinutes:0} min. " +
                   "Während eines Benchmarks, Dauertests oder abgesenkten Temperaturschutzes pausiert die Regel. " +
                   "Jede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet.";
        Approve(s, text, "Zeitplan");
    }

    private string PresetText(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
            ? p.ToString() : $"„{name}“ (fehlt!)";

    private void Approve(AutomationRule rule, string text, string label)
    {
        if (!rule.Enabled)
        {
            MessageBox.Show($"{label} ist nicht eingeschaltet.", label, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(text, label + " freigeben", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        rule.Approve(Address);
        _host.Config.Save();
        AddLog($"{label} freigegeben.");
    }

    // ---------- Dauertest ----------

    [ObservableProperty] private string _soakStatus = "";
    [ObservableProperty] private int _soakHours = 24;
    public IReadOnlyList<int> SoakHourOptions { get; } = [6, 12, 24, 48];
    public bool SoakActive => Config.Soak is not null;

    [RelayCommand]
    private void StartSoak()
    {
        if (Info is null) { AddLog("Dauertest: Miner nicht erreichbar."); return; }
        if (IsRunning) { AddLog("Dauertest: zuerst den Benchmark beenden."); return; }
        if (MessageBox.Show($"Dauertest für {Title} starten?\n\n" +
                            $"Beobachtet wird die aktuelle Einstellung {Info.FrequencyMhz} MHz / {Info.CoreVoltageMv} mV für {SoakHours} h: " +
                            $"Hashrate (Ø 15 min, mind. {SoakMonitor.RatioThreshold:P0} der Soll-Hashrate), Fehlerrate, Temperaturen, Erreichbarkeit.\n\n" +
                            "Am Miner wird dabei nichts geändert. Zeitplan/Strompreis-Regel pausieren so lange. " +
                            "Bei einem Fehler meldet die App das und schlägt die nächstniedrigere stabile Einstellung vor (nur nach Bestätigung).",
                "Dauertest", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var now = DateTime.Now;
        Config.Soak = new SoakTestState(now, now.AddHours(SoakHours), Info.FrequencyMhz, Info.CoreVoltageMv);
        _host.Config.Save();
        SoakStatus = "Dauertest gestartet – Anlaufphase";
        OnPropertyChanged(nameof(SoakActive));
        AddLog($"Dauertest gestartet: {Info.FrequencyMhz} MHz / {Info.CoreVoltageMv} mV für {SoakHours} h");
    }

    [RelayCommand]
    private void StopSoak()
    {
        if (Config.Soak is null) return;
        Config.Soak = null;
        _host.Config.Save();
        SoakStatus = "Dauertest abgebrochen";
        OnPropertyChanged(nameof(SoakActive));
        AddLog("Dauertest abgebrochen.");
    }

    /// <summary>Vom Koordinator nach Ende des Dauertests aufgerufen.</summary>
    public async Task OnSoakFinishedAsync(SoakResult result, SoakTestState soak)
    {
        SoakStatus = result.Message;
        OnPropertyChanged(nameof(SoakActive));
        AddLog(result.Message);
        if (result.Outcome != SoakOutcome.Failed) return;

        if (SoakMonitor.SuggestLower(Results, soak.FrequencyMhz) is { } s)
            await ApplyValuesAsync(s.Frequency, s.Voltage,
                $"Dauertest fehlgeschlagen: {result.Message}\n\nVorschlag: nächstniedrigere stabile Einstellung aus dem letzten Benchmark.");
        else
            AddLog("Kein Vorschlag möglich – im letzten Benchmark gibt es keine stabile Einstellung unterhalb dieser Frequenz.");
    }

    // ---------- Einstellungen sichern / wiederherstellen ----------

    /// <summary>Aktuelle Einstellungen des Miners sichern (&lt;Datenordner&gt;\snapshots).</summary>
    [RelayCommand]
    private async Task BackupSettings()
    {
        try
        {
            var raw = await _connection.GetRawInfoAsync();
            var snap = _host.Snapshots.Save(Address, Title, raw, "manuell");
            AddLog($"Einstellungen gesichert: {snap.DisplayText}");
            MessageBox.Show($"Einstellungen von {Title} gesichert:\n{snap.DisplayText}\n\n{snap.FilePath}\n\n" +
                            "Hinweis: Die Datei enthält auch Pool-Benutzer (Wallet-Adresse). Pool-Passwörter liefert AxeOS nicht aus.",
                "Einstellungen sichern", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is MinerApiException or System.Text.Json.JsonException or IOException)
        {
            MessageBox.Show("Sichern fehlgeschlagen: " + ex.Message, "Einstellungen sichern", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task TryAutoBackupAsync(string reason)
    {
        if (IsSimulated) return;
        try
        {
            var raw = await _connection.GetRawInfoAsync();
            _host.Snapshots.Save(Address, Title, raw, reason);
            AddLog($"Automatische Sicherung ({reason})");
        }
        catch (Exception ex)
        {
            AddLog($"Automatische Sicherung ({reason}) fehlgeschlagen: {ex.Message}");
        }
    }

    private bool CanRestore() => !IsRunning;

    /// <summary>Gesicherte Einstellungen zurückspielen – nur ausgewählte, geänderte Felder, nach Bestätigung.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreSettings()
    {
        var snapshots = _host.Snapshots.List(Address);
        if (snapshots.Count == 0)
        {
            MessageBox.Show($"Für {Title} gibt es noch keine Sicherung.", "Wiederherstellen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string current;
        try { current = await _connection.GetRawInfoAsync(); }
        catch (MinerApiException ex)
        {
            MessageBox.Show(ex.Message, "Wiederherstellen", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new Views.RestoreWindow(Title, snapshots, snap => SettingsSnapshots.Diff(snap, current), Profile)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.Selected.Count == 0) return;

        var changes = dialog.Selected;
        await TryAutoBackupAsync("vor Wiederherstellung");
        try
        {
            // Frequenz/Spannung über den protokollierten Weg (history.db, Diagramm-Markierung)
            var freq = changes.FirstOrDefault(c => c.Field == "frequency");
            var volt = changes.FirstOrDefault(c => c.Field == "coreVoltage");
            if (freq is not null || volt is not null)
            {
                var info = await Client.GetInfoAsync();
                var f = freq is not null ? (int)SettingsSnapshots.ToPatchValue(freq.Value) : info.FrequencyMhz;
                var v = volt is not null ? (int)SettingsSnapshots.ToPatchValue(volt.Value) : info.CoreVoltageMv;
                await Client.ApplySettingsAsync(f, v, TuningSource.Restore);
            }

            var rest = changes.Where(c => c.Field is not "frequency" and not "coreVoltage")
                              .ToDictionary(c => c.Field, c => SettingsSnapshots.ToPatchValue(c.Value));
            if (rest.Count > 0) await _connection.PatchSettingsAsync(rest);

            var restart = _host.Config.RestartAfterApply || changes.Any(c => c.Group == SettingGroup.Pool);
            if (restart) await Client.RestartAsync();

            AddLog($"Wiederhergestellt ({dialog.Snapshot!.DisplayText}): " +
                   string.Join(", ", changes.Select(c => $"{c.Label} {c.Current} → {c.Saved}")));
            RefreshComparisons();
        }
        catch (MinerApiException ex)
        {
            MessageBox.Show("Wiederherstellen fehlgeschlagen: " + ex.Message, "Wiederherstellen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Vorher/Nachher ----------

    /// <summary>
    /// Je Tuning-Änderung: Ø Hashrate, Temperatur und J/TH in den 60 min davor und 60 min danach
    /// (die ersten 5 min nach der Änderung werden als Anlaufphase übersprungen).
    /// </summary>
    [RelayCommand]
    private void RefreshComparisons()
    {
        Comparisons.Clear();
        if (_host.History is not { } history || IsSimulated) return;
        try
        {
            var events = history.QueryTuningEvents(Address, DateTime.Now.AddDays(-30), DateTime.Now)
                .OrderByDescending(e => e.Time).Take(50);
            foreach (var e in events)
            {
                var before = history.Average(Address, e.Time.AddMinutes(-60), e.Time.AddMinutes(-1));
                var after = e.Time.AddMinutes(5) < DateTime.Now
                    ? history.Average(Address, e.Time.AddMinutes(5), e.Time.AddMinutes(65))
                    : null;
                Comparisons.Add(new TuningComparisonRow(e, before, after));
            }
        }
        catch (Exception ex)
        {
            AddLog("Vorher/Nachher nicht verfügbar: " + ex.Message);
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
        // Eingabefelder für "Manuell einstellen" mit den aktuellen Werten vorbelegen
        if (string.IsNullOrEmpty(ManualFrequency) && info.FrequencyMhz > 0) ManualFrequency = info.FrequencyMhz.ToString();
        if (string.IsNullOrEmpty(ManualVoltage) && info.CoreVoltageMv > 0) ManualVoltage = info.CoreVoltageMv.ToString();
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
        // Die Verbindung gehört dem zentralen Polling und wird dort freigegeben
        _cts?.Cancel();
        _host.TuningApplied -= OnTuningApplied;
        _host.LogAlerts.Triggered -= OnLogAlert;
        Logs.Dispose();
    }
}
