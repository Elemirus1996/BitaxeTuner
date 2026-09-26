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

    public string Note { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime SoldAtLocal => SoldAtUtc.ToLocalTime();
}
