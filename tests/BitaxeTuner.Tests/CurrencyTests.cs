using System.Text;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11 „Eine Währung für alles“: Kurse, Steuer-Übersicht und Exporte in der gewählten Währung, Euro-Daten bleiben.</summary>
public class CurrencyTests
{
    /// <summary>Kurs je Währung: 1 BTC = 60 000 EUR = 65 000 USD.</summary>
    private sealed class FakePrices : IPriceService
    {
        public List<string> Asked { get; } = [];
        public bool UsdDown { get; set; }

        public Task<(PriceQuote? Quote, string? FailureReason)> GetPriceAsync(CoinType coin, DateTime atUtc, string currency, CancellationToken ct = default)
        {
            Asked.Add(currency);
            if (currency == "USD" && UsdDown) return Task.FromResult<(PriceQuote?, string?)>((null, "offline"));
            var value = currency == "USD" ? 65000m : 60000m;
            return Task.FromResult<(PriceQuote?, string?)>((new PriceQuote(value, atUtc, "Test " + currency), null));
        }
    }

    private sealed class OneTx : IBlockchainService
    {
        public Task<IReadOnlyList<IncomingTransaction>> GetIncomingTransactionsAsync(string address, CoinType coin, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IncomingTransaction>>([new IncomingTransaction("tx-1", DateTime.UtcNow.AddDays(-2), 0.01m, 900000)]);
    }

    // Beispieladresse aus BIP 173 – keine echte Wallet
    private const string Btc = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

    [Fact]
    public void Currency_is_derived_from_older_settings_and_set_with_its_symbol()
    {
        Assert.Equal("EUR", Currencies.Of(new AppConfig()).Code);
        Assert.Equal("CHF", Currencies.Of(new AppConfig { Currency = "CHF" }).Code);
        Assert.Equal("USD", Currencies.Of(new AppConfig { Currency = "$" }).Code);
        Assert.Equal("EUR", Currencies.Of(new AppConfig { Currency = "Taler" }).Code);   // unbekannt → Euro

        var c = new AppConfig();
        Currencies.Set(c, "gbp");
        Assert.Equal(("GBP", "£"), (c.CurrencyCode, c.Currency));
        Assert.Equal("Strom 30 p/kWh", "Strom 30 ct/kWh".Cents(c));
        Assert.Equal("Strom 30 ct/kWh", "Strom 30 ct/kWh".Cents(new AppConfig()));
        Assert.Equal("eur", Currencies.Get("bogus").GeckoCode);                         // nur bekannte Codes in die Adresse
    }

    [Fact]
    public async Task Monitor_records_euro_and_the_chosen_currency_and_backfills_after_a_switch()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        var prices = new FakePrices { UsdDown = true };
        var currency = "USD";
        using var monitor = new WalletMonitorService(new OneTx(), prices, repo) { Currency = () => currency };
        Assert.True(monitor.AddWallet(new WalletAddress { Address = Btc, Coin = CoinType.Bitcoin, Label = "Test" }));

        await monitor.PollOnceAsync();
        var r = monitor.LoadRewards().Single();
        Assert.Equal(60000m, r.EurPriceAtReceipt);                                      // Euro immer (deutsches Steuerrecht)
        Assert.Null(r.PriceIn("USD"));

        prices.UsdDown = false;
        await monitor.PollOnceAsync();                                                   // fehlender USD-Kurs wird nachgeholt
        r = monitor.LoadRewards().Single();
        Assert.Equal(65000m, r.PriceIn("USD"));
        Assert.Equal(650m, r.ValueIn("USD"));
        Assert.Equal(60000m, r.EurPriceAtReceipt);                                      // Euro-Kurs unverändert

        currency = "CHF";
        await monitor.PollOnceAsync();
        Assert.NotNull(monitor.LoadRewards().Single().PriceIn("CHF"));                  // Wechsel → Kurse der neuen Währung
        Assert.Equal(65000m, monitor.LoadRewards().Single().PriceIn("USD"));            // ältere bleiben
    }

    [Fact]
    public void Fifo_gain_is_computed_in_the_chosen_currency_and_other_currency_sales_are_marked()
    {
        var now = DateTime.UtcNow;
        var reward = new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.01m, ReceivedAtUtc = now.AddDays(-30), EurPriceAtReceipt = 60000m, TxId = "a" };
        reward.SetPrice("USD", 65000m, null, "Test");
        var usdSale = new Disposal { Coin = CoinType.Bitcoin, Amount = 0.005m, SoldAtUtc = now, Currency = "USD", Proceeds = 400m };

        var usd = HoldingCalculator.Apply([reward], [usdSale], "USD").Single();
        Assert.Equal(325m, usd.CostBasisEur);                                           // 0,005 × 65 000
        Assert.Equal(75m, usd.TaxableGainEur);
        Assert.False(usd.OtherCurrency);

        var eur = HoldingCalculator.Apply([reward], [usdSale], "EUR").Single();         // in Euro gerechnet: Erlös unbekannt
        Assert.True(eur.OtherCurrency);
        Assert.Equal(0m, eur.TaxableGainEur);
        Assert.Equal(0.005m, eur.TaxableAmount);                                        // Menge und Haltefrist stimmen trotzdem

