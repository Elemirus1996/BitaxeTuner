using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Mining-Art-Vergleich für alle Miner, die gerade online sind.</summary>
public sealed record MiningModesResult(double HashrateGh, int Miners, string Currency, decimal CostPerDay, double PowerW,
    double? BtcDifficulty, double? BchDifficulty, decimal? BtcPrice, decimal? BchPrice, IReadOnlyList<MiningModeRow> Rows,
    MiningModeAssumptions Assumptions);

/// <summary>0.9.15 Mining-Art-Vergleich: Difficulty (Blockchair, stündlich), Kurse (CoinGecko, stündlich), Strom wie in der Übersicht.</summary>
public sealed partial class MinerHub
{
    public async Task<MiningModesResult> MiningModesAsync(MiningModeAssumptions assumptions, DateTime now)
    {
        var online = Devices.Where(d => d.State.Online && d.State.Info is not null).ToList();
        var hash = online.Sum(d => d.State.Info!.hashRate);
        if (Options.OnlineChecks)
        {
            try { await Odds.RefreshAsync(); } catch { /* ohne Difficulty: Zeilen ohne Wert */ }
            await RefreshPoolPricesAsync([CoinType.Bitcoin, CoinType.BitcoinCash]);
        }
        var cur = Currencies.Of(Config).Code;
        decimal? Price(CoinType c) => _poolPrices.TryGetValue(c.Symbol(), out var p) && p.Currency == cur ? p.Price : null;
        var btcDiff = Odds.Calculate(CoinType.Bitcoin, Math.Max(hash, 1), online.Count).Difficulty;
        var bchDiff = Odds.Calculate(CoinType.BitcoinCash, Math.Max(hash, 1), online.Count).Difficulty;

        var power = online.Count > 0 ? CurrentEnergy().TotalPowerW : 0;
        var ct = Plugs.EnergyCost.CurrentCt(Config, Prices.PriceAt(now.ToUniversalTime()));
        var cost = Math.Round((decimal)(power * 24 / 1000 * ct / 100), 4);

        // Ist-Wert nur mit Pool-Konto: Ø der Gutschriften der letzten 7 Tage (zum aktuellen Kurs)
        decimal? actual = Profit(now) is { Source: "pool" } pf && pf.Window("7d") is { Income: { } inc } ? Math.Round(inc / 7, 4) : null;

        var rows = MiningModes.Build(hash, btcDiff, bchDiff, Price(CoinType.Bitcoin), Price(CoinType.BitcoinCash), cost, assumptions, actual);
        return new MiningModesResult(hash, online.Count, cur, cost, Math.Round(power, 1), btcDiff, bchDiff,
            Price(CoinType.Bitcoin), Price(CoinType.BitcoinCash), rows, assumptions);
    }
}
