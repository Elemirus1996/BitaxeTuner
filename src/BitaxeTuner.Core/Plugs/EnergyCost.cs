using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Plugs;

/// <summary>Energie und Kosten eines Zeitraums. DynamicHours = Stunden mit Preis der Quelle (Rest: fester ct/kWh-Wert).</summary>
public sealed record CostResult(double Kwh, double Cost, int Hours, int DynamicHours)
{
    /// <summary>Durchschnittspreis in ct/kWh (gewichtet nach Energie).</summary>
    public double? AvgCt => Kwh > 0 ? Cost / Kwh * 100 : null;
}

/// <summary>
/// Stromkosten stundenweise: Energie je Stunde (Miner aus Minutenwerten, mit Smart Plugs wie in der Energiebilanz)
/// mal Preis der Stunde (+ Aufschlag). Fehlt ein Preis, gilt der feste ct/kWh-Wert.
/// </summary>
public static class EnergyCost
{
    /// <summary>Volle Stunden: [floor(from), floor(to)).</summary>
    public static CostResult Compute(HistoryStore history, AppConfig config, IReadOnlyCollection<string> hosts, DateTime from, DateTime to)
    {
        var start = Floor(from);
        var end = Floor(to);
        var dynamic = config.PriceSource.DynamicCosts;
        var prices = dynamic ? history.HourlyPrices(start, end) : [];
        var miners = hosts.ToDictionary(h => h, h => history.HourlyEnergyWh(h, start, end), StringComparer.OrdinalIgnoreCase);
        var plugs = config.Plugs.Items.ToDictionary(p => p.Id, p => history.HourlyPlugPower(p.Id, start, end));

        double kwh = 0, cost = 0;
        int hours = 0, dynHours = 0;
        for (var t = start; t < end; t = t.AddHours(1))
        {
            var key = new DateTimeOffset(t).ToUnixTimeSeconds();
            var minerWh = miners.Where(kv => kv.Value.ContainsKey(key)).ToDictionary(kv => kv.Key, kv => kv.Value[key], StringComparer.OrdinalIgnoreCase);
            // Wh einer Stunde = mittlere Leistung in dieser Stunde; ein Plug zählt ab 30 Messminuten
            var balance = EnergyBalance.Compute(config.Plugs, minerWh,
                p => plugs[p.Id].TryGetValue(key, out var v) && v.Count >= 30 ? v.Value : null);
            var hourKwh = balance.TotalPowerW / 1000.0;
            if (hourKwh <= 0) continue;
            hours++;
            double ct;
            if (dynamic && prices.TryGetValue(key, out var p))
            {
                ct = p + config.PriceSource.SurchargeCt;
                dynHours++;
            }
            else ct = config.ElectricityCtPerKwh;
            kwh += hourKwh;
            cost += hourKwh * ct / 100.0;
        }
        return new CostResult(kwh, cost, hours, dynHours);
    }

    /// <summary>Aktueller Preis für „Kosten pro Tag“: Stundenpreis + Aufschlag, sonst der feste Wert.</summary>
    public static double CurrentCt(AppConfig config, double? sourcePriceCt) =>
        config.PriceSource.DynamicCosts && sourcePriceCt is { } p ? p + config.PriceSource.SurchargeCt : config.ElectricityCtPerKwh;

    private static DateTime Floor(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
}
