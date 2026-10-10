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
        Assert.Contains("/pools/bitcoin.php?page=api&action=getuserworkers&api_key=schluessel", pool.Asked[0]);   // kontoweit, Schlüssel ohne Leerzeichen

        var bal = await client.GetBalanceAsync(CoinType.DigiByte);
        Assert.Equal((CoinType.DigiByte, 12.5m, 0.25m), (bal.Coin, bal.Confirmed, bal.Unconfirmed));
        Assert.Contains("/pools/digibyte.php", pool.Asked[1]);

        var txs = await client.GetTransactionsAsync(CoinType.DigiByte);
        Assert.Equal(2, txs.Count);                                                         // unlesbarer Betrag fällt weg
        Assert.True(txs[0].IsCredit);
        Assert.Equal((101L, 1.5m, 123L), (txs[0].Id, txs[0].Amount, txs[0].Height!.Value));
        Assert.Equal(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc), txs[0].TimeUtc);  // Sommerzeit NL = UTC+2
        Assert.True(txs[1].IsPayout);
        Assert.Equal(10m, txs[1].Amount);                                                   // Auszahlung als positive Menge
        Assert.Null(txs[1].Height);
        Assert.Equal(new DateTime(2026, 1, 15, 7, 30, 0, DateTimeKind.Utc), txs[1].TimeUtc); // Winterzeit = UTC+1
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
}
