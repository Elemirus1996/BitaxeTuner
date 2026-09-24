namespace BitaxeTuner.Core.Benchmark;

/// <summary>
/// Bestimmt aus den bisherigen Ergebnissen die nächste zu testende Kombination.
/// Zustandslos – dadurch kann ein abgebrochener Lauf einfach fortgesetzt werden.
/// <list type="bullet">
/// <item>Stabil → Frequenz erhöhen (optional vorher Spannung senken).</item>
/// <item>Instabil → Spannung erhöhen, gleiche Frequenz erneut.</item>
/// <item>Grenze überschritten / Gerätefehler → Ende.</item>
/// </list>
/// </summary>
public static class StepPlanner
{
    public readonly record struct Step(int FrequencyMhz, int CoreVoltageMv);

    public static Step? Next(BenchmarkSettings s, IReadOnlyList<StepResult> history, out string? finishReason)
    {
        finishReason = null;
        var last = history.LastOrDefault();
        if (last is null)
            return new Step(s.StartFrequencyMhz, s.StartVoltageMv);

        Step? next;
        switch (last.Outcome)
        {
            case StepOutcome.Cancelled:
                return new Step(last.FrequencyMhz, last.CoreVoltageMv);

            case StepOutcome.LimitExceeded:
                finishReason = $"Grenze erreicht bei {last.FrequencyMhz} MHz / {last.CoreVoltageMv} mV: {last.Message}";
                return null;

            case StepOutcome.DeviceError:
                finishReason = $"Gerätefehler bei {last.FrequencyMhz} MHz / {last.CoreVoltageMv} mV: {last.Message}";
                return null;

            case StepOutcome.Stable:
                if (s.TryLowerVoltage)
                {
                    var lower = last.CoreVoltageMv - s.VoltageStepMv;
                    if (lower >= s.MinVoltageMv && !WasTested(history, last.FrequencyMhz, lower))
                        return new Step(last.FrequencyMhz, lower);
                }
                next = NextFrequency(s, history, last.FrequencyMhz, out finishReason);
                break;

            case StepOutcome.Unstable:
                // Beim Absenken instabil geworden → die zuletzt stabile Spannung ist das Minimum, weiter mit nächster Frequenz.
                if (s.TryLowerVoltage && history.Any(r => r.IsStable && r.FrequencyMhz == last.FrequencyMhz && r.CoreVoltageMv > last.CoreVoltageMv))
                {
                    next = NextFrequency(s, history, last.FrequencyMhz, out finishReason);
                    break;
                }
                var higher = last.CoreVoltageMv + s.VoltageStepMv;
                if (higher > s.MaxVoltageMv)
                {
                    finishReason = $"Maximale Spannung ({s.MaxVoltageMv} mV) erreicht – {last.FrequencyMhz} MHz läuft nicht stabil.";
                    return null;
                }
                next = new Step(last.FrequencyMhz, higher);
                break;

            default:
                return null;
        }

        if (next is { } n && WasTested(history, n.FrequencyMhz, n.CoreVoltageMv))
        {
            finishReason = "Alle sinnvollen Kombinationen getestet.";
            return null;
        }
        return next;
    }

    private static Step? NextFrequency(BenchmarkSettings s, IReadOnlyList<StepResult> history, int currentFreq, out string? finishReason)
    {
        finishReason = null;
        var freq = currentFreq + s.FrequencyStepMhz;
        if (freq > s.MaxFrequencyMhz)
        {
            finishReason = $"Maximale Frequenz ({s.MaxFrequencyMhz} MHz) erreicht.";
            return null;
        }
        var lowestStable = history.Where(r => r.IsStable && r.FrequencyMhz == currentFreq).Min(r => (int?)r.CoreVoltageMv)
            ?? s.StartVoltageMv;
        return new Step(freq, lowestStable);
    }

    private static bool WasTested(IReadOnlyList<StepResult> history, int freq, int mv) =>
        history.Any(r => r.FrequencyMhz == freq && r.CoreVoltageMv == mv && r.Outcome != StepOutcome.Cancelled);
}
