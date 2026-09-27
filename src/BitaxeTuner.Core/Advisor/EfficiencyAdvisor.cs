using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Core.Advisor;

public enum AdvisorGoal { Efficiency, Balanced, Hashrate }

/// <summary>Eine geprüfte Einstellung aus den Benchmarks, verglichen mit der aktuellen.</summary>
public sealed record AdvisorCandidate(
    AdvisorGoal Goal, int FrequencyMhz, int CoreVoltageMv, double HashrateGh, double PowerW, double Jth,
    bool SoakPassed, double DeltaW, double DeltaGh, double MonthlyCostDelta)
{
    /// <summary>„Dauertest bestanden“ oder „nur Benchmark“.</summary>
    public string Confidence => SoakPassed ? "Dauertest bestanden" : "nur Benchmark";
}

public sealed record AdvisorResult(
    string Host, string Name, int? FrequencyMhz, int? CoreVoltageMv, double? HashrateGh, double? PowerW, double? Jth, string Basis,
    IReadOnlyList<AdvisorCandidate> Candidates, AdvisorCandidate? Recommended, string Note);

/// <summary>
/// Effizienz-Ratgeber: aus stabilen Benchmark-Ergebnissen (innerhalb der Profilgrenzen) und bestandenen Dauertests
/// die beste Einstellung je Ziel (Effizienz, ausgewogen, Hashrate) ermitteln und mit dem aktuellen Betrieb vergleichen –
/// mit erwarteter Änderung von Leistung, Hashrate und Stromkosten pro Monat. Er ändert nie selbst etwas.
/// </summary>
public static class EfficiencyAdvisor
{
    /// <summary>Unter diesen Schwellen lohnt ein Wechsel nicht (Messschwankung).</summary>
    public const double MinEfficiencyGain = 0.03, MinHashrateGain = 0.02;

    public static AdvisorResult Evaluate(string host, string name, DeviceProfile profile,
        int? frequencyMhz, int? coreVoltageMv, double? hashrateGh, double? powerW, string basis,
        IEnumerable<StepResult> results, IEnumerable<SoakResultRecord> soaks, double ctPerKwh, AdvisorGoal goal)
    {
        var soakList = soaks.ToList();
        // Letztes Ergebnis je Einstellung; nur stabile innerhalb der Profilgrenzen; zuletzt im Dauertest durchgefallene raus
        var stable = results
            .Where(r => r.IsStable && r.AvgHashRateGh > 0 && r.EfficiencyJth is not null
                        && r.FrequencyMhz >= profile.MinFrequencyMhz && r.FrequencyMhz <= profile.MaxFrequencyMhz
                        && r.CoreVoltageMv >= profile.MinVoltageMv && r.CoreVoltageMv <= profile.MaxVoltageMv)
            .GroupBy(r => (r.FrequencyMhz, r.CoreVoltageMv))
            .Select(g => g.OrderByDescending(r => r.Timestamp).First())
            .Where(r => LatestSoak(soakList, r.FrequencyMhz, r.CoreVoltageMv) is not { Passed: false })
            .ToList();

        double? jth = hashrateGh is > 1 && powerW is > 0 ? powerW / (hashrateGh / 1000) : null;
        var candidates = new List<AdvisorCandidate>();
        foreach (var g in new[] { AdvisorGoal.Efficiency, AdvisorGoal.Balanced, AdvisorGoal.Hashrate })
        {
            var mode = g switch { AdvisorGoal.Efficiency => RankingMode.Efficiency, AdvisorGoal.Hashrate => RankingMode.MaxHashrate, _ => RankingMode.Balanced };
            var best = ResultRanking.Rank(stable, mode).FirstOrDefault();
            if (best is null) continue;
            var dw = powerW is { } w ? best.AvgPowerW - w : 0;
            var dgh = hashrateGh is { } h ? best.AvgHashRateGh - h : 0;
            candidates.Add(new AdvisorCandidate(g, best.FrequencyMhz, best.CoreVoltageMv, best.AvgHashRateGh, best.AvgPowerW, best.EfficiencyJth!.Value,
                LatestSoak(soakList, best.FrequencyMhz, best.CoreVoltageMv) is { Passed: true },
                dw, dgh, dw * 24 * 30 / 1000.0 * ctPerKwh / 100.0));
        }

        var pick = candidates.FirstOrDefault(c => c.Goal == goal);
        string note;
        AdvisorCandidate? recommended = null;
        if (stable.Count == 0)
            note = "Noch keine stabilen Benchmark-Ergebnisse – erst einen Benchmark laufen lassen.";
        else if (pick is null || frequencyMhz is null || hashrateGh is null || powerW is null)
            note = "Miner gerade nicht erreichbar – Vergleich nicht möglich.";
        else if (pick.FrequencyMhz == frequencyMhz && pick.CoreVoltageMv == coreVoltageMv)
            note = "Die aktuelle Einstellung ist für dieses Ziel bereits die beste.";
        else if (!Worth(goal, pick, hashrateGh.Value, jth))
            note = "Die beste Alternative ist kaum besser als die aktuelle Einstellung – ein Wechsel lohnt nicht.";
        else
        {
            recommended = pick;
            note = pick.SoakPassed
                ? "Diese Einstellung hat bereits einen Dauertest bestanden."
                : "Nur im Benchmark geprüft – nach dem Wechsel einen Dauertest empfehlen.";
        }
        return new AdvisorResult(host, name, frequencyMhz, coreVoltageMv, hashrateGh, powerW, jth, basis, candidates, recommended, note);
    }

    private static bool Worth(AdvisorGoal goal, AdvisorCandidate c, double currentGh, double? currentJth) => goal switch
    {
        AdvisorGoal.Efficiency => currentJth is { } j && c.Jth <= j * (1 - MinEfficiencyGain),
        AdvisorGoal.Hashrate => c.HashrateGh >= currentGh * (1 + MinHashrateGain),
        _ => (currentJth is { } j2 && c.Jth <= j2 * (1 - MinEfficiencyGain)) || c.HashrateGh >= currentGh * (1 + MinHashrateGain),
    };

    private static SoakResultRecord? LatestSoak(List<SoakResultRecord> soaks, int f, int mv) =>
        soaks.Where(s => s.FrequencyMhz == f && s.CoreVoltageMv == mv).OrderByDescending(s => s.Started).FirstOrDefault();
}
