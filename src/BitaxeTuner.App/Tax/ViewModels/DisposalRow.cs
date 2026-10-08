using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Tax.ViewModels;

/// <summary>Anzeigezeile eines Verkaufs mit FIFO-Ergebnis.</summary>
public sealed class DisposalRow
{
    public DisposalRow(DisposalResult result, string currency = "EUR")
    {
        Result = result;
        _currency = currency;
    }

    private readonly string _currency;

    public DisposalResult Result { get; }
    public Disposal Disposal => Result.Disposal;

    public DateTime SoldAtLocal => Disposal.SoldAtLocal;
    public string CoinSymbol => Disposal.Coin.Symbol();
    public decimal Amount => Disposal.Amount;
    /// <summary>Erlös in der eingestellten Währung (0.9.11); in anderer Währung erfasst → 0, siehe <see cref="Hint"/>.</summary>
    public decimal ProceedsEur => Disposal.ProceedsIn(_currency) ?? 0;
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
            if (Result.OtherCurrency)
                parts.Add(L.T("Erlös in {0} erfasst – in {1} nicht gerechnet", Disposal.EnteredCurrency, _currency) +
                          $" ({(Disposal.EnteredCurrency == "EUR" ? Disposal.ProceedsEur : Disposal.Proceeds ?? 0).ToString("N2", L.Culture)} {Disposal.EnteredCurrency})");
            return string.Join(" · ", parts);
        }
    }
}
