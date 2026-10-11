namespace BitaxeTuner.Core.Monitoring;

/// <summary>Annahmen für den Mining-Art-Vergleich (alle anpassbar in der Oberfläche).</summary>
/// <param name="PpsFeePercent">Gebühr bei PPS/FPPS (der Pool zahlt den Erwartungswert, abzüglich Gebühr).</param>
/// <param name="PplnsFeePercent">Gebühr bei PPLNS/PROP (Auszahlung nach Glück des Pools).</param>
/// <param name="SoloFeePercent">Gebühr eines Solo-Pools vom Blockfund (z. B. 0–2 %).</param>
/// <param name="BtcFeesPerBlock">Geschätzte Transaktionsgebühren je BTC-Block (zusätzlich zur Blockprämie).</param>
public sealed record MiningModeAssumptions(double PpsFeePercent = 2.0, double PplnsFeePercent = 1.0, double SoloFeePercent = 1.0,
    double BtcFeesPerBlock = 0.03, double BchFeesPerBlock = 0.002);

/// <summary>Eine Mining-Art. Income/Net je Tag in der gewählten Währung; null = Kurs oder Difficulty fehlt.</summary>
/// <param name="Key">„pps“, „pplns“, „solo-btc“, „solo-bch“, „actual“.</param>
/// <param name="CoinPerDay">Erwartete Coins je Tag (bei „actual“ null).</param>
/// <param name="ChancePerMonth">Solo: Wahrscheinlichkeit für mindestens einen Block in 30 Tagen.</param>
/// <param name="ExpectedYears">Solo: im Mittel ein Block alle … Jahre.</param>
public sealed record MiningModeRow(string Key, string Coin, double? CoinPerDay, decimal? IncomePerDay, decimal CostPerDay, decimal? NetPerDay,
    double? ChancePerMonth, double? ExpectedYears);

/// <summary>
/// 0.9.15 Mining-Art-Vergleich: Was bringt dieselbe Hashrate bei PPS, PPLNS oder Solo (BTC, BCH) – und was hat dein Pool-Konto
/// tatsächlich gutgeschrieben? Erwartungswert je Tag = Hashrate · 86 400 / (Difficulty · 2³²) · (Blockprämie + Gebühren).
/// Bei PPS kommt er Tag für Tag (abzüglich Gebühr), bei PPLNS schwankt er mit dem Glück des Pools, bei Solo kommt er
/// als ganzer Block – oder gar nicht.
/// </summary>
public static class MiningModes
{
    /// <summary>Blockprämie seit dem Halving im April 2024 (BTC ab Block 840 000, BCH ab 842 000). Nächstes Halving ca. 2028.</summary>
    public const double Subsidy = 3.125;
    private const double TwoPow32 = 4294967296.0;

    /// <summary>Erwartete Blöcke je Tag bei dieser Hashrate.</summary>
    public static double BlocksPerDay(double hashrateGh, double difficulty) =>
        difficulty > 0 ? hashrateGh * 1e9 * 86400 / (difficulty * TwoPow32) : 0;

    /// <param name="actualPerDay">Tatsächlicher Ertrag je Tag laut Pool-Konto (Ø der letzten 7 Tage), null = kein Pool-Konto.</param>
    public static List<MiningModeRow> Build(double hashrateGh, double? btcDifficulty, double? bchDifficulty, decimal? btcPrice, decimal? bchPrice,
        decimal costPerDay, MiningModeAssumptions a, decimal? actualPerDay)
    {
        var rows = new List<MiningModeRow>();
        MiningModeRow Row(string key, string coin, double? difficulty, decimal? price, double feesPerBlock, double feePercent)
        {
            if (difficulty is not > 0 || hashrateGh <= 0)
                return new MiningModeRow(key, coin, null, null, costPerDay, null, null, null);
            var blocks = BlocksPerDay(hashrateGh, difficulty.Value);
            var coins = blocks * (Subsidy + feesPerBlock) * (1 - Math.Clamp(feePercent, 0, 100) / 100);
            decimal? income = price is { } p ? Math.Round((decimal)coins * p, 4) : null;
            var solo = key.StartsWith("solo", StringComparison.Ordinal);
            return new MiningModeRow(key, coin, coins, income, costPerDay, income is { } i ? Math.Round(i - costPerDay, 4) : null,
                solo ? 1 - Math.Exp(-blocks * 30) : null, solo && blocks > 0 ? 1 / blocks / 365.25 : null);
        }
        rows.Add(Row("pps", "BTC", btcDifficulty, btcPrice, a.BtcFeesPerBlock, a.PpsFeePercent));
        rows.Add(Row("pplns", "BTC", btcDifficulty, btcPrice, a.BtcFeesPerBlock, a.PplnsFeePercent));
        rows.Add(Row("solo-btc", "BTC", btcDifficulty, btcPrice, a.BtcFeesPerBlock, a.SoloFeePercent));
        rows.Add(Row("solo-bch", "BCH", bchDifficulty, bchPrice, a.BchFeesPerBlock, a.SoloFeePercent));
        if (actualPerDay is { } act)
            rows.Add(new MiningModeRow("actual", "", null, Math.Round(act, 4), costPerDay, Math.Round(act - costPerDay, 4), null, null));
        return rows;
    }
}
