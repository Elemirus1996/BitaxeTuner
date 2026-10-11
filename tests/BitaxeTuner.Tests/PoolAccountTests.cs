using System.Net;
using System.Text;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Tests;

/// <summary>0.9.12 Pool-Konto (Mining-Dutch): Antworten lesen, Pool-Buch → Zuflüsse (Gutschrift je Tag oder Auszahlung).</summary>
public class PoolAccountTests
{
    /// <summary>Antwortet je „action“ mit festem Text; merkt sich die abgefragten Adressen.</summary>
    private sealed class FakePool(Func<string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Asked.Add(url);
            var (status, body) = answer(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixedPrices : IPriceService
    {
        public Task<(PriceQuote? Quote, string? FailureReason)> GetPriceAsync(CoinType coin, DateTime atUtc, string currency, CancellationToken ct = default) =>
            Task.FromResult<(PriceQuote?, string?)>((new PriceQuote(coin == CoinType.DigiByte ? 0.01m : 60000m, atUtc, "Test"), null));
    }

    private sealed class NoChain : IBlockchainService
    {
        public Task<IReadOnlyList<IncomingTransaction>> GetIncomingTransactionsAsync(string address, CoinType coin, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IncomingTransaction>>([]);
    }

    private const string Workers = """
        {"getuserworkers":{"version":"1.0.0","runtime":3,"data":{"miners":[
          {"username":"werkbank","nowMining":"digibyte","mergedMining":1,"mode":"PPS","alive":1,"hashrate":"1100000000000","lastshare":1790000000,"difficulty":"4096"},
          {"username":"keller","nowMining":"bitcoin","mergedMining":0,"mode":"PPS","alive":0,"hashrate":0,"lastshare":0,"difficulty":"0"}]}}}
        """;

    private const string Balance = """{"getuserbalance":{"data":{"confirmed":"12.5","unconfirmed":"0.25","orphaned":"0"}}}""";

    private const string Transactions = """
        {"getusertransactions":{"data":{"transactions":[
          {"id":"101","username":"x","type":"Credit","amount":"1.5","timestamp":"2026-07-01 12:00:00","txid":null,"height":"123"},
          {"id":102,"type":"Debit_AC","amount":"-10.0","timestamp":"2026-01-15 08:30:00","txid":"{\"shares\":1}","height":0},
          {"id":103,"type":"Credit","amount":"kaputt","timestamp":"2026-07-01 12:00:00"}],
          "transactionsummary":{"Credit":"11.5"}}}}
        """;

    private static MiningDutchClient Client(FakePool pool) => new("  schluessel ", pool, TimeSpan.Zero);

    [Fact]
    public async Task Client_reads_workers_balance_and_transactions()
    {
        var pool = new FakePool(url => (HttpStatusCode.OK, url.Contains("getuserworkers") ? Workers : url.Contains("getuserbalance") ? Balance : Transactions));
        using var client = Client(pool);

        var workers = await client.GetWorkersAsync();
        Assert.Equal(2, workers.Count);
        var w = workers[0];
        Assert.Equal(("werkbank", true, "digibyte", true, "PPS"), (w.Name, w.Alive, w.NowMining, w.MergedMining, w.Mode));
        Assert.Equal(1.1e12, w.HashrateHs);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime, w.LastShareUtc);
        Assert.False(workers[1].Alive);
        Assert.Null(workers[1].LastShareUtc);
        Assert.Contains("/pools/sha256.php?page=api&action=getuserworkers&api_key=schluessel", pool.Asked[0]);    // SHA-256-Pool (Profit-Wechsel, SOLO), Schlüssel ohne Leerzeichen
        Assert.Contains("/pools/bitcoin.php?page=api&action=getuserworkers", pool.Asked[1]);

        var bal = await client.GetBalanceAsync(CoinType.DigiByte);
        Assert.Equal((CoinType.DigiByte, 12.5m, 0.25m), (bal.Coin, bal.Confirmed, bal.Unconfirmed));
        Assert.Contains("/pools/digibyte.php", pool.Asked[2]);

        var txs = await client.GetTransactionsAsync(CoinType.DigiByte);
        Assert.Equal(2, txs.Count);                                                         // unlesbarer Betrag fällt weg
        Assert.True(txs[0].IsCredit);
        Assert.False(txs[0].FromShares);                                                    // ohne Shares: z. B. Umtausch
        Assert.True(txs[1].FromShares);
        Assert.Equal((101L, 1.5m, 123L), (txs[0].Id, txs[0].Amount, txs[0].Height!.Value));
        Assert.Equal(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc), txs[0].TimeUtc);  // Sommerzeit NL = UTC+2
        Assert.True(txs[1].IsPayout);
        Assert.Equal(10m, txs[1].Amount);                                                   // Auszahlung als positive Menge
        Assert.Null(txs[1].Height);
        Assert.Equal(new DateTime(2026, 1, 15, 7, 30, 0, DateTimeKind.Utc), txs[1].TimeUtc); // Winterzeit = UTC+1
    }

    [Theory]
    [InlineData("PPS", false, false)]
    [InlineData("PPLNS", false, false)]
    [InlineData("A-PROP", false, false)]
    [InlineData("SOLO", true, false)]
    [InlineData("solo mining", true, false)]
    [InlineData("PARTY", false, true)]
    public void Payout_scheme_decides_who_owns_a_found_block(string mode, bool solo, bool party)
    {
        var w = new PoolWorker("w", true, 1e12, "bitcoin", false, mode, null);
        Assert.Equal((solo, party), (w.IsSolo, w.IsParty));
    }

    [Fact]
    public async Task Client_reports_rejected_key_and_unknown_coins()
    {
        using var client = Client(new FakePool(_ => (HttpStatusCode.Unauthorized, "Access denied")));
        var ex = await Assert.ThrowsAsync<LocalizedException>(() => client.GetWorkersAsync());
        Assert.Equal(MiningDutchClient.KeyRejected, ex.Text);
        Assert.Throws<LocalizedException>(() => MiningDutchClient.Slug(CoinType.BitcoinCash));   // BCH gibt es dort nicht

        using var broken = Client(new FakePool(_ => (HttpStatusCode.OK, "<html>")));
        await Assert.ThrowsAsync<LocalizedException>(() => broken.GetBalanceAsync(CoinType.Bitcoin));
        Assert.Null(MiningDutchClient.ParseTime("01.07.2026 12:00"));
    }

    private static PoolTransaction Tx(long id, string type, decimal amount, DateTime utc, CoinType coin = CoinType.DigiByte) =>
        new(id, coin, type, amount, utc, null);

    // 10:00 UTC liegt in Berlin sicher am selben Tag
    private static readonly DateTime Day1 = new(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);

    private static List<PoolTransaction> Ledger() =>
    [
        Tx(1, "Credit", 1m, Day1),
        Tx(2, "Credit", 2m, Day1.AddHours(3)),
        Tx(3, "Credit", 4m, Day1.AddDays(1)),                         // heute – Tag noch nicht abgeschlossen
        Tx(4, "Credit", 0.5m, Day1, CoinType.Namecoin),               // Merged Mining: eigener Coin
        Tx(5, "Debit_AC", 3m, Day1.AddHours(5)),
    ];

    [Fact]
    public void Credits_become_one_inflow_per_coin_and_finished_day_payouts_one_each()
    {
        var now = Day1.AddDays(1).AddHours(2);
        var credits = PoolTaxSync.Derive(Ledger(), PoolIncomeBasis.Credit, now).OrderBy(r => r.TxId).ToList();
        Assert.Equal(2, credits.Count);
        Assert.Equal(("md-credit-DGB-2026-07-01", 3m, Day1.AddHours(3)), (credits[0].TxId, credits[0].Amount, credits[0].ReceivedAtUtc));
        Assert.Equal(("md-credit-NMC-2026-07-01", 0.5m, CoinType.Namecoin), (credits[1].TxId, credits[1].Amount, credits[1].Coin));
        Assert.All(credits, r => Assert.Equal(PoolTaxSync.WalletId, r.WalletAddressId));

        var payouts = PoolTaxSync.Derive(Ledger(), PoolIncomeBasis.Payout, now);
        Assert.Equal(("md-payout-DGB-5", 3m), (payouts.Single().TxId, payouts.Single().Amount));
    }

    [Fact]
    public void Switching_the_basis_parks_entries_and_keeps_manual_prices()
    {
        var now = Day1.AddDays(1).AddHours(2);
        var other = new MinedReward { WalletAddressId = "w1", Coin = CoinType.Bitcoin, TxId = "chain-tx", Amount = 0.1m };
        var rewards = new List<MinedReward> { other };
        var inactive = new List<MinedReward>();
        var none = new HashSet<string>();

        var added = PoolTaxSync.Apply(rewards, inactive, PoolTaxSync.Derive(Ledger(), PoolIncomeBasis.Credit, now), PoolIncomeBasis.Credit, none, out var changed);
        Assert.True(changed);
        Assert.Equal(2, added.Count);
        var dgb = rewards.Single(r => r.TxId == "md-credit-DGB-2026-07-01");
        dgb.SetPrice("EUR", 0.02m, null, "manuell eingetragen");

        // Später kommt noch eine Gutschrift für denselben Tag dazu → Menge nachziehen, Kurs bleibt
        var ledger = Ledger();
        ledger.Add(Tx(6, "Credit", 1m, Day1.AddHours(6)));
        added = PoolTaxSync.Apply(rewards, inactive, PoolTaxSync.Derive(ledger, PoolIncomeBasis.Credit, now), PoolIncomeBasis.Credit, none, out changed);
        Assert.True(changed);
        Assert.Empty(added);
        Assert.Equal(4m, dgb.Amount);
        Assert.Equal(0.02m, dgb.EurPriceAtReceipt);

        // Auf Auszahlung umschalten: Gutschriften wandern in den Speicher, die Auszahlung kommt dazu, fremde Einträge bleiben
        added = PoolTaxSync.Apply(rewards, inactive, PoolTaxSync.Derive(ledger, PoolIncomeBasis.Payout, now), PoolIncomeBasis.Payout, none, out _);
        Assert.Equal("md-payout-DGB-5", added.Single().TxId);
        Assert.Equal(["chain-tx", "md-payout-DGB-5"], rewards.Select(r => r.TxId).Order().ToArray());
        Assert.Equal(2, inactive.Count);

        // Zurück: der manuelle Kurs ist noch da, die Auszahlung wird geparkt
        PoolTaxSync.Apply(rewards, inactive, PoolTaxSync.Derive(ledger, PoolIncomeBasis.Credit, now), PoolIncomeBasis.Credit, none, out _);
        Assert.Equal(0.02m, rewards.Single(r => r.TxId == "md-credit-DGB-2026-07-01").EurPriceAtReceipt);
        Assert.Equal("md-payout-DGB-5", inactive.Single().TxId);
        Assert.Equal(3, rewards.Count);

        // Gelöschte (ignorierte) Einträge kommen nicht wieder
        rewards.RemoveAll(r => r.TxId == "md-credit-NMC-2026-07-01");
        var ignored = new HashSet<string> { TaxLogRepository.TxKey(CoinType.Namecoin, "md-credit-NMC-2026-07-01") };
        added = PoolTaxSync.Apply(rewards, inactive, PoolTaxSync.Derive(ledger, PoolIncomeBasis.Credit, now), PoolIncomeBasis.Credit, ignored, out changed);
        Assert.Empty(added);
        Assert.False(changed);
    }

    [Fact]
    public async Task Monitor_imports_pool_inflows_with_prices_and_parks_them_when_import_is_off()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        using var monitor = new WalletMonitorService(new NoChain(), new FixedPrices(), repo);
        var day = DateTime.UtcNow.Date.AddDays(-3).AddHours(10);
        repo.SavePoolLedger([Tx(1, "Credit", 100m, day), Tx(2, "Credit", 50m, day.AddMinutes(1)), Tx(3, "Debit_AC", 120m, day.AddHours(1))]);
        var ledger = repo.LoadPoolLedger();                                         // Pool-Buch übersteht Speichern/Laden
        Assert.Equal(3, ledger.Count);
        Assert.True(ledger[2].IsPayout);
        var seen = new List<MinedReward>();
        monitor.NewRewardDetected += seen.Add;

        Assert.Equal(1, await monitor.SyncPoolAsync(ledger, PoolIncomeBasis.Credit, import: true));
        var r = monitor.LoadRewards().Single();
        Assert.Equal((150m, 0.01m, CoinType.DigiByte), (r.Amount, r.EurPriceAtReceipt!.Value, r.Coin));
        Assert.Single(seen);
        Assert.Equal(0, await monitor.SyncPoolAsync(ledger, PoolIncomeBasis.Credit, import: true));   // nichts doppelt

        await monitor.SyncPoolAsync(ledger, PoolIncomeBasis.Credit, import: false);
        Assert.Empty(monitor.LoadRewards());
        Assert.Single(repo.LoadPoolInactive());

        Assert.Equal(1, await monitor.SyncPoolAsync(ledger, PoolIncomeBasis.Payout, import: true));
        Assert.Equal("md-payout-DGB-3", monitor.LoadRewards().Single().TxId);
        await monitor.SyncPoolAsync(ledger, PoolIncomeBasis.Credit, import: true);   // zurück: Kurs vom Erstimport bleibt
        Assert.Equal(0.01m, monitor.LoadRewards().Single().EurPriceAtReceipt);
    }

