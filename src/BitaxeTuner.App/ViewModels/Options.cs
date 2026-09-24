using BitaxeTuner.Core.Benchmark;

namespace BitaxeTuner.App.ViewModels;

public sealed record Option<T>(T Value, string Label);

public static class Options
{
    public static IReadOnlyList<Option<RankingMode>> Ranking { get; } =
    [
        new(RankingMode.Balanced, "Kompromiss (Hashrate + Effizienz)"),
        new(RankingMode.MaxHashrate, "Maximale Hashrate"),
        new(RankingMode.Efficiency, "Beste Effizienz (J/TH)"),
    ];

    public static IReadOnlyList<Option<RestoreMode>> Restore { get; } =
    [
        new(RestoreMode.Best, "Beste Einstellung anwenden"),
        new(RestoreMode.Original, "Ursprüngliche Einstellung wiederherstellen"),
    ];

    public static IReadOnlyList<Option<FanModeDuringBenchmark>> Fan { get; } =
    [
        new(FanModeDuringBenchmark.KeepCurrent, "Lüfter unverändert lassen"),
        new(FanModeDuringBenchmark.Full, "Lüfter auf 100 % während des Tests"),
    ];

    public static IReadOnlyList<Option<string>> HeatmapMetrics { get; } =
    [
        new("hashrate", "Hashrate (GH/s)"),
        new("efficiency", "Effizienz (J/TH)"),
        new("temp", "Max. Chiptemperatur (°C)"),
        new("power", "Leistung (W)"),
    ];
}
