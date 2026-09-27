using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Benchmark;

namespace BitaxeTuner.Core.Host;

/// <summary>Anzeigezustand eines Benchmark-Laufs – dieselben Texte für Desktop und Browser.</summary>
public sealed class BenchmarkRun
{
    internal BenchmarkRun(BenchmarkSession session) => Session = session;

    public BenchmarkSession Session { get; }
    public bool IsRunning { get; internal set; }
    public bool IsPaused { get; internal set; }
    public DateTime Started { get; } = DateTime.Now;
    public string PhaseText { get; internal set; } = "Vorbereitung";
    public string StepText { get; internal set; } = "";
    public string EtaText { get; internal set; } = "";
    public double PhaseProgress { get; internal set; }
    public double OverallProgress { get; internal set; }

    internal BenchmarkEngine? Engine { get; set; }
    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }
}

/// <summary>Geprüfter Startwunsch mit dem Bestätigungstext (Oberfläche zeigt ihn, erst danach <see cref="BenchmarkManager.RunAsync"/>).</summary>
public sealed record BenchmarkPlan(BenchmarkSettings Settings, bool Resume, string ConfirmText);

/// <summary>
/// Start, Pause, Stopp und Fortsetzen von Benchmarks je Gerät. Läuft im Hub (Desktop oder Server);
/// Ergebnisse landen über <see cref="Storage.ResultStore"/> im Datenordner, jeder Schritt in history.db.
/// </summary>
public sealed class BenchmarkManager
{
    /// <summary>Simulation läuft 30× schneller, damit ein kompletter Lauf in wenigen Minuten sichtbar ist.</summary>
    public const int SimulationSpeedup = 30;

    private readonly MinerHub _hub;

    internal BenchmarkManager(MinerHub hub) => _hub = hub;

    /// <summary>Fortschritt eines Laufs (nachdem <see cref="BenchmarkRun"/> aktualisiert wurde).</summary>
    public event Action<HubDevice, BenchmarkProgress>? Progress;

    /// <summary>Lauf gestartet oder beendet (IsRunning geändert).</summary>
    public event Action<HubDevice>? StateChanged;

    public bool AnyRunning => _hub.Devices.Any(d => d.IsBenchmarkRunning);

    /// <summary>Letzte gespeicherte Sitzung (für "Fortsetzen" und die Ergebnisliste).</summary>
    public BenchmarkSession? LatestSession(HubDevice device) =>
        device.Benchmark?.Session ?? _hub.Results.LoadLatest(device.Host, device.State.Normalized?.Hostname);

