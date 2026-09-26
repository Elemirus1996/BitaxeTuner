using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Ermittelter Kurs mit Herkunft, damit die Bewertung nachvollziehbar bleibt.</summary>
/// <param name="Eur">EUR je Coin.</param>
/// <param name="AtUtc">Zeitpunkt des verwendeten Kurspunkts.</param>
/// <param name="Source">Kurzbeschreibung, landet im CSV-Export.</param>
public sealed record PriceQuote(decimal Eur, DateTime AtUtc, string Source);

public interface IPriceService
{
    /// <summary>
    /// EUR-Kurs möglichst nah am angegebenen Zeitpunkt, oder null.
    /// <paramref name="failureReason"/> erklärt im Fehlerfall, warum.
    /// </summary>
    Task<(PriceQuote? Quote, string? FailureReason)> GetEurPriceAsync(CoinType coin, DateTime atUtc, CancellationToken ct = default);
}
