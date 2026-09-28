using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Benchmark;

/// <summary>
/// Führt den automatischen Benchmark für genau ein Gerät aus. Mehrere Geräte laufen parallel,
/// indem pro Gerät eine eigene Instanz gestartet wird.
/// </summary>
public sealed class BenchmarkEngine(IMinerClient client, DeviceProfile profile)
{
    private const int MaxConsecutiveReadFailures = 3;
    private static readonly TimeSpan RebootInitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RebootPollInterval = TimeSpan.FromSeconds(5);
    private const int RebootMaxPolls = 36; // ≈ 3 Minuten

    private volatile bool _paused;

    /// <summary>Austauschbar für Tests (z. B. ohne echte Wartezeit).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public IProgress<BenchmarkProgress>? Progress { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>Wird nach jedem abgeschlossenen Schritt aufgerufen (z. B. zum Speichern).</summary>
    public Func<BenchmarkSession, Task>? StepCompleted { get; init; }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            Write(value ? L.T("Pausiert – Watchdog bleibt aktiv.") : L.T("Fortgesetzt."));
        }
    }

    /// <summary>Startet einen neuen Lauf oder setzt <paramref name="session"/> fort.</summary>
    public async Task<BenchmarkSession> RunAsync(BenchmarkSession session, CancellationToken ct)
    {
        var s = session.Settings;
        var errors = s.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors));

        Report(BenchmarkPhase.Preparing, session, 0, 0, 0, message: L.T("Lese aktuelle Einstellungen …"));
        var start = await client.GetInfoAsync(ct).ConfigureAwait(false);
        session.Hostname ??= start.Hostname;
        session.DeviceModel ??= start.DeviceModel ?? profile.Name;
        session.ProfileId ??= profile.Id;
        session.Original ??= new OriginalSettings(start.FrequencyMhz, start.CoreVoltageMv, start.AutoFanMode, start.FanPercent);
        session.FinishedAt = null;
        session.FinishReason = null;
        Write(L.T("Start: {0} ({1}), aktuell {2} MHz / {3} mV. ", start.DisplayName, profile.Name, start.FrequencyMhz, start.CoreVoltageMv) +
              L.T("Ursprüngliche Werte: {0} MHz / {1} mV.", session.Original.FrequencyMhz, session.Original.CoreVoltageMv));

        var finalPhase = BenchmarkPhase.Finished;
        try
        {
            if (s.FanMode == FanModeDuringBenchmark.Full)
            {
                Write(L.T("Lüfter auf 100 % für die Dauer des Tests."));
                await client.SetFanAsync(0, 100, ct).ConfigureAwait(false);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var next = StepPlanner.Next(s, session.Results, out var reason);
                if (next is null)
                {
                    session.FinishReason = reason;
                    Write(L.T("Fertig: {0}", reason));
                    break;
                }

                var result = await RunStepAsync(session, next.Value.FrequencyMhz, next.Value.CoreVoltageMv, ct).ConfigureAwait(false);
                session.Results.Add(result);
                Write(L.T("Ergebnis {0} MHz / {1} mV: {2} – ", result.FrequencyMhz, result.CoreVoltageMv, result.OutcomeText) +
                      L.T("{0:F1} GH/s ({1:P1}), {2:F1} W, ", result.AvgHashRateGh, result.HashRateRatio, result.AvgPowerW) +
                      L.T("{0:F2} J/TH, max. {1:F1} °C", result.EfficiencyJth, result.MaxChipTempC) +
                      (result.Message is null ? "" : $" – {result.Message}"));
                Progress?.Report(new BenchmarkProgress
                {
                    Phase = BenchmarkPhase.Measuring,
                    StepIndex = session.Results.Count,
                    EstimatedSteps = s.EstimatedSteps,
                    FrequencyMhz = result.FrequencyMhz,
                    CoreVoltageMv = result.CoreVoltageMv,
                    PhaseProgress = 1,
                    CompletedStep = result,
                });
                if (StepCompleted is not null)
                    await StepCompleted(session).ConfigureAwait(false);
            }

            session.FinishedAt = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            finalPhase = BenchmarkPhase.Cancelled;
            session.FinishReason = L.T("Vom Benutzer abgebrochen.");
            Write(L.T("Abgebrochen – Einstellungen werden wiederhergestellt."));
        }
        catch (Exception ex)
        {
            finalPhase = BenchmarkPhase.Failed;
            session.FinishReason = L.T("Fehler: {0}", ex.Message);
            Write(L.T("Fehler: {0} – Einstellungen werden wiederhergestellt.", ex.Message));
        }
        finally
        {
            await RestoreAsync(session).ConfigureAwait(false);
            if (StepCompleted is not null)
            {
                try { await StepCompleted(session).ConfigureAwait(false); } catch { /* Speichern ist best effort */ }
            }
            Report(finalPhase, session, 0, 0, 1, message: session.FinishReason);
        }

        if (finalPhase == BenchmarkPhase.Cancelled)
            ct.ThrowIfCancellationRequested();
        return session;
    }

    private async Task<StepResult> RunStepAsync(BenchmarkSession session, int freq, int mv, CancellationToken ct)
    {
        var s = session.Settings;
        Write(L.T("Schritt {0}: {1} MHz / {2} mV", session.Results.Count + 1, freq, mv));
        Report(BenchmarkPhase.Applying, session, freq, mv, 0);

        try
        {
            await client.ApplySettingsAsync(freq, mv, TuningSource.Benchmark, ct).ConfigureAwait(false);
            if (s.RestartAfterApply)
            {
                Report(BenchmarkPhase.Restarting, session, freq, mv, 0);
                await client.RestartAsync(ct).ConfigureAwait(false);
                await WaitForDeviceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (MinerApiException ex)
        {
            return Error(freq, mv, ex.Message);
        }

        // Aufwärmen – der Watchdog prüft trotzdem bei jedem Intervall die Grenzen.
        var interval = TimeSpan.FromSeconds(s.SampleIntervalSeconds);
        var warmupPolls = (int)Math.Ceiling(s.WarmupSeconds / (double)s.SampleIntervalSeconds);
        var failures = 0;
        for (var i = 0; i < warmupPolls; i++)
        {
            await PausableDelayAsync(s, interval, ct).ConfigureAwait(false);
            var info = await TryReadAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                if (++failures >= MaxConsecutiveReadFailures) return Error(freq, mv, L.T("Gerät während des Aufwärmens nicht erreichbar."));
                continue;
            }
            failures = 0;
            Report(BenchmarkPhase.WarmingUp, session, freq, mv, (i + 1) / (double)warmupPolls, info);
            if (CheckLimits(s, info) is { } limit)
                return Limit(freq, mv, limit, [info]);
        }

        // Messen
        var samples = new List<MinerInfo>();
        var sampleCount = Math.Max(s.MinSamples, s.MeasureSeconds / s.SampleIntervalSeconds);
        failures = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            await PausableDelayAsync(s, interval, ct).ConfigureAwait(false);
            var info = await TryReadAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                if (++failures >= MaxConsecutiveReadFailures) return Error(freq, mv, L.T("Gerät während der Messung nicht erreichbar."));
                continue;
            }
            failures = 0;
            samples.Add(info);
            Report(BenchmarkPhase.Measuring, session, freq, mv, (i + 1) / (double)sampleCount, info);
            if (CheckLimits(s, info) is { } limit)
                return Limit(freq, mv, limit, samples);
        }

        return Evaluate(s, freq, mv, samples);
    }

    internal StepResult Evaluate(BenchmarkSettings s, int freq, int mv, IReadOnlyList<MinerInfo> samples)
    {
        if (samples.Count < s.MinSamples)
            return Error(freq, mv, L.T("Zu wenige Messwerte ({0} von mindestens {1}).", samples.Count, s.MinSamples));

        var result = Aggregate(freq, mv, samples, StepOutcome.Stable, null);
        var reasons = new List<string>();
        if (result.ExpectedHashRateGh > 0 && result.HashRateRatio < s.StabilityThreshold)
            reasons.Add(L.T("Hashrate nur {0:P1} der erwarteten (Soll ≥ {1:P0})", result.HashRateRatio, s.StabilityThreshold));
        if (result.AvgErrorPercent is { } err && err > s.MaxErrorPercent)
            reasons.Add(L.T("Fehlerrate {0:F2} % > {1:F2} %", err, s.MaxErrorPercent));
        if (result.AvgHashRateGh <= 0)
            reasons.Add(L.T("keine Hashrate"));

        return reasons.Count == 0 ? result : result with { Outcome = StepOutcome.Unstable, Message = string.Join("; ", reasons) };
    }

    private StepResult Aggregate(int freq, int mv, IReadOnlyList<MinerInfo> samples, StepOutcome outcome, string? message)
    {
        var expected = SampleStats.AverageOrNull(samples.Select(x => x.ExpectedHashRateGh))
            ?? (profile.SmallCoresPerAsic > 0 ? freq * profile.SmallCoresPerAsic * (double)profile.AsicCount / 1000.0 : 0);
        var last = samples.Count > 0 ? samples[^1] : null;
        return new StepResult
        {
            FrequencyMhz = freq,
            CoreVoltageMv = mv,
            Outcome = outcome,
            Message = message,
            SampleCount = samples.Count,
            AvgHashRateGh = SampleStats.TrimmedMean(samples.Select(x => x.HashRateGh).ToList()),
            ExpectedHashRateGh = expected,
            AvgPowerW = samples.Count > 0 ? SampleStats.TrimmedMean(samples.Select(x => x.PowerW).ToList()) : 0,
            AvgChipTempC = SampleStats.AverageOrNull(samples.Select(x => x.MaxChipTempC)),
            MaxChipTempC = SampleStats.MaxOrNull(samples.Select(x => x.MaxChipTempC)),
            AvgVrTempC = SampleStats.AverageOrNull(samples.Select(x => x.VrTempC)),
            MaxVrTempC = SampleStats.MaxOrNull(samples.Select(x => x.VrTempC)),
            AvgErrorPercent = SampleStats.AverageOrNull(samples.Select(x => x.ErrorPercent)),
            AvgInputVoltageMv = SampleStats.AverageOrNull(samples.Select(x => x.InputVoltageMv)),
            SharesAccepted = last?.SharesAccepted ?? 0,
            SharesRejected = last?.SharesRejected ?? 0,
        };
    }

    /// <summary>Prüft alle Sicherheitsgrenzen. Liefert eine Begründung, wenn eine überschritten ist.</summary>
    public static string? CheckLimits(BenchmarkSettings s, MinerInfo info)
    {
        if (info.MaxChipTempC is { } t && t > s.MaxChipTempC) return L.T("Chiptemperatur {0:F1} °C > {1:F0} °C", t, s.MaxChipTempC);
        if (info.VrTempC is { } vr && vr > s.MaxVrTempC) return L.T("VR-Temperatur {0:F1} °C > {1:F0} °C", vr, s.MaxVrTempC);
        if (info.PowerW > s.MaxPowerW) return L.T("Leistung {0:F1} W > {1:F0} W", info.PowerW, s.MaxPowerW);
        if (info.InputVoltageMv is { } vin)
        {
            if (s.MinInputVoltageMv is { } min && vin < min) return L.T("Eingangsspannung {0:F0} mV < {1:F0} mV", vin, min);
            if (s.MaxInputVoltageMv is { } max && vin > max) return L.T("Eingangsspannung {0:F0} mV > {1:F0} mV", vin, max);
        }
        if (info.OverheatMode) return L.T("Gerät meldet Überhitzungsschutz");
        if (!string.IsNullOrWhiteSpace(info.PowerFault)) return L.T("Spannungsfehler: {0}", info.PowerFault);
        if (!string.IsNullOrWhiteSpace(info.HardwareFault)) return L.T("Hardwarefehler: {0}", info.HardwareFault);
        return null;
    }

    private StepResult Limit(int freq, int mv, string reason, IReadOnlyList<MinerInfo> samples)
    {
        Write(L.T("⚠ {0} – Test wird sofort beendet.", reason));
        return Aggregate(freq, mv, samples, StepOutcome.LimitExceeded, reason);
    }

    private StepResult Error(int freq, int mv, string reason)
    {
        Write(L.T("Fehler: {0}", reason));
        return new StepResult { FrequencyMhz = freq, CoreVoltageMv = mv, Outcome = StepOutcome.DeviceError, Message = reason };
    }

    private async Task RestoreAsync(BenchmarkSession session)
    {
        // Bewusst ohne das Abbruch-Token: Wiederherstellen muss auch nach "Stopp" passieren.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var s = session.Settings;
        var original = session.Original;
        if (original is null) return;

        int freq = original.FrequencyMhz, mv = original.CoreVoltageMv;
        var label = L.T("ursprüngliche Einstellung");
        var completed = session.IsFinished;
        if (s.RestoreMode == RestoreMode.Best && ResultRanking.Best(session.Results, s.RestoreRanking, s.BalancedHashrateWeight) is { } best)
        {
            // Bei Abbruch/Fehler nur dann die beste Einstellung nehmen, wenn sie vollständig gemessen wurde – das ist sie per Definition.
            (freq, mv) = (best.FrequencyMhz, best.CoreVoltageMv);
            label = L.T("beste Einstellung ({0})", RankingName(s.RestoreRanking));
        }

        Report(BenchmarkPhase.Restoring, session, freq, mv, 0, message: L.T("Setze {0}: {1} MHz / {2} mV", label, freq, mv));
        Write(L.T("Setze {0}: {1} MHz / {2} mV{3}.", label, freq, mv, (completed ? "" : L.T(" (Lauf nicht abgeschlossen)"))));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await client.ApplySettingsAsync(freq, mv, TuningSource.Restore, ct).ConfigureAwait(false);
                if (s.FanMode == FanModeDuringBenchmark.Full && original.AutoFanMode is { } fanMode)
                    await client.SetFanAsync(fanMode, original.FanPercent ?? 100, ct).ConfigureAwait(false);
                if (s.RestartAfterApply)
                    await client.RestartAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is MinerApiException or TaskCanceledException)
            {
                Write(L.T("Wiederherstellen fehlgeschlagen (Versuch {0}/5): {1}", attempt, ex.Message));
                if (ct.IsCancellationRequested) break;
                try { await Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        Write(L.T("⚠ Einstellungen konnten NICHT wiederhergestellt werden – bitte manuell im AxeOS-Webinterface prüfen!"));
    }

    public static string RankingName(RankingMode mode) => mode switch
    {
        RankingMode.MaxHashrate => L.T("max. Hashrate"),
        RankingMode.Efficiency => L.T("beste Effizienz"),
        _ => L.T("Kompromiss"),
    };

    private async Task WaitForDeviceAsync(CancellationToken ct)
    {
        await Delay(RebootInitialDelay, ct).ConfigureAwait(false);
        for (var i = 0; i < RebootMaxPolls; i++)
        {
            if (await TryReadAsync(ct).ConfigureAwait(false) is not null) return;
            await Delay(RebootPollInterval, ct).ConfigureAwait(false);
        }
        throw new MinerApiException(L.T("Gerät ist nach dem Neustart nicht wieder erreichbar."));
    }

    private async Task<MinerInfo?> TryReadAsync(CancellationToken ct)
    {
        try { return await client.GetInfoAsync(ct).ConfigureAwait(false); }
        catch (MinerApiException) { return null; }
    }

    private async Task PausableDelayAsync(BenchmarkSettings s, TimeSpan interval, CancellationToken ct)
    {
        await Delay(interval, ct).ConfigureAwait(false);
        while (_paused)
        {
            // Während der Pause weiter überwachen, aber keine Messzeit verbrauchen.
            if (await TryReadAsync(ct).ConfigureAwait(false) is { } info && CheckLimits(s, info) is not null)
            {
                // Pause aufheben – die nächste Messung erkennt die Grenzverletzung und beendet den Schritt sofort.
                _paused = false;
                Write(L.T("⚠ Grenzwert während der Pause überschritten – Pause wird beendet."));
                return;
            }
            await Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private void Report(BenchmarkPhase phase, BenchmarkSession session, int freq, int mv, double progress,
        MinerInfo? info = null, string? message = null) =>
        Progress?.Report(new BenchmarkProgress
        {
            Phase = phase,
            StepIndex = session.Results.Count + (phase is BenchmarkPhase.Finished or BenchmarkPhase.Cancelled or BenchmarkPhase.Failed ? 0 : 1),
            EstimatedSteps = Math.Max(session.Settings.EstimatedSteps, session.Results.Count + 1),
            FrequencyMhz = freq,
            CoreVoltageMv = mv,
            PhaseProgress = progress,
            Info = info,
            Message = message,
        });

    private void Write(string message) => Log?.Invoke(message);
}
