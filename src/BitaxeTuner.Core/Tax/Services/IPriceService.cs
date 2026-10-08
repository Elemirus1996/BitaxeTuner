using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Ermittelter Kurs mit Herkunft, damit die Bewertung nachvollziehbar bleibt.</summary>
/// <param name="Value">Kurs je Coin in der angefragten Währung.</param>
/// <param name="AtUtc">Zeitpunkt des verwendeten Kurspunkts.</param>
/// <param name="Source">Kurzbeschreibung, landet im CSV-Export.</param>
public sealed record PriceQuote(decimal Value, DateTime AtUtc, string Source);

public interface IPriceService
{
    /// <summary>
    /// Kurs in der Währung (ISO-Code, 0.9.11) möglichst nah am angegebenen Zeitpunkt, oder null.
    /// FailureReason erklärt im Fehlerfall, warum.
    /// </summary>
    Task<(PriceQuote? Quote, string? FailureReason)> GetPriceAsync(CoinType coin, DateTime atUtc, string currency, CancellationToken ct = default);

    /// <summary>EUR-Kurs (Steuer nach deutschem Recht).</summary>
    Task<(PriceQuote? Quote, string? FailureReason)> GetEurPriceAsync(CoinType coin, DateTime atUtc, CancellationToken ct = default) =>
        GetPriceAsync(coin, atUtc, "EUR", ct);
}
