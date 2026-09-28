using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.ViewModels;

public sealed record Option<T>(T Value, string Label);

public static class Options
{
    public static IReadOnlyList<Option<RankingMode>> Ranking { get; } =
    [
        new(RankingMode.Balanced, L.T("Kompromiss (Hashrate + Effizienz)")),
        new(RankingMode.MaxHashrate, L.T("Maximale Hashrate")),
        new(RankingMode.Efficiency, L.T("Beste Effizienz (J/TH)")),
    ];

    public static IReadOnlyList<Option<RestoreMode>> Restore { get; } =
    [
        new(RestoreMode.Best, L.T("Beste Einstellung anwenden")),
        new(RestoreMode.Original, L.T("Ursprüngliche Einstellung wiederherstellen")),
    ];

    public static IReadOnlyList<Option<FanModeDuringBenchmark>> Fan { get; } =
    [
        new(FanModeDuringBenchmark.KeepCurrent, L.T("Lüfter unverändert lassen")),
        new(FanModeDuringBenchmark.Full, L.T("Lüfter auf 100 % während des Tests")),
    ];

    public static IReadOnlyList<Option<string>> HeatmapMetrics { get; } =
    [
        new("hashrate", L.T("Hashrate (GH/s)")),
        new("efficiency", L.T("Effizienz (J/TH)")),
        new("temp", L.T("Max. Chiptemperatur (°C)")),
        new("power", L.T("Leistung (W)")),
    ];
}