    private static List<PoolTransaction> ExchangeLedger() =>
    [
        Tx(1, "Credit", 100m, Day1) with { FromShares = true },                              // DGB aus dem Mining
        Tx(2, "Credit", 50m, Day1.AddHours(1)) with { FromShares = true },
        Tx(3, "Debit_AC", 150m, Day1.AddHours(2)) with { FromShares = false },               // DGB → BTC umgetauscht
        Tx(4, "Credit", 0.00001m, Day1.AddHours(2), CoinType.Bitcoin) with { FromShares = false },   // BTC aus dem Umtausch
        Tx(5, "Credit", 0.00002m, Day1.AddHours(3), CoinType.Bitcoin) with { FromShares = true },    // BTC aus dem Mining
        Tx(6, "Debit_AC", 0.00003m, Day1.AddHours(4), CoinType.Bitcoin) with { FromShares = false }, // Auszahlung an die Wallet
        new(7, CoinType.Other, "Credit", 45m, Day1.AddHours(5), null, "WJK", "wojakcoin", true),    // weiterer Coin vom Pool
    ];

    [Fact]
    public void Auto_exchange_counts_mined_credits_once_and_books_the_exchange_as_a_swap()
    {
        var now = Day1.AddDays(1).AddHours(2);
        var credit = PoolTaxSync.Derive(ExchangeLedger(), PoolIncomeBasis.Credit, now, autoExchange: true);
        var income = credit.Where(r => !r.IsSwap).OrderBy(r => r.TxId).ToList();
        Assert.Equal(["md-credit-BTC-2026-07-01", "md-credit-DGB-2026-07-01", "md-credit-WJK-2026-07-01"], income.Select(r => r.TxId).ToArray());
        Assert.Equal(0.00002m, income[0].Amount);                                            // nur BTC aus dem Mining
        Assert.Equal((CoinType.Other, "WJK", "WJK"), (income[2].Coin, income[2].CoinCode, income[2].CoinSymbol));
        var swap = credit.Single(r => r.IsSwap);                                             // BTC aus dem Umtausch: Anschaffung
        Assert.Equal(("md-swapin-BTC-4", 0.00001m), (swap.TxId, swap.Amount));
        var sale = PoolTaxSync.DeriveSwapSales(ExchangeLedger(), PoolIncomeBasis.Credit, autoExchange: true).Single();
        Assert.Equal(("md-swapout-DGB-3", CoinType.DigiByte, 150m), (sale.Id, sale.Coin, sale.Amount));

        // Ohne Umtausch-Einstellung (bisheriges Verhalten): alle Gutschriften, keine Verkäufe
        Assert.Equal(0.00003m, PoolTaxSync.Derive(ExchangeLedger(), PoolIncomeBasis.Credit, now).Single(r => r.CoinSymbol == "BTC").Amount);
        Assert.Empty(PoolTaxSync.DeriveSwapSales(ExchangeLedger(), PoolIncomeBasis.Credit, autoExchange: false));

        // Auszahlung: nur die BTC-Auszahlung an die Wallet, der Altcoin-Umtausch zählt nicht
        Assert.Equal("md-payout-BTC-6", PoolTaxSync.Derive(ExchangeLedger(), PoolIncomeBasis.Payout, now, autoExchange: true).Single().TxId);
        Assert.Empty(PoolTaxSync.DeriveSwapSales(ExchangeLedger(), PoolIncomeBasis.Payout, autoExchange: true));
    }

