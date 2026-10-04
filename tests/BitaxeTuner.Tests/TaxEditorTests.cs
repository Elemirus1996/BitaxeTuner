using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax;
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

    [Theory]
    [InlineData("65.000", 65000)]
    [InlineData("65.000,50", 65000.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("65000", 65000)]
    [InlineData("12.50", 12.5)]
    [InlineData("12,5 €", 12.5)]
    public void Euro_amounts_understand_thousands_dots(string text, double expected)
    {
        // Audit N-F1: „65.000“ war 65 €
        Assert.True(TaxEditor.TryParseEur(text, out var v));
        Assert.Equal((decimal)expected, v);
        Assert.True(TaxEditor.TryParseDecimal("0.001", out var btc));                  // Coin-Mengen bleiben Dezimalpunkt
        Assert.Equal(0.001m, btc);
    }

    [Fact]
    public void Implausibly_low_manual_price_is_rejected()
    {
        using var dir = new TempDir();
        var (editor, monitor, _) = Create(dir);
        var reward = new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.001m, ReceivedAtUtc = DateTime.UtcNow.AddDays(-3), TxId = "p" };
        monitor.SaveReward(reward);
        Assert.Throws<LocalizedException>(() => editor.UpdateReward(reward.Id, "65", null));
        Assert.Equal(65000m, editor.UpdateReward(reward.Id, "65.000", null).EurPriceAtReceipt);
    }

    [Fact]
    public void Sale_includes_inflows_of_the_same_afternoon()
    {
        // Audit N-F2: Verkauf ist auf 12:00 gelegt – ein Zufluss um 15:00 desselben Tages blieb unberücksichtigt
        using var dir = new TempDir();
        var (editor, monitor, _) = Create(dir);
        var day = TaxTime.ToTax(DateTime.UtcNow).Date.AddDays(-2);
        monitor.SaveReward(new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.001m, ReceivedAtUtc = TaxTime.FromTax(day.AddHours(15)), TxId = "nachmittag", EurPriceAtReceipt = 60000m });
        editor.AddDisposal(CoinType.Bitcoin, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "0,001", "61", null, false);
        var (_, sales, _, _) = editor.Overview(DateTime.Now);
        Assert.Equal(0m, Assert.Single(sales).UnmatchedAmount);
    }

    [Fact]
    public void Csv_text_fields_cannot_become_formulas()
    {
        // Audit N-F3
        Assert.Equal("'=HYPERLINK(1)", TaxLogRepository.Escape("=HYPERLINK(1)"));
        Assert.Equal("'+1", TaxLogRepository.Escape("+1"));
        Assert.Equal("\"'@a;b\"", TaxLogRepository.Escape("@a;b"));
        Assert.Equal("Börse", TaxLogRepository.Escape("Börse"));
    }

    private sealed class FlakyGecko(bool rangeDown) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            if (url.Contains("/range") && rangeDown) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));
            var body = url.Contains("/range") ? """{"prices":[]}""" : """{"market_data":{"current_price":{"eur":61000}}}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Day_snapshot_only_when_the_time_series_really_has_no_point()
    {
        // Audit F6: bei vorübergehendem Fehler nicht sofort den ungenaueren 00:00-Wert speichern
        var down = new FlakyGecko(rangeDown: true);
        using (var gecko = new CoinGeckoPriceService(down))
        {
            var (quote, reason) = await gecko.GetEurPriceAsync(CoinType.Bitcoin, DateTime.UtcNow.AddDays(-2));
            Assert.Null(quote);
            Assert.NotNull(reason);
            Assert.DoesNotContain(down.Urls, u => u.Contains("/history"));
            // Audit N-I2: nach 429 bis zum Ende der Sperre keine weitere Anfrage
            var before = down.Urls.Count;
            await gecko.GetEurPriceAsync(CoinType.Bitcoin, DateTime.UtcNow.AddDays(-1));
            Assert.Equal(before, down.Urls.Count);
        }
        var empty = new FlakyGecko(rangeDown: false);
        using (var gecko = new CoinGeckoPriceService(empty))
        {
            var (quote, _) = await gecko.GetEurPriceAsync(CoinType.Bitcoin, DateTime.UtcNow.AddDays(-2));
            Assert.Equal(61000m, quote!.Eur);
        }
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
