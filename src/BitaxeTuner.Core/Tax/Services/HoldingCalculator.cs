using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Ergebnis der FIFO-Zuordnung für einen Verkauf.</summary>
public sealed record DisposalResult(
    Disposal Disposal,
    decimal CostBasisEur,
    decimal TaxableGainEur,
    decimal TaxFreeAmount,
    decimal TaxableAmount,
    decimal UnmatchedAmount,
    bool MissingPrice,
    decimal UncertainGainEur = 0)
{
    /// <summary>
    /// Audit F6: Teil von <see cref="TaxableGainEur"/>, der mit 0 € Anschaffungskosten gerechnet ist – Zufluss ohne Kurs
    /// oder verkaufte Menge ohne dokumentierten Zufluss. Mit nachgetragenem Kurs wird er kleiner.
    /// </summary>
    public decimal UncertainGainEur { get; init; } = UncertainGainEur;
}

/// <summary>
/// Ordnet Verkäufe nach FIFO den dokumentierten Zuflüssen desselben Coins zu.
///
/// Anschaffungskosten eines Zuflusses sind sein EUR-Wert zum Zuflusszeitpunkt.
/// Anteile, die mehr als ein Jahr gehalten wurden, gelten als haltefristfrei
/// und gehen nicht in den Gewinn ein. Liegt kein Kurs vor, wird mit 0 €
/// Anschaffungskosten gerechnet und das Ergebnis markiert.
/// Verkaufte Menge ohne dokumentierten Zufluss (<see cref="DisposalResult.UnmatchedAmount"/>): Haltedauer unbekannt,
/// daher vorsichtig als steuerpflichtig mit 0 € Anschaffungskosten gerechnet – der Erlösanteil zählt voll zum Gewinn.
///
/// Rohrechnung für die eigene Übersicht, keine steuerliche Beratung.
/// </summary>
public static class HoldingCalculator
{
    public static List<DisposalResult> Apply(IList<MinedReward> rewards, IEnumerable<Disposal> disposals)
    {
        foreach (var r in rewards) r.Remaining = r.Amount;

        var results = new List<DisposalResult>();

        foreach (var d in disposals.OrderBy(d => d.SoldAtUtc))
        {
            var lots = rewards
                // Audit N-F2: Verkäufe tragen nur ein Datum (gespeichert 12:00) – Zuflüsse desselben Tages gehören dazu,
                // auch wenn sie am Nachmittag kamen (in Steuerzeit verglichen)
                .Where(r => r.Coin == d.Coin && r.Remaining > 0 && r.ReceivedAtLocal.Date <= d.SoldAtLocal.Date)
                .OrderBy(r => r.ReceivedAtUtc)
                .ToList();

            var open = d.Amount;
            decimal costBasis = 0, taxableGain = 0, taxFreeAmount = 0, taxableAmount = 0, uncertain = 0;
            var missingPrice = false;

            foreach (var lot in lots)
            {
                if (open <= 0) break;

                var take = Math.Min(open, lot.Remaining);
                lot.Remaining -= take;
                open -= take;

                var share = d.Amount > 0 ? take / d.Amount : 0;
                var proceeds = d.ProceedsEur * share;
                var cost = take * (lot.EurPriceAtReceipt ?? 0);
                if (lot.EurPriceAtReceipt is null) missingPrice = true;

                costBasis += cost;

                if (d.SoldAtLocal.Date >= lot.TaxFreeFrom)
                {
                    taxFreeAmount += take;
                }
                else
                {
                    taxableAmount += take;
                    taxableGain += proceeds - cost;
                    if (lot.EurPriceAtReceipt is null) uncertain += proceeds;
                }
            }

            if (open > 0 && d.Amount > 0)
            {
                taxableAmount += open;
                taxableGain += d.ProceedsEur * (open / d.Amount);
                uncertain += d.ProceedsEur * (open / d.Amount);
            }

            results.Add(new DisposalResult(d, costBasis, taxableGain, taxFreeAmount, taxableAmount, open, missingPrice, uncertain));
        }

        return results;
    }
}
