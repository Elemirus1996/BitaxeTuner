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
    bool MissingPrice);

/// <summary>
/// Ordnet Verkäufe nach FIFO den dokumentierten Zuflüssen desselben Coins zu.
///
/// Anschaffungskosten eines Zuflusses sind sein EUR-Wert zum Zuflusszeitpunkt.
/// Anteile, die mehr als ein Jahr gehalten wurden, gelten als haltefristfrei
/// und gehen nicht in den Gewinn ein. Liegt kein Kurs vor, wird mit 0 €
/// Anschaffungskosten gerechnet und das Ergebnis markiert.
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
                .Where(r => r.Coin == d.Coin && r.Remaining > 0 && r.ReceivedAtUtc <= d.SoldAtUtc)
                .OrderBy(r => r.ReceivedAtUtc)
                .ToList();

            var open = d.Amount;
            decimal costBasis = 0, taxableGain = 0, taxFreeAmount = 0, taxableAmount = 0;
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
                }
            }

            results.Add(new DisposalResult(d, costBasis, taxableGain, taxFreeAmount, taxableAmount, open, missingPrice));
        }

        return results;
    }
}
