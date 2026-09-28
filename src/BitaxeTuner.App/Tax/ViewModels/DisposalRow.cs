using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Tax.ViewModels;

/// <summary>Anzeigezeile eines Verkaufs mit FIFO-Ergebnis.</summary>
public sealed class DisposalRow
{
    public DisposalRow(DisposalResult result) => Result = result;

    public DisposalResult Result { get; }
    public Disposal Disposal => Result.Disposal;

    public DateTime SoldAtLocal => Disposal.SoldAtLocal;
    public string CoinSymbol => Disposal.Coin.Symbol();
    public decimal Amount => Disposal.Amount;
    public decimal ProceedsEur => Disposal.ProceedsEur;
    public decimal CostBasisEur => Result.CostBasisEur;
    public decimal TaxableGainEur => Result.TaxableGainEur;
    public decimal TaxFreeAmount => Result.TaxFreeAmount;
    public string Note => Disposal.Note;

    public string Hint
    {
        get
        {
            var parts = new List<string>();
            if (Result.MissingPrice) parts.Add(L.T("Kurs fehlt bei einem Zufluss"));
            if (Result.UnmatchedAmount > 0) parts.Add(L.T("{0:0.00000000} ohne dokumentierten Zufluss", Result.UnmatchedAmount));
            return string.Join(" · ", parts);
        }
    }
}
