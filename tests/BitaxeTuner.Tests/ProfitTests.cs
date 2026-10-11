using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Tests;

/// <summary>0.9.14: Ertrag gegen Stromkosten (ohne Pool-Konto aus den dokumentierten Zuflüssen, Tausch zählt nicht).</summary>
public class ProfitTests
{
    [Fact]
    public void Profit_uses_documented_income_without_swaps_per_window()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var now = DateTime.Now;
        MinedReward R(double hoursAgo, decimal amount, decimal? eur, bool swap = false) =>
            new() { Coin = CoinType.Bitcoin, TxId = Guid.NewGuid().ToString("N"), ReceivedAtUtc = now.ToUniversalTime().AddHours(-hoursAgo), Amount = amount, EurPriceAtReceipt = eur, IsSwap = swap };
        hub.TaxRepository.SaveRewards([
            R(2, 0.001m, 60000),           // 60 € in allen Fenstern
            R(48, 0.002m, 50000),          // 100 € ab 7 Tagen
            R(3, 0.01m, 60000, swap: true), // Tausch: kein Ertrag
            R(5, 0.001m, null),            // ohne Kurs → „teilweise“
        ]);

        var p = hub.Profit(now)!;
        Assert.Equal("tax", p.Source);
        var day = p.Window("24h")!;
        Assert.Equal((60m, true), (day.Income!.Value, day.Partial));
        Assert.Equal(day.Income - day.Cost, day.Net);
        Assert.Equal(160m, p.Window("7d")!.Income);
        Assert.Equal(["24h", "7d", "30d", "month"], p.Windows.Select(w => w.Key).ToArray());
        Assert.Same(p, hub.Profit(now));   // 5 min zwischengespeichert
        Assert.Contains("60", hub.ProfitLine(now));
    }
}
