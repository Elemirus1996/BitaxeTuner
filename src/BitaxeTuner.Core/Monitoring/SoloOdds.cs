using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Core.Monitoring;

public sealed record SoloOddsResult(
    CoinType Coin,
    double? Difficulty,
    double HashrateGh,
    int Miners,
    double? ChancePerDay,
    double? ChancePerYear,
    double? ExpectedDays);

/// <summary>
/// Solo-Wahrscheinlichkeit aus eigener Hashrate und Netzwerk-Difficulty.
///
/// Für einen Block braucht es im Mittel Difficulty × 2^32 Hashes. Bei H Hashes
/// pro Sekunde ist die erwartete Zahl gefundener Blöcke in t Sekunden
/// λ = H·t / (D·2^32), die Wahrscheinlichkeit für mindestens einen Block
/// 1 − e^(−λ). Die Formel hängt nur von der Zeit ab, nicht von der Blockzeit,
/// gilt also für BTC und BCH gleichermaßen.
///
/// Difficulty wird stündlich über Blockchair aktualisiert (48 Requests pro Tag).
/// </summary>
public sealed class SoloOddsService
{
    private const double TwoPow32 = 4294967296.0;
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(1);

    private readonly BlockchairBlockchainService _blockchair;
    private readonly Dictionary<CoinType, (DateTime Fetched, double? Value)> _difficulty = new();

    public SoloOddsService(BlockchairBlockchainService blockchair) => _blockchair = blockchair;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        foreach (var coin in CoinTypeExtensions.WalletCoins)
        {
            lock (_difficulty)
            {
                if (_difficulty.TryGetValue(coin, out var e) && e.Value.HasValue && DateTime.UtcNow - e.Fetched < CacheTime)
                    continue;
            }

            double? value = null;
            try { value = await _blockchair.GetDifficultyAsync(coin, ct); }
            catch { /* bleibt null, nächster Versuch beim nächsten Durchlauf */ }

            lock (_difficulty) _difficulty[coin] = (DateTime.UtcNow, value);
        }
    }

    public SoloOddsResult Calculate(CoinType coin, double hashrateGh, int miners)
    {
        double? difficulty;
        lock (_difficulty) difficulty = _difficulty.TryGetValue(coin, out var e) ? e.Value : null;

        if (difficulty is not > 0 || hashrateGh <= 0)
            return new SoloOddsResult(coin, difficulty, hashrateGh, miners, null, null, null);

        var hashesPerSecond = hashrateGh * 1e9;
        var perDay = hashesPerSecond * 86400 / (difficulty.Value * TwoPow32);

        return new SoloOddsResult(
            coin,
            difficulty,
            hashrateGh,
            miners,
            ChancePerDay: 1 - Math.Exp(-perDay),
            ChancePerYear: 1 - Math.Exp(-perDay * 365),
            ExpectedDays: 1 / perDay);
    }
}