    [Fact]
    public async Task Monitor_books_swaps_prices_pool_coins_and_keeps_swaps_out_of_income()
    {
        using var dir = new TempDir();
        var repo = new TaxLogRepository(dir.Path);
        using var monitor = new WalletMonitorService(new NoChain(), new FixedPrices(), repo);
        var day = DateTime.UtcNow.Date.AddDays(-3).AddHours(10);
        var ledger = ExchangeLedger().Select(t => t with { TimeUtc = day.AddMinutes(t.Id) }).ToList();
        var o = new PoolSyncOptions(PoolIncomeBasis.Credit, true, true, PoolPrice: (sym, cur) => sym == "WJK" ? 0.002m : null);

        Assert.Equal(4, await monitor.SyncPoolAsync(ledger, o));                            // BTC, DGB, WJK + BTC-Anschaffung
        var rewards = monitor.LoadRewards();
        Assert.Equal(0.002m, rewards.Single(r => r.CoinSymbol == "WJK").EurPriceAtReceipt);  // Pool-Kurs (Coin ohne CoinGecko)
        Assert.True(rewards.Single(r => r.TxId == "md-swapin-BTC-4").IsSwap);
        var sale = repo.LoadDisposals().Single();
        Assert.Equal(("md-swapout-DGB-3", 1.5m), (sale.Id, sale.ProceedsEur));                // 150 DGB × 0,01 €

        var editor = new TaxEditor(monitor, repo);
        var (_, sales, summary, available) = editor.Overview(DateTime.Now);
        Assert.Equal(3, summary.RewardCount);                                                // Tausch ist kein Ertrag
        Assert.Equal(0m, available["DGB"]);                                                  // umgetauscht
        Assert.Equal(45m, available["WJK"]);
        Assert.Equal(0m, sales.Single().TaxableGainEur);                                     // Erlös = Anschaffung zum Tageskurs

        // Umtausch-Einstellung aus: Verkauf weg, BTC-Anschaffung geparkt, BTC-Gutschrift wieder mit allem
        await monitor.SyncPoolAsync(ledger, o with { AutoExchange = false });
        Assert.Empty(repo.LoadDisposals());
        Assert.DoesNotContain(monitor.LoadRewards(), r => r.IsSwap);
        Assert.Equal(0.00003m, monitor.LoadRewards().Single(r => r.CoinSymbol == "BTC").Amount);
    }