    /// <summary>Einstellungen prüfen (inkl. Profilgrenzen) und den Bestätigungstext bauen. Fehler → Exception mit Klartext.</summary>
    public async Task<BenchmarkPlan> PrepareAsync(HubDevice device, BenchmarkSettings requested, bool resume)
    {
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Auf diesem Gerät läuft bereits ein Benchmark.");
        var profile = device.Profile;
        var last = resume ? LatestSession(device) : null;
        if (resume && last is not { IsFinished: false, Results.Count: > 0 })
            throw new InvalidOperationException("Es gibt keinen unterbrochenen Lauf zum Fortsetzen.");
        var settings = resume ? last!.Settings : requested.Clone();

        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        // Profilgrenzen je ASIC-Modell: der Suchbereich darf sie nicht überschreiten
        if (settings.MaxFrequencyMhz > profile.MaxFrequencyMhz || settings.MaxVoltageMv > profile.MaxVoltageMv ||
            settings.StartFrequencyMhz < profile.MinFrequencyMhz || settings.MinVoltageMv < profile.MinVoltageMv)
            throw new InvalidOperationException($"Der Suchbereich liegt außerhalb der Grenzen für {profile.Name}:\n" +
                $"Frequenz {profile.MinFrequencyMhz}–{profile.MaxFrequencyMhz} MHz, Spannung {profile.MinVoltageMv}–{profile.MaxVoltageMv} mV.");

        var overclock = false;
        try { overclock = await device.Connection.WillEnableOverclockAsync(settings.MaxFrequencyMhz, settings.MaxVoltageMv); }
        catch (MinerApiException) { }
        var info = device.Info;
        var current = info is { } ci ? $"{ci.FrequencyMhz} MHz / {ci.CoreVoltageMv} mV" : "unbekannt (Gerät nicht erreichbar)";
        var restore = settings.RestoreMode == RestoreMode.Best
            ? $"beste Einstellung ({BenchmarkEngine.RankingName(settings.RestoreRanking)}); ohne stabiles Ergebnis die aktuelle"
            : "aktuelle Einstellung (" + current + ")";
        var text = $"{(resume ? "Benchmark fortsetzen" : "Benchmark starten")} für {device.Title}?\n\n" +
                   $"Aktuell: {current}\n" +
                   $"Frequenz: {settings.StartFrequencyMhz} → {settings.MaxFrequencyMhz} MHz (Schritt {settings.FrequencyStepMhz})\n" +
                   $"Spannung: {settings.StartVoltageMv} → {settings.MaxVoltageMv} mV (Schritt {settings.VoltageStepMv})\n" +
                   $"Grenzen: Chip {settings.MaxChipTempC} °C · VR {settings.MaxVrTempC} °C · {settings.MaxPowerW} W\n" +
                   $"Profilgrenzen {profile.Name}: {profile.MinFrequencyMhz}–{profile.MaxFrequencyMhz} MHz, {profile.MinVoltageMv}–{profile.MaxVoltageMv} mV\n" +
                   $"Am Ende gesetzt: {restore}\n" +
                   $"{EstimatedDurationText(settings)}\n" +
                   (overclock ? "\nHinweis: Für Werte außerhalb der AxeOS-Auswahlliste wird „overclockEnabled“ eingeschaltet.\n" : "") +
                   "\nWährend des Laufs pausiert der Watchdog für diesen Miner, Offline-Meldungen für die Neustarts entfallen.\n" +
                   "Übertakten geschieht auf eigenes Risiko. Stelle sicher, dass Netzteil und Kühlung ausreichen.";
        return new BenchmarkPlan(settings, resume, text);
    }

    /// <summary>Bestätigten Lauf ausführen. Die Aufgabe endet, wenn der Lauf fertig, abgebrochen oder fehlgeschlagen ist.</summary>
    public async Task RunAsync(HubDevice device, BenchmarkPlan plan)
    {
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Auf diesem Gerät läuft bereits ein Benchmark.");
        var profile = device.Profile;
        await _hub.TryAutoBackupAsync(device, "vor Benchmark");

        var session = plan.Resume ? LatestSession(device)! : new BenchmarkSession
        {
            DeviceAddress = device.Host,
            Hostname = device.Info?.Hostname,
            DeviceModel = device.Info?.DeviceModel ?? profile.Name,
            ProfileId = profile.Id,
            Settings = plan.Settings,
        };
        var run = new BenchmarkRun(session) { IsRunning = true, Cts = new CancellationTokenSource() };
        device.Benchmark = run;

        // Wartungsfenster für die gesamte Laufzeit (+3 min Nachlauf): Watchdog und Offline-Meldungen ruhen
        using var maintenance = device.Connection.BeginMaintenance("Benchmark");
        var simulated = device.IsSimulated;
        run.Engine = new BenchmarkEngine(device.Connection, profile)
        {
            Delay = _hub.Options.BenchmarkDelay ?? (simulated ? (t, ct) => Task.Delay(t / SimulationSpeedup, ct) : Task.Delay),
            Progress = new Progress<BenchmarkProgress>(p => OnProgress(device, run, p)),
            Log = device.AddLog,
            StepCompleted = s => _hub.Results.SaveAsync(s),
        };
        StateChanged?.Invoke(device);

        var task = run.Engine.RunAsync(session, run.Cts.Token);
        run.Task = task;
        try
        {
            await task;
            run.PhaseText = "Fertig";
        }
        catch (OperationCanceledException)
        {
            run.PhaseText = "Abgebrochen";
        }
        catch (Exception ex)
        {
            run.PhaseText = "Fehler";
            device.AddLog($"Fehler: {ex.Message}");
        }
        finally
        {
            run.IsRunning = false;
            run.IsPaused = false;
            run.Engine = null;
            run.Cts.Dispose();
            run.Cts = null;
            run.StepText = session.FinishReason ?? "";
            run.EtaText = $"Dauer: {FormatDuration(DateTime.Now - run.Started)}";
            StateChanged?.Invoke(device);
        }
    }

