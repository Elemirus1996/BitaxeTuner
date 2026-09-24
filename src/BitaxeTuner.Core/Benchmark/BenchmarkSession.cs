namespace BitaxeTuner.Core.Benchmark;

/// <summary>Einstellungen des Geräts vor dem Benchmark – werden am Ende bzw. bei Abbruch wiederhergestellt.</summary>
public sealed record OriginalSettings(int FrequencyMhz, int CoreVoltageMv, int? AutoFanMode, int? FanPercent);

/// <summary>Ein kompletter Benchmark-Lauf für ein Gerät (wird nach jedem Schritt gespeichert und kann fortgesetzt werden).</summary>
public sealed class BenchmarkSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DeviceAddress { get; set; } = "";
    public string? Hostname { get; set; }
    public string? DeviceModel { get; set; }
    public string? ProfileId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public string? FinishReason { get; set; }
    public BenchmarkSettings Settings { get; set; } = new();
    public OriginalSettings? Original { get; set; }
    public List<StepResult> Results { get; set; } = [];

    public bool IsFinished => FinishedAt is not null;
}

public enum BenchmarkPhase
{
    Idle,
    Preparing,
    Applying,
    Restarting,
    WarmingUp,
    Measuring,
    Restoring,
    Finished,
    Failed,
    Cancelled,
}

public sealed record BenchmarkProgress
{
    public BenchmarkPhase Phase { get; init; }
    public int StepIndex { get; init; }
    public int EstimatedSteps { get; init; }
    public int FrequencyMhz { get; init; }
    public int CoreVoltageMv { get; init; }
    /// <summary>Fortschritt innerhalb der aktuellen Phase (0–1).</summary>
    public double PhaseProgress { get; init; }
    public Api.MinerInfo? Info { get; init; }
    public StepResult? CompletedStep { get; init; }
    public string? Message { get; init; }
}
