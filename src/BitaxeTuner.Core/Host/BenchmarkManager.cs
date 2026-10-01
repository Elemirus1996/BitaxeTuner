using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Anzeigezustand eines Benchmark-Laufs – dieselben Texte für Desktop und Browser.</summary>
public sealed class BenchmarkRun
{
    internal BenchmarkRun(BenchmarkSession session) => Session = session;

    public BenchmarkSession Session { get; }
    public bool IsRunning { get; internal set; }
    public bool IsPaused { get; internal set; }
    /// <summary>Lauf regulär abgeschlossen (nicht abgebrochen, kein Fehler).</summary>
    public bool Completed { get; internal set; }
    public DateTime Started { get; } = DateTime.Now;
    public string PhaseText { get; internal set; } = L.T("Vorbereitung");
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
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Auf diesem Gerät läuft bereits ein Benchmark."));
        var profile = device.Profile;
        var last = resume ? LatestSession(device) : null;
        if (resume && last is not { IsFinished: false, Results.Count: > 0 })
            throw new InvalidOperationException(L.T("Es gibt keinen unterbrochenen Lauf zum Fortsetzen."));
        var settings = resume ? last!.Settings : requested.Clone();

        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        // Profilgrenzen je ASIC-Modell: der Suchbereich darf sie nicht überschreiten
        if (settings.MaxFrequencyMhz > profile.MaxFrequencyMhz || settings.MaxVoltageMv > profile.MaxVoltageMv ||
            settings.StartFrequencyMhz < profile.MinFrequencyMhz || settings.MinVoltageMv < profile.MinVoltageMv)
            throw new InvalidOperationException(L.T("Der Suchbereich liegt außerhalb der Grenzen für {0}:\n", profile.Name) +
                L.T("Frequenz {0}–{1} MHz, Spannung {2}–{3} mV.", profile.MinFrequencyMhz, profile.MaxFrequencyMhz, profile.MinVoltageMv, profile.MaxVoltageMv));

