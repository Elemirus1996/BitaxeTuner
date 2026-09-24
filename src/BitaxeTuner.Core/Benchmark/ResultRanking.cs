namespace BitaxeTuner.Core.Benchmark;

public enum RankingMode
{
    /// <summary>Höchste stabile Hashrate.</summary>
    MaxHashrate,
    /// <summary>Geringster Energieverbrauch pro Terahash (J/TH).</summary>
    Efficiency,
    /// <summary>Gewichteter Kompromiss aus Hashrate und Effizienz.</summary>
    Balanced,
}

public static class ResultRanking
{
    /// <summary>Sortiert alle stabilen Ergebnisse – das beste zuerst.</summary>
    public static IReadOnlyList<StepResult> Rank(IEnumerable<StepResult> results, RankingMode mode, double hashrateWeight = 0.5)
    {
        var stable = results.Where(r => r.IsStable && r.AvgHashRateGh > 0 && r.EfficiencyJth is not null).ToList();
        if (stable.Count == 0) return [];

        return mode switch
        {
            RankingMode.MaxHashrate => stable.OrderByDescending(r => r.AvgHashRateGh).ThenBy(r => r.EfficiencyJth).ToList(),
            RankingMode.Efficiency => stable.OrderBy(r => r.EfficiencyJth).ThenByDescending(r => r.AvgHashRateGh).ToList(),
            _ => stable.OrderByDescending(r => Score(r, stable, hashrateWeight)).ToList(),
        };
    }

    public static StepResult? Best(IEnumerable<StepResult> results, RankingMode mode, double hashrateWeight = 0.5) =>
        Rank(results, mode, hashrateWeight).FirstOrDefault();

    /// <summary>Score 0–1: Hashrate relativ zum Maximum, Effizienz relativ zum Besten.</summary>
    public static double Score(StepResult r, IReadOnlyCollection<StepResult> all, double hashrateWeight)
    {
        var w = Math.Clamp(hashrateWeight, 0, 1);
        var maxHash = all.Max(x => x.AvgHashRateGh);
        var bestEff = all.Where(x => x.EfficiencyJth is not null).Min(x => x.EfficiencyJth!.Value);
        var hashNorm = maxHash > 0 ? r.AvgHashRateGh / maxHash : 0;
        var effNorm = r.EfficiencyJth is { } e and > 0 ? bestEff / e : 0;
        return w * hashNorm + (1 - w) * effNorm;
    }
}
