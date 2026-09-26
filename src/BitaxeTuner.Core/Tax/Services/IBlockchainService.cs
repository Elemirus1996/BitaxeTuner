using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Eingehende Transaktion an eine überwachte Adresse.</summary>
public record IncomingTransaction(string TxId, DateTime ReceivedAtUtc, decimal Amount, long? BlockId);

public interface IBlockchainService
{
    /// <summary>
    /// Liefert die jüngsten eingehenden Transaktionen (positive Bilanzänderung)
    /// für die Adresse. Nur bestätigte Transaktionen.
    /// </summary>
    Task<IReadOnlyList<IncomingTransaction>> GetIncomingTransactionsAsync(string address, CoinType coin, CancellationToken ct = default);
}
