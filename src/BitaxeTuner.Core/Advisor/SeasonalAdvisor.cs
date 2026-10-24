using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Core.Advisor;

/// <summary>
/// 0.9.25: Vorschlag des saisonalen Feinschliffs. <see cref="Direction"/>: „down“ = wärmer geworden → sparsamere Einstellung,
/// „up“ = wieder kühler → zurück zur Einstellung von vor dem letzten saisonalen Wechsel. Erwartete Temperatur = Benchmark-
/// Temperatur der neuen Einstellung + heutige Abweichung (<see cref="ShiftC"/>) der aktuellen Einstellung von ihrem Benchmark.
/// </summary>
public sealed record SeasonalSuggestion(string Direction, int FromMhz, int FromMv, int ToMhz, int ToMv,
    double Avg7dC, double ShiftC, double ExpectedC, double FromGh, double ToGh, double FromW, double ToW, bool SoakPassed, DateTime Created);

/// <summary>
/// 0.9.25 Saisonaler Feinschliff: Vergleicht die mittlere Chip-Temperatur der letzten 7 Tage (Einstellung unverändert) mit der
/// Temperatur derselben Einstellung im Benchmark. Ist es dauerhaft deutlich wärmer und nahe an der Warnschwelle, schlägt er die
/// nächstsparsamere geprüfte Einstellung vor; ist es wieder deutlich kühler, die Einstellung von vor dem saisonalen Wechsel.
/// Nur stabile Benchmark-Ergebnisse (im Dauertest durchgefallene nie). Ändert nie selbst etwas.
/// </summary>
public static class SeasonalAdvisor
{
    /// <summary>So viel wärmer/kühler als im Benchmark muss es im Wochenmittel sein.</summary>
    public const double ShiftC = 5;
    /// <summary>„Nahe an der Grenze“: höchstens so weit unter der Warnschwelle.</summary>
    public const double NearLimitC = 5;
    /// <summary>Die sparsamere Einstellung muss voraussichtlich mindestens so viel kühler laufen.</summary>
    public const double MinCoolingC = 2;
    /// <summary>… und mindestens so viel weniger Leistung brauchen.</summary>
    public const double MinPowerDrop = 0.03;

    public static SeasonalSuggestion? Evaluate(DeviceProfile profile, int frequencyMhz, int coreVoltageMv, double avg7dC, double tempWarnC,
        IEnumerable<StepResult> results, IEnumerable<SoakResultRecord> soaks, (int Mhz, int Mv)? seasonalFrom, DateTime now)
    {
        var soakList = soaks.ToList();
        var stable = EfficiencyAdvisor.StableResults(profile, results, soakList).Where(r => r.AvgChipTempC is > 0).ToList();
        var current = stable.FirstOrDefault(r => r.FrequencyMhz == frequencyMhz && r.CoreVoltageMv == coreVoltageMv);
        if (current is null) return null;   // aktuelle Einstellung nie gebenchmarkt – keine Vergleichsbasis
        var shift = avg7dC - current.AvgChipTempC!.Value;
        double Expected(StepResult r) => r.AvgChipTempC!.Value + shift;
        SeasonalSuggestion Make(string dir, StepResult to) => new(dir, frequencyMhz, coreVoltageMv, to.FrequencyMhz, to.CoreVoltageMv,
            avg7dC, shift, Expected(to), current.AvgHashRateGh, to.AvgHashRateGh, current.AvgPowerW, to.AvgPowerW,
            EfficiencyAdvisor.LatestSoak(soakList, to.FrequencyMhz, to.CoreVoltageMv) is { Passed: true }, now);

        if (shift >= ShiftC && avg7dC >= tempWarnC - NearLimitC)
        {
            // die nächstsparsamere: weniger Leistung, voraussichtlich kühler – davon die mit der meisten Hashrate
            var down = stable
                .Where(r => r.AvgPowerW <= current.AvgPowerW * (1 - MinPowerDrop) && Expected(r) <= avg7dC - MinCoolingC)
                .OrderByDescending(r => r.AvgHashRateGh).ThenBy(r => r.AvgPowerW)
                .FirstOrDefault();
            return down is null ? null : Make("down", down);
        }
        if (shift <= -ShiftC && seasonalFrom is { } from && (from.Mhz != frequencyMhz || from.Mv != coreVoltageMv))
        {
            // zurück zur Einstellung von vor dem saisonalen Wechsel – nur wenn sie stabil war und mit Abstand unter der Warnschwelle bleibt
            var back = stable.FirstOrDefault(r => r.FrequencyMhz == from.Mhz && r.CoreVoltageMv == from.Mv);
            if (back is not null && back.AvgHashRateGh > current.AvgHashRateGh && Expected(back) <= tempWarnC - NearLimitC)
                return Make("up", back);
        }
        return null;
    }
}