    public void Stop(HubDevice device)
    {
        if (device.Benchmark is not { IsRunning: true } run) return;
        run.PhaseText = "Stoppe – stelle Einstellungen wieder her …";
        run.Cts?.Cancel();
    }

    /// <summary>Bricht einen laufenden Benchmark ab und wartet, bis die Einstellungen wiederhergestellt sind.</summary>
    public async Task StopAndWaitAsync(HubDevice device)
    {
        if (device.Benchmark is not { IsRunning: true, Task: { } task } run) return;
        run.Cts?.Cancel();
        try { await task; } catch { /* Abbruch erwartet */ }
    }

    public Task StopAllAsync() => Task.WhenAll(_hub.Devices.Select(StopAndWaitAsync));

    public bool TogglePause(HubDevice device)
    {
        if (device.Benchmark is not { IsRunning: true, Engine: { } engine } run) return false;
        engine.IsPaused = !engine.IsPaused;
        run.IsPaused = engine.IsPaused;
        if (run.IsPaused) run.PhaseText = "Pausiert";
        StateChanged?.Invoke(device);
        return run.IsPaused;
    }

    private void OnProgress(HubDevice device, BenchmarkRun run, BenchmarkProgress p)
    {
        run.PhaseText = p.Phase switch
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
            _ => run.PhaseText,
        };
        if (run.IsPaused) run.PhaseText = "Pausiert";
        if (p.FrequencyMhz > 0)
            run.StepText = $"Schritt {p.StepIndex} von ca. {p.EstimatedSteps}: {p.FrequencyMhz} MHz / {p.CoreVoltageMv} mV";
        if (p.Message is not null && p.Phase is BenchmarkPhase.Restoring or BenchmarkPhase.Finished or BenchmarkPhase.Failed or BenchmarkPhase.Cancelled)
            run.StepText = p.Message;

        run.PhaseProgress = p.Phase is BenchmarkPhase.WarmingUp or BenchmarkPhase.Measuring ? p.PhaseProgress * 100 : run.PhaseProgress;
        if (p.EstimatedSteps > 0)
        {
            var within = p.Phase == BenchmarkPhase.WarmingUp ? p.PhaseProgress * 0.15 : p.Phase == BenchmarkPhase.Measuring ? 0.15 + p.PhaseProgress * 0.85 : 0;
            run.OverallProgress = Math.Min(100, (Math.Max(0, p.StepIndex - 1) + within) / p.EstimatedSteps * 100);
            if (p.Phase is BenchmarkPhase.WarmingUp or BenchmarkPhase.Measuring)
            {
                var remainingSteps = Math.Max(0, p.EstimatedSteps - p.StepIndex) + (1 - within);
                var timeScale = device.IsSimulated ? SimulationSpeedup : 1;
                run.EtaText = $"Restzeit höchstens ≈ {FormatDuration(TimeSpan.FromTicks((long)(run.Session.Settings.EstimatedStepDuration.Ticks * remainingSteps / timeScale)))}";
            }
        }
        Progress?.Invoke(device, p);
    }

    public static string EstimatedDurationText(BenchmarkSettings settings)
    {
        var total = TimeSpan.FromTicks(settings.EstimatedStepDuration.Ticks * settings.EstimatedSteps);
        return $"≈ {settings.EstimatedSteps} Schritte à {settings.EstimatedStepDuration.TotalMinutes:F0} min – bis zu {FormatDuration(total)}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min" : $"{Math.Max(0, (int)t.TotalMinutes)} min";
}
