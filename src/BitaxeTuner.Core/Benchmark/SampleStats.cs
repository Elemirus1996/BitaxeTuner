namespace BitaxeTuner.Core.Benchmark;

public static class SampleStats
{
    /// <summary>
    /// Mittelwert nach Verwerfen der Ausreißer: bei mindestens 10 Werten je 3 höchste und niedrigste,
    /// sonst je einer ab 5 Werten (angelehnt an mrv777/Bitaxe-Hashrate-Benchmark).
    /// </summary>
    public static double TrimmedMean(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return 0;
        var trim = values.Count >= 10 ? 3 : values.Count >= 5 ? 1 : 0;
        var sorted = values.OrderBy(v => v).Skip(trim).Take(values.Count - 2 * trim).ToList();
        return sorted.Count > 0 ? sorted.Average() : values.Average();
    }

    public static double? AverageOrNull(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Average() : null;
    }

    public static double? MaxOrNull(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Max() : null;
    }
}
