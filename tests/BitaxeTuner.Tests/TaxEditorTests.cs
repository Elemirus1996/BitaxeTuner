using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Tests;

/// <summary>Steuer-Bereich im Browser bearbeiten (0.9.6) – ohne Netz, Wallet-Dienste sind Attrappen.</summary>
public class TaxEditorTests
{
    private sealed class NoChain : IBlockchainService
    {
        public Task<IReadOnlyList<IncomingTransaction>> GetIncomingTransactionsAsync(string address, CoinType coin, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IncomingTransaction>>([]);
    }

    private sealed class NoPrices : IPriceService
    {
        public Task<(PriceQuote? Quote, string? FailureReason)> GetEurPriceAsync(CoinType coin, DateTime atUtc, CancellationToken ct = default) =>
            Task.FromResult<(PriceQuote?, string?)>((null, "offline"));
    }

    // Beispieladressen aus BIP 173 bzw. der CashAddr-Spezifikation – keine echten Wallets
    private const string Btc = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    private const string Bch = "bitcoincash:qpm2qsznhks23z7629mms6s4cwef74vcwvy22gdx6a";

    private static (TaxEditor Editor, WalletMonitorService Monitor, TaxLogRepository Repo) Create(TempDir dir)
    {
        var repo = new TaxLogRepository(dir.Path);
        var monitor = new WalletMonitorService(new NoChain(), new NoPrices(), repo);
        return (new TaxEditor(monitor, repo), monitor, repo);
    }

    [Fact]
    public void Wallets_can_be_added_edited_and_removed()
    {
        using var dir = new TempDir();
        var (editor, monitor, repo) = Create(dir);

        var w = editor.AddWallet(" " + Btc + " ", null, "");
        Assert.Equal(CoinType.Bitcoin, w.Coin);                                    // aus dem Adressformat
        Assert.Equal("BTC-Wallet", w.Label);
        Assert.Equal(CoinType.BitcoinCash, editor.AddWallet(Bch, null, "Keller").Coin);
        Assert.Equal(400, Assert.Throws<LocalizedException>(() => editor.AddWallet(Btc.ToUpperInvariant(), null, null)).Status);   // doppelt
        Assert.Throws<LocalizedException>(() => editor.AddWallet("kurz", null, null));

        editor.UpdateWallet(w.Id, "Hauptwallet", null);
        Assert.Equal("Hauptwallet", repo.LoadWallets().Single(x => x.Id == w.Id).Label);   // gespeichert

        editor.RemoveWallet(w.Id);
        Assert.Single(monitor.Wallets);
        Assert.Equal(404, Assert.Throws<LocalizedException>(() => editor.RemoveWallet(w.Id)).Status);
    }

    [Fact]
    public void Miner_addresses_are_taken_over_once()
    {
        using var dir = new TempDir();
        var (editor, monitor, _) = Create(dir);
        var miners = new List<(string, string, string)> { ("Gamma", "h1", Btc), ("Alt", "h2", "1BoatSLRHtKNngkdXEeobR76b53LETtpyT"), ("Leer", "h3", "") };
        var (added, skipped, ambiguous) = editor.ImportFromMiners(miners);
        Assert.Equal((2, 0), (added, skipped));
        Assert.Equal(["Alt"], ambiguous);                                         // Legacy-Adresse: Coin nur angenommen
        Assert.Equal("Gamma", monitor.Wallets.Single(w => w.SourceDeviceHost == "h1").Label);
        Assert.Equal((0, 2), (editor.ImportFromMiners(miners).Added, editor.ImportFromMiners(miners).Skipped));
    }

    [Fact]
    public void Reward_price_becomes_manual_and_removed_rewards_stay_ignored()
    {
        using var dir = new TempDir();
        var (editor, monitor, repo) = Create(dir);
        var reward = new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.001m, ReceivedAtUtc = DateTime.UtcNow.AddDays(-3), TxId = "tx1", PriceSource = "CoinGecko" };
        monitor.SaveReward(reward);

        editor.UpdateReward(reward.Id, "58.250,50", "Blockfund");
        var stored = monitor.LoadRewards().Single();
        Assert.Equal(58250.50m, stored.EurPriceAtReceipt);
        Assert.True(stored.IsManualPrice);
        Assert.Equal("Blockfund", stored.Note);

        editor.UpdateReward(reward.Id, null, "nur Notiz");                       // Kurs unverändert
        Assert.Equal(58250.50m, monitor.LoadRewards().Single().EurPriceAtReceipt);
        Assert.Throws<LocalizedException>(() => editor.UpdateReward(reward.Id, "-5", null));

        editor.RemoveReward(reward.Id);
        Assert.Empty(monitor.LoadRewards());
        Assert.Contains(TaxLogRepository.TxKey(CoinType.Bitcoin, "tx1"), repo.LoadKnownTxKeys());   // kommt nicht wieder
    }

    [Fact]
    public void Sales_use_fifo_and_overselling_needs_confirmation()
    {
        using var dir = new TempDir();
        var (editor, monitor, repo) = Create(dir);
        monitor.SaveReward(new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.002m, ReceivedAtUtc = DateTime.UtcNow.AddDays(-30), TxId = "a", EurPriceAtReceipt = 50000m });
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var d = editor.AddDisposal(CoinType.Bitcoin, today, "0,001", "60", "Börse", false);
        Assert.Equal(12, d.SoldAtLocal.Hour);
        var (rewards, sales, summary, available) = editor.Overview(DateTime.Now);
        Assert.Equal(0.001m, rewards.Single().Remaining);
        Assert.Equal(10m, sales.Single().TaxableGainEur);                        // 60 − 50
        Assert.Equal(0.001m, available["BTC"]);
        Assert.Equal(1, summary.SaleCount);

        var ex = Assert.Throws<LocalizedException>(() => editor.AddDisposal(CoinType.Bitcoin, today, "0.005", "300", null, false));
        Assert.Equal(409, ex.Status);
        Assert.Single(repo.LoadDisposals());                                      // nichts gespeichert
        editor.AddDisposal(CoinType.Bitcoin, today, "0.005", "300", null, true);
        Assert.Equal(2, repo.LoadDisposals().Count);

        Assert.Throws<LocalizedException>(() => editor.AddDisposal(CoinType.Bitcoin, DateTime.Now.AddDays(2).ToString("yyyy-MM-dd"), "1", "1", null, true));
        Assert.Throws<LocalizedException>(() => editor.AddDisposal(CoinType.Bitcoin, today, "abc", "1", null, true));

        editor.RemoveDisposal(d.Id);
        Assert.Single(repo.LoadDisposals());
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("0,00012345", 0.00012345)]
    [InlineData("58 000 €", 58000)]
    public void Numbers_accept_german_and_english_notation(string text, double expected)
    {
        Assert.True(TaxEditor.TryParseDecimal(text, out var v));
        Assert.Equal((decimal)expected, v);
    }
}
