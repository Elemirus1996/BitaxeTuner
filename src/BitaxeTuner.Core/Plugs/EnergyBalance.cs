using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Core.Plugs;

/// <summary>
/// Leistung für Kosten und Effizienz: AxeOS-Werte der Miner, ersetzt bzw. ergänzt durch Smart-Plug-Messungen.
/// Gilt gleichermaßen für Momentanwerte und Mittelwerte (Tagesbericht).
/// </summary>
/// <param name="MinerPowerW">Summe der AxeOS-Leistung aller Miner.</param>
/// <param name="TotalPowerW">Leistung für Kosten: mit Plugs der Wert an der Steckdose, sonst = MinerPowerW.</param>
/// <param name="FromPlugs">Mindestens ein Plug hat zur Summe beigetragen.</param>
/// <param name="Covered">Miner, deren Verbrauch ein Plug misst.</param>
public sealed record EnergyBalance(double MinerPowerW, double TotalPowerW, bool FromPlugs, IReadOnlySet<string> Covered,
    IReadOnlyDictionary<string, double> MinerWallW)
{
    /// <summary>Netzteilverluste und Nebenverbraucher (Steckdose minus AxeOS), nur mit Plugs.</summary>
    public double? OverheadW => FromPlugs ? TotalPowerW - MinerPowerW : null;

    /// <summary>Leistung eines Miners an der Steckdose (anteilig, wenn ein Plug mehrere Miner speist); null ohne Plug.</summary>
    public double? WallPowerOf(string host) => MinerWallW.TryGetValue(host, out var w) ? w : null;

    /// <summary>
    /// Energiebilanz aus Mittelwerten in history.db (Tagesbericht, Anzeige). Ein Plug zählt nur,
    /// wenn er mindestens die Hälfte des Zeitraums gemessen hat.
    /// </summary>
    public static EnergyBalance FromHistory(Monitoring.HistoryStore history, SmartPlugSettings plugs,
        IReadOnlyDictionary<string, double> minerAvgW, DateTime from, DateTime to)
    {
        var minutes = (to - from).TotalMinutes;
        return Compute(plugs, minerAvgW, p =>
            history.AveragePlugPower(p.Id, from, to) is { } a && a.Minutes >= minutes / 2 ? a.PowerW : null);
    }

    /// <summary>
    /// <paramref name="minerPowerW"/>: AxeOS-Leistung je Miner-Host (nur Miner mit Werten).
    /// <paramref name="plugPowerW"/>: Messwert je Plug oder null (offline, keine Daten).
    /// </summary>
    public static EnergyBalance Compute(SmartPlugSettings settings, IReadOnlyDictionary<string, double> minerPowerW,
        Func<SmartPlugConfig, double?> plugPowerW)
    {
        var minerSum = minerPowerW.Values.Sum();
        var none = new EnergyBalance(minerSum, minerSum, false, new HashSet<string>(), new Dictionary<string, double>());
        if (!settings.UseForCosts || settings.Items.Count == 0) return none;

        var measured = settings.Items.Select(p => (Plug: p, W: plugPowerW(p))).Where(x => x.W is not null).ToList();
        if (measured.Count == 0) return none;

        var perMiner = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plug, w) in measured.Where(x => x.Plug.Role == "miners"))
        {
            var hosts = plug.Miners.Where(h => !covered.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count == 0) continue;
            var axe = hosts.Sum(h => minerPowerW.GetValueOrDefault(h));
            foreach (var h in hosts)
            {
                // anteilig nach AxeOS-Leistung, ohne AxeOS-Werte gleichmäßig
                perMiner[h] = axe > 0 ? w!.Value * minerPowerW.GetValueOrDefault(h) / axe : w!.Value / hosts.Count;
                covered.Add(h);
            }
        }

        var totals = measured.Where(x => x.Plug.Role == "total").Sum(x => x.W!.Value);
        double total;
        if (measured.Any(x => x.Plug.Role == "total"))
        {
            // Gesamtmessung: alles hängt dahinter
            total = totals;
            foreach (var h in minerPowerW.Keys) covered.Add(h);
        }
        else
        {
            var uncovered = minerPowerW.Where(kv => !covered.Contains(kv.Key)).Sum(kv => kv.Value);
            total = perMiner.Values.Sum() + uncovered + measured.Where(x => x.Plug.Role == "other").Sum(x => x.W!.Value);
        }
        return new EnergyBalance(minerSum, total, true, covered, perMiner);
    }
}