        var oldSale = new Disposal { Coin = CoinType.Bitcoin, Amount = 0.005m, SoldAtUtc = now, ProceedsEur = 350m };   // älteres Format
        Assert.Equal(350m, oldSale.ProceedsIn("EUR"));
        Assert.Null(oldSale.ProceedsIn("USD"));
        Assert.Equal(50m, HoldingCalculator.Apply([reward], [oldSale]).Single().TaxableGainEur);
    }

    [Fact]
    public void Editor_writes_prices_and_sales_in_the_chosen_currency_without_touching_euro()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        var monitor = new WalletMonitorService(new OneTx(), new FakePrices(), repo);
        var editor = new TaxEditor(monitor, repo);
        var reward = new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.01m, ReceivedAtUtc = DateTime.UtcNow.AddDays(-3), TxId = "x", EurPriceAtReceipt = 60000m, PriceSource = "CoinGecko" };
        monitor.SaveReward(reward);

        editor.UpdateReward(reward.Id, "64.000 $", null, "USD");
        var stored = monitor.LoadRewards().Single();
        Assert.Equal(64000m, stored.PriceIn("USD"));
        Assert.True(stored.IsManualIn("USD"));
        Assert.Equal(60000m, stored.EurPriceAtReceipt);
        Assert.False(stored.IsManualPrice);                                             // Euro-Kurs bleibt automatisch

        var d = editor.AddDisposal(CoinType.Bitcoin, DateTime.Now.ToString("yyyy-MM-dd"), "0,005", "350", null, false, "USD");
        Assert.Equal(("USD", 350m, 0m), (d.Currency, d.Proceeds, d.ProceedsEur));

        var (_, sales, summary, _) = editor.Overview(DateTime.Now, "USD");
        Assert.Equal("USD", summary.Currency);
        Assert.Equal(640m, summary.RewardEur);                                          // 0,01 × 64 000 (Wert in USD)
        Assert.Equal(30m, sales.Single().TaxableGainEur);                               // 350 − 320
        Assert.True(editor.Overview(DateTime.Now, "EUR").Summary.SaleOtherCurrency);

        // Gespeichertes Format: neue Felder kommen hinzu, ältere Leser sehen die Euro-Felder unverändert
        var json = File.ReadAllText(Directory.GetFiles(dir.Path, "*.json", SearchOption.AllDirectories)
            .First(f => File.ReadAllText(f).Contains("\"OtherPrices\"")));
        Assert.Contains("\"EurPriceAtReceipt\": 60000", json);
    }

    [Fact]
    public void Csv_export_keeps_the_euro_columns_and_adds_the_chosen_currency()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        var reward = new MinedReward { Coin = CoinType.Bitcoin, Amount = 0.01m, ReceivedAtUtc = DateTime.UtcNow.AddDays(-3), TxId = "x", EurPriceAtReceipt = 60000m };
        reward.SetPrice("USD", 65000m, null, "Test USD");
        var file = Path.Combine(dir.Path, "z.csv");

        repo.ExportCsv(file, [reward], currency: "USD");
        var lines = File.ReadAllLines(file, Encoding.UTF8);
        Assert.StartsWith("Datum;Uhrzeit;Zeitpunkt UTC;Coin;Wallet;Blockhöhe;TXID;Menge;EUR-Kurs;EUR-Wert;", lines[0].TrimStart('﻿'));
        Assert.EndsWith(";USD-Kurs;USD-Wert;Kursquelle USD", lines[0]);
        Assert.Contains("65000,00;650,00;Test USD", lines[1]);

        repo.ExportCsv(file, [reward]);                                                  // Euro: Format wie bisher
        Assert.EndsWith("Restbestand;Notiz", File.ReadAllLines(file, Encoding.UTF8)[0]);
    }

    [Fact]
    public async Task CoinGecko_is_asked_in_the_chosen_currency()
    {
        var handler = new RecordingGecko();
        using var gecko = new CoinGeckoPriceService(handler);
        var (quote, _) = await gecko.GetPriceAsync(CoinType.Bitcoin, DateTime.UtcNow.AddDays(-2), "CHF");
        Assert.Equal(55000m, quote!.Value);                                              // Rückfall-Tageswert aus "chf"
        Assert.All(handler.Urls.Where(u => u.Contains("/range")), u => Assert.Contains("vs_currency=chf", u));
        var chart = await gecko.GetDayChartAsync(CoinType.Bitcoin, "GBP");
        Assert.Contains(handler.Urls, u => u.Contains("market_chart?vs_currency=gbp"));
        Assert.Single(chart);
    }

    private sealed class RecordingGecko : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            var body = url.Contains("/range") ? """{"prices":[]}"""
                : url.Contains("/history") ? """{"market_data":{"current_price":{"eur":50000,"chf":55000}}}"""
                : """{"prices":[[1760000000000, 48000.5]]}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
