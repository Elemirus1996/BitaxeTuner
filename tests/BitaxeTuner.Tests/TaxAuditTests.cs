using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Tests;

/// <summary>Funde F1–F3 aus dem Audit vom 02.10.2026.</summary>
public class TaxAuditTests
{
    private static MinedReward Reward(decimal amount, DateTime at, decimal? price = 50000m) =>
        new() { Coin = CoinType.Bitcoin, Amount = amount, ReceivedAtUtc = at, EurPriceAtReceipt = price, TxId = Guid.NewGuid().ToString("N") };

    [Fact]
    public void Oversold_amount_counts_fully_as_taxable_gain()
    {
        var now = DateTime.UtcNow;
        var rewards = new List<MinedReward> { Reward(0.001m, now.AddDays(-10)) };
        // 0,002 verkauft, nur 0,001 dokumentiert: Erlös 200 € → 100 € gehören zum Zufluss (Kosten 50 €), 100 € ohne Zufluss
        var sale = new Disposal { Coin = CoinType.Bitcoin, Amount = 0.002m, ProceedsEur = 200m, SoldAtUtc = now };
        var r = HoldingCalculator.Apply(rewards, [sale]).Single();
        Assert.Equal(0.001m, r.UnmatchedAmount);
        Assert.Equal(0.002m, r.TaxableAmount);
        Assert.Equal(150m, r.TaxableGainEur);   // (100 − 50) + 100 ohne bekannte Anschaffungskosten
    }

    [Fact]
    public void Csv_export_with_disposals_shows_the_remaining_amount()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        var now = DateTime.UtcNow;
        var rewards = new List<MinedReward> { Reward(0.003m, now.AddDays(-5)) };
        var sale = new Disposal { Coin = CoinType.Bitcoin, Amount = 0.001m, ProceedsEur = 60m, SoldAtUtc = now };
        var file = Path.Combine(dir.Path, "out.csv");
        repo.ExportCsv(file, rewards, [sale]);
        var line = File.ReadAllLines(file)[1];
        Assert.Contains(";0,00200000;", line);   // Restbestand nach dem Verkauf, nicht 0
    }

    [Fact]
    public void Damaged_tax_file_is_never_overwritten_and_reported()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        repo.SaveRewards([Reward(0.001m, DateTime.UtcNow)]);
        File.WriteAllText(repo.RewardsPath, "{ kaputt");
        Assert.Empty(repo.LoadRewards());
        Assert.NotNull(repo.Warning);
        Assert.Throws<InvalidOperationException>(() => repo.SaveRewards([Reward(0.002m, DateTime.UtcNow)]));
        Assert.Equal("{ kaputt", File.ReadAllText(repo.RewardsPath));                // Original unverändert
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(repo.RewardsPath)!, "*.broken-*"));

        // Datei wiederhergestellt → wieder lesbar und speicherbar
        File.WriteAllText(repo.RewardsPath, "[]");
        Assert.Empty(repo.LoadRewards());
        Assert.Null(repo.Warning);
        repo.SaveRewards([Reward(0.002m, DateTime.UtcNow)]);
        Assert.Single(repo.LoadRewards());
    }
}
