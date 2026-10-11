using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.15 Mining-Art-Vergleich: Erwartungswert je Mining-Art aus Hashrate, Difficulty, Kurs und Gebühren.</summary>
public sealed class MiningModesTests
{
    [Fact]
    public void Expected_value_fees_and_solo_chance()
    {
        const double hash = 2000;                                        // 2 TH/s
        var difficulty = hash * 1e9 * 86400 / (0.001 * 4294967296.0);    // → 0,001 Blöcke je Tag
        Assert.Equal(0.001, MiningModes.BlocksPerDay(hash, difficulty), 9);

        var a = new MiningModeAssumptions(PpsFeePercent: 2, PplnsFeePercent: 1, SoloFeePercent: 0, BtcFeesPerBlock: 0.075);
        var rows = MiningModes.Build(hash, difficulty, null, 100_000m, null, costPerDay: 0.5m, a, actualPerDay: 0.2m);

        var pps = rows.Single(r => r.Key == "pps");
        Assert.Equal(0.001 * 3.2 * 0.98, pps.CoinPerDay!.Value, 9);       // (3,125 + 0,075) BTC × 0,98
        Assert.Equal(313.6m, pps.IncomePerDay);
        Assert.Equal(313.1m, pps.NetPerDay);
        Assert.Null(pps.ChancePerMonth);

        var solo = rows.Single(r => r.Key == "solo-btc");
        Assert.Equal(320m, solo.IncomePerDay);                            // ohne Gebühr
        Assert.Equal(1 - Math.Exp(-0.03), solo.ChancePerMonth!.Value, 9);
        Assert.Equal(1000 / 365.25, solo.ExpectedYears!.Value, 6);

        var bch = rows.Single(r => r.Key == "solo-bch");                  // keine Difficulty → keine Werte
        Assert.Null(bch.IncomePerDay);
        Assert.Equal(0.5m, bch.CostPerDay);

        var actual = rows.Single(r => r.Key == "actual");
        Assert.Equal(-0.3m, actual.NetPerDay);
    }

    [Fact]
    public void Mempool_current_difficulty_is_parsed()
    {
        Assert.Equal(132716002350731.3, Core.Network.NetworkClient.ParseCurrentDifficulty("""{"hashrates":[],"currentHashrate":1.0e21,"currentDifficulty":132716002350731.3}"""));
        Assert.Null(Core.Network.NetworkClient.ParseCurrentDifficulty("""{"hashrates":[]}"""));
    }

    [Fact]
    public void Without_price_only_coins_are_shown()
    {
        var rows = MiningModes.Build(1000, 1e14, 1e12, null, null, 0m, new MiningModeAssumptions(), null);
        Assert.All(rows, r => Assert.Null(r.IncomePerDay));
        Assert.All(rows, r => Assert.True(r.CoinPerDay > 0));
        Assert.DoesNotContain(rows, r => r.Key == "actual");
    }
}