        var overclock = false;
        try { overclock = await device.Connection.WillEnableOverclockAsync(settings.MaxFrequencyMhz, settings.MaxVoltageMv); }
        catch (MinerApiException) { }
        var info = device.Info;
        var current = info is { } ci ? L.T("{0} MHz / {1} mV", ci.FrequencyMhz, ci.CoreVoltageMv) : L.T("unbekannt (Gerät nicht erreichbar)");
        var restore = settings.RestoreMode == RestoreMode.Best
            ? L.T("beste Einstellung ({0}); ohne stabiles Ergebnis die aktuelle", BenchmarkEngine.RankingName(settings.RestoreRanking))
            : L.T("aktuelle Einstellung (") + current + ")";
        var text = L.T("{0} für {1}?\n\n", (resume ? L.T("Benchmark fortsetzen") : L.T("Benchmark starten")), device.Title) +
                   L.T("Aktuell: {0}\n", current) +
                   L.T("Frequenz: {0} → {1} MHz (Schritt {2})\n", settings.StartFrequencyMhz, settings.MaxFrequencyMhz, settings.FrequencyStepMhz) +
                   L.T("Spannung: {0} → {1} mV (Schritt {2})\n", settings.StartVoltageMv, settings.MaxVoltageMv, settings.VoltageStepMv) +
                   (settings.TryLowerVoltage
                       ? L.T("Je stabiler Frequenz zusätzlich weniger Spannung testen – bis hinunter auf {0} mV\n", settings.MinVoltageMv)
                       : "") +
                   L.T("Grenzen: Chip {0} °C · VR {1} °C · {2} W\n", settings.MaxChipTempC, settings.MaxVrTempC, settings.MaxPowerW) +
                   L.T("Profilgrenzen {0}: {1}–{2} MHz, {3}–{4} mV\n", profile.Name, profile.MinFrequencyMhz, profile.MaxFrequencyMhz, profile.MinVoltageMv, profile.MaxVoltageMv) +
                   L.T("Am Ende gesetzt: {0}\n", restore) +
                   $"{EstimatedDurationText(settings)}\n" +
                   (overclock ? L.T("\nHinweis: Für Werte außerhalb der AxeOS-Auswahlliste wird „overclockEnabled“ eingeschaltet.\n") : "") +
                   L.T("\nWährend des Laufs pausiert der Watchdog für diesen Miner, Offline-Meldungen für die Neustarts entfallen.\n") +
                   L.T("Übertakten geschieht auf eigenes Risiko. Stelle sicher, dass Netzteil und Kühlung ausreichen.");
        return new BenchmarkPlan(settings, resume, text);
    }

    /// <summary>Bestätigten Lauf ausführen. Die Aufgabe endet, wenn der Lauf fertig, abgebrochen oder fehlgeschlagen ist.</summary>
    public async Task RunAsync(HubDevice device, BenchmarkPlan plan)
    {
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Auf diesem Gerät läuft bereits ein Benchmark."));
        var profile = device.Profile;
        await _hub.TryAutoBackupAsync(device, L.T("vor Benchmark"));

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
        using var maintenance = device.Connection.BeginMaintenance(L.T("Benchmark"));
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
            run.Completed = true;
            run.PhaseText = L.T("Fertig");
        }
        catch (OperationCanceledException)
        {
            run.PhaseText = L.T("Abgebrochen");
        }
        catch (Exception ex)
        {
            run.PhaseText = L.T("Fehler");
            device.AddLog(L.T("Fehler: {0}", ex.Message));
        }
        finally
        {
            run.IsRunning = false;
            run.IsPaused = false;
            run.Engine = null;
            run.Cts.Dispose();
            run.Cts = null;
            run.StepText = session.FinishReason ?? "";
            run.EtaText = L.T("Dauer: {0}", FormatDuration(DateTime.Now - run.Started));
            StateChanged?.Invoke(device);
        }
    }

    public void Stop(HubDevice device)
    {
        if (device.Benchmark is not { IsRunning: true } run) return;
        run.PhaseText = L.T("Stoppe – stelle Einstellungen wieder her …");
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
        if (run.IsPaused) run.PhaseText = L.T("Pausiert");
        StateChanged?.Invoke(device);
        return run.IsPaused;
    }

    private void OnProgress(HubDevice device, BenchmarkRun run, BenchmarkProgress p)
    {
        run.PhaseText = p.Phase switch
        {
            BenchmarkPhase.Preparing => L.T("Vorbereitung"),
            BenchmarkPhase.Applying => L.T("Einstellung wird gesetzt"),
            BenchmarkPhase.Restarting => L.T("Neustart – warte auf Gerät"),
            BenchmarkPhase.WarmingUp => L.T("Aufwärmen"),
            BenchmarkPhase.Measuring => L.T("Messung läuft"),
            BenchmarkPhase.Restoring => L.T("Stelle Einstellung wieder her"),
            BenchmarkPhase.Finished => L.T("Fertig"),
            BenchmarkPhase.Cancelled => L.T("Abgebrochen"),
            BenchmarkPhase.Failed => L.T("Fehler"),
            _ => run.PhaseText,
        };
        if (run.IsPaused) run.PhaseText = L.T("Pausiert");
        if (p.FrequencyMhz > 0)
            run.StepText = L.T("Schritt {0} von ca. {1}: {2} MHz / {3} mV", p.StepIndex, p.EstimatedSteps, p.FrequencyMhz, p.CoreVoltageMv);
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
                run.EtaText = L.T("Restzeit höchstens ≈ {0}", FormatDuration(TimeSpan.FromTicks((long)(run.Session.Settings.EstimatedStepDuration.Ticks * remainingSteps / timeScale))));
            }
        }
        Progress?.Invoke(device, p);
    }

    public static string EstimatedDurationText(BenchmarkSettings settings)
    {
        var total = TimeSpan.FromTicks(settings.EstimatedStepDuration.Ticks * settings.EstimatedSteps);
        return L.T("≈ {0} Schritte à {1:F0} min – bis zu {2}", settings.EstimatedSteps, settings.EstimatedStepDuration.TotalMinutes, FormatDuration(total));
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? L.T("{0} h {1:D2} min", (int)t.TotalHours, t.Minutes) : L.T("{0} min", Math.Max(0, (int)t.TotalMinutes));
}
