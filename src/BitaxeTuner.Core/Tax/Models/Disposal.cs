using System.Text.Json.Serialization;

namespace BitaxeTuner.Core.Tax.Models;

/// <summary>Ein Verkauf oder Tausch von geminten Coins.</summary>
public class Disposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CoinType Coin { get; set; }

    /// <summary>Zeitpunkt der Veräußerung, UTC.</summary>
    public DateTime SoldAtUtc { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Erlös in EUR, bei Tausch der Marktwert der erhaltenen Gegenleistung.</summary>
    public decimal ProceedsEur { get; set; }

    /// <summary>
    /// 0.9.11: Währung, in der der Erlös erfasst wurde; null = Euro (<see cref="ProceedsEur"/>). Bei einer anderen Währung
    /// steht der Erlös in <see cref="Proceeds"/> und <see cref="ProceedsEur"/> bleibt 0.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>0.9.11: Erlös in <see cref="Currency"/>, wenn das nicht Euro ist.</summary>
    public decimal? Proceeds { get; set; }

    [JsonIgnore]
    public string EnteredCurrency => string.IsNullOrEmpty(Currency) ? "EUR" : Currency;

    /// <summary>Erlös in der Währung, oder null, wenn er in einer anderen Währung erfasst ist.</summary>
    public decimal? ProceedsIn(string? currency)
    {
        var code = string.IsNullOrEmpty(currency) ? "EUR" : currency.ToUpperInvariant();
        if (!code.Equals(EnteredCurrency, StringComparison.OrdinalIgnoreCase)) return null;
        return code == "EUR" ? ProceedsEur : Proceeds;
    }

    public string Note { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime SoldAtLocal => TaxTime.ToTax(SoldAtUtc);   // Steuer-Zeitzone (Audit F5)
}