    [Fact]
    public void Api_key_is_kept_out_of_config_json()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "config.json");
        var cfg = new AppConfig { FilePath = path };
        cfg.PoolAccount.Enabled = true;
        cfg.PoolAccount.ApiKey = "testschluessel-0123456789abcdef";
        cfg.PoolAccount.TaxBasis = PoolIncomeBasis.Payout;
        cfg.PoolAccount.IntervalMinutes = 1;
        cfg.Save();

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("testschluessel", text);
        Assert.Contains("\"Payout\"", text);
        var loaded = AppConfig.Load(path);
        Assert.Equal("testschluessel-0123456789abcdef", loaded.PoolAccount.ApiKey);
        Assert.Equal(PoolIncomeBasis.Payout, loaded.PoolAccount.TaxBasis);
        Assert.Equal(10, loaded.PoolAccount.IntervalMinutes);                       // mindestens 10 min (Pool sperrt sonst)
        Assert.Equal(CoinType.DigiByte, CoinTypeExtensions.FromSymbolOrName("digibyte"));
        Assert.Equal(CoinType.Namecoin, CoinTypeExtensions.FromSymbolOrName("NMC"));
        Assert.Null(CoinTypeExtensions.FromSymbolOrName("dogecoin"));
    }

    private static string PoolTime(DateTime utc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Amsterdam");
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Hub_matches_workers_to_miners_and_keeps_every_booking()
    {
        using var dir = new TempDir();
        var gamma = BitaxeTuner.Core.Profiles.ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Werkbank", Host = "10.0.7.1" });
        config.PoolAccount.Enabled = true;
        config.PoolAccount.ApiKey = "k";
        var day = DateTime.UtcNow.Date.AddDays(-3).AddHours(10);
        var txs = $$$$"""
            {"getusertransactions":{"data":{"transactions":[
              {"id":"7","type":"Credit","amount":"20","timestamp":"{{{{PoolTime(day)}}}}"},
              {"id":"8","type":"Credit","amount":"30","timestamp":"{{{{PoolTime(day.AddHours(2))}}}}"}]}}}
            """;
        var rejected = false;
        var pool = new FakePool(url =>
            rejected ? (HttpStatusCode.Unauthorized, "")
            : url.Contains("getuserworkers") ? (HttpStatusCode.OK, Workers.Replace("werkbank", "worker"))
            : url.Contains("getuserbalance") ? (HttpStatusCode.OK, url.Contains("digibyte") ? Balance : """{"getuserbalance":{"data":{"confirmed":"0","unconfirmed":"0"}}}""")
            : (HttpStatusCode.OK, url.Contains("digibyte") ? txs : """{"getusertransactions":{"data":{"transactions":[]}}}"""));
        using var hub = new BitaxeTuner.Core.Host.MinerHub(config, new BitaxeTuner.Core.Host.MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new BitaxeTuner.Core.Simulation.SimulatedMinerClient(gamma, seed: 3, address: h),
        })
        { PoolClientFactory = key => new MiningDutchClient(key, pool, TimeSpan.Zero) };
        await hub.PollNowAsync();
        var state = hub.Polling.States.Single();
        Assert.Null(hub.PoolWorkerOf(state));                                        // ohne Abfrage kein Worker
        state.Info!.stratumURL = "stratum+tcp://sha256.mining-dutch.nl";
        state.Info.stratumUser = "konto.worker";

        Assert.Null(await hub.PoolAccountTickAsync());
        Assert.Equal(CoinType.DigiByte, hub.PoolCoinOf(state));
        Assert.Equal("PPS", hub.PoolWorkerOf(state)!.Mode);
        state.Info.stratumURL = "public-pool.io";                                    // anderer Pool → keine Zuordnung
        Assert.Null(hub.PoolWorkerOf(state));

        Assert.Equal(2, hub.TaxRepository.LoadPoolLedger().Count);
        var reward = hub.TaxMonitor.LoadRewards().Single();
        Assert.Equal((50m, CoinType.DigiByte), (reward.Amount, reward.Coin));
        var st = hub.PoolAccount;
        Assert.Equal(12.75m, st.Coins.Single(c => c.Coin == CoinType.DigiByte).Confirmed + st.Coins.Single(c => c.Coin == CoinType.DigiByte).Unconfirmed);
        Assert.Equal(50m, st.Coins.Single(c => c.Coin == CoinType.DigiByte).Credits7d);
        Assert.Equal(1, st.NewRewards);

        await hub.PoolAccountTickAsync();                                            // nichts doppelt
        Assert.Equal(2, hub.TaxRepository.LoadPoolLedger().Count);
        Assert.Single(hub.TaxMonitor.LoadRewards());

        rejected = true;                                                             // Schlüssel ungültig → Meldung, Daten bleiben
        Assert.Equal(L.T(MiningDutchClient.KeyRejected), await hub.PoolAccountTickAsync());
        Assert.Equal(2, hub.TaxRepository.LoadPoolLedger().Count);
        Assert.Equal(L.T(MiningDutchClient.KeyRejected), hub.PoolAccount.Error);
    }

    [Fact]
    public async Task Hub_reports_new_payouts_once_and_dead_workers_after_two_rounds()
    {
        using var dir = new TempDir();
        var gamma = BitaxeTuner.Core.Profiles.ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "t" } };
        config.Devices.Add(new DeviceConfig { Name = "Werkbank", Host = "10.0.7.1" });
        config.PoolAccount.Enabled = true;
        config.PoolAccount.ApiKey = "k";
        config.PoolAccount.NotifyCoinSwitch = true;
        var day = DateTime.UtcNow.AddHours(-2);
        var payouts = new List<string> { $$$"""{"id":"1","type":"Debit_AC","amount":"0.001","timestamp":"{{{PoolTime(day)}}}"}""" };
        var alive = 1;
        var coin = "digibyte";
        var pool = new FakePool(url =>
            url.Contains("getuserworkers")
                ? (HttpStatusCode.OK, $$$$"""{"getuserworkers":{"data":{"miners":[{"username":"worker","nowMining":"{{{{coin}}}}","mergedMining":0,"mode":"PPS","alive":{{{{alive}}}},"hashrate":1100000000000,"lastshare":0}]}}}""")
            : url.Contains("getuserbalance") ? (HttpStatusCode.OK, """{"getuserbalance":{"data":{"confirmed":"0","unconfirmed":"0"}}}""")
            : (HttpStatusCode.OK, url.Contains("/bitcoin.php") ? $$$$"""{"getusertransactions":{"data":{"transactions":[{{{{string.Join(",", payouts)}}}}]}}}""" : """{"getusertransactions":{"data":{"transactions":[]}}}"""));
        using var hub = new BitaxeTuner.Core.Host.MinerHub(config, new BitaxeTuner.Core.Host.MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new BitaxeTuner.Core.Simulation.SimulatedMinerClient(gamma, seed: 3, address: h),
        })
        { PoolClientFactory = key => new MiningDutchClient(key, pool, TimeSpan.Zero) };
        var sent = new List<string>();
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        hub.Notify.Sending += (key, _, _, _) => { lock (sent) sent.Add(key); };
        await hub.PollNowAsync();
        var state = hub.Polling.States.Single();
        state.Info!.stratumURL = "stratum+tcp://sha256.mining-dutch.nl";
        state.Info.stratumUser = "konto.worker";

        await hub.PoolAccountTickAsync();                                            // erste Abfrage: alte Auszahlung nicht melden
        Assert.DoesNotContain(sent, k => k.StartsWith("pool-payout"));

        payouts.Add($$$"""{"id":"2","type":"Debit_AC","amount":"0.002","timestamp":"{{{PoolTime(day.AddMinutes(30))}}}"}""");
        alive = 0;
        coin = "wojakcoin";
        await hub.PoolAccountTickAsync();
        Assert.Single(sent, k => k.StartsWith("pool-payout:BTC:2"));
        Assert.DoesNotContain(sent, k => k.StartsWith("pool-dead"));                 // erst nach zwei Abfragen in Folge
        Assert.Contains(sent, k => k.StartsWith("pool-coin:10.0.7.1"));              // Coin-Wechsel (eingeschaltet)

        await hub.PoolAccountTickAsync();
        Assert.Single(sent, k => k.StartsWith("pool-dead:10.0.7.1"));
        Assert.Single(sent, k => k.StartsWith("pool-payout"));                       // nichts doppelt
    }
}
