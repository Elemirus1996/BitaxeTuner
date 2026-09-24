namespace BitaxeTuner.Core.Benchmark;

public enum StepOutcome
{
    /// <summary>Hashrate und Fehlerrate im Soll, keine Grenze verletzt.</summary>
    Stable,
    /// <summary>Hashrate zu niedrig oder Fehlerrate zu hoch → mehr Spannung nötig.</summary>
    Unstable,
    /// <summary>Temperatur-, Leistungs- oder Spannungsgrenze überschritten.</summary>
    LimitExceeded,
    /// <summary>Gerät nicht erreichbar, Fehler gemeldet oder zu wenige Messwerte.</summary>
    DeviceError,
    Cancelled,
}

/// <summary>Messergebnis einer Frequenz/Spannungs-Kombination.</summary>
public sealed record StepResult
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int FrequencyMhz { get; init; }
    public int CoreVoltageMv { get; init; }
    public StepOutcome Outcome { get; init; }
    public string? Message { get; init; }

    public int SampleCount { get; init; }
    public double AvgHashRateGh { get; init; }
    public double ExpectedHashRateGh { get; init; }
    public double HashRateRatio => ExpectedHashRateGh > 0 ? AvgHashRateGh / ExpectedHashRateGh : 0;
    public double AvgPowerW { get; init; }
    public double? EfficiencyJth => AvgHashRateGh > 0 && AvgPowerW > 0 ? AvgPowerW / (AvgHashRateGh / 1000.0) : null;
    public double? AvgChipTempC { get; init; }
    public double? MaxChipTempC { get; init; }
    public double? AvgVrTempC { get; init; }
    public double? MaxVrTempC { get; init; }
    public double? AvgErrorPercent { get; init; }
    public double? AvgInputVoltageMv { get; init; }
    public long SharesAccepted { get; init; }
    public long SharesRejected { get; init; }

    public bool IsStable => Outcome == StepOutcome.Stable;

    public string OutcomeText => Outcome switch
    {
        StepOutcome.Stable => "Stabil",
        StepOutcome.Unstable => "Instabil",
        StepOutcome.LimitExceeded => "Grenze",
        StepOutcome.DeviceError => "Fehler",
        StepOutcome.Cancelled => "Abgebrochen",
        _ => Outcome.ToString(),
    };
}
