using System.Net;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.20: weitere Pool-Konten (public-pool, solo.ckpool, OCEAN, Braiins, ViaBTC) – nur lesend.</summary>
public sealed class ExtraPoolTests : IDisposable
{
    private const string Addr = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";   // Genesis-Adresse (öffentlich bekannt)
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly TempDir _dir = new();

    private sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (code, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    public void Dispose() => _dir.Dispose();

    private static ExtraPoolClient Client(Fake f) => new(new HttpClient(f)) { Pause = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task Public_pool_and_ckpool_show_hashrate_workers_and_best_difficulty()
    {
        // Antworten wie live am 16.10.2026 abgerufen (gekürzt)
        var f = new Fake(r => r.RequestUri!.Host.Contains("public-pool")
            ? (HttpStatusCode.OK, """{"bestDifficulty":"11824155153.809406","workersCount":1,"workers":[{"sessionId":"8fee46e5","name":"worker","bestDifficulty":"8.82","hashRate":205973956636.85,"startTime":"2026-10-15T03:46:14.418Z","lastSeen":"2026-10-16T11:58:00.000Z"}]}""")
            : (HttpStatusCode.OK, """{"hashrate1m":"113M","hashrate5m":"1.2T","hashrate1hr":"64.7M","hashrate1d":"35.5G","lastshare":1792166400,"workers":2,"bestshare":5704276694.06,"bestever":9474970254,"worker":[{"workername":"ADDR.bitaxe","hashrate5m":"1.2T","lastshare":1792166400,"bestever":9474970254},{"workername":"ADDR.bitaxe2","hashrate5m":"0","lastshare":1792000000,"bestever":12}]}"""));
        var pp = await Client(f).FetchAsync(new ExtraPoolAccount { Provider = "public-pool", Address = Addr }, null, Now);
        Assert.Equal(11824155153.809406, pp.BestDifficulty!.Value, 3);
        Assert.Equal(205.97, pp.HashrateGh!.Value, 1);
        Assert.Equal("worker", Assert.Single(pp.Workers).Name);
        Assert.EndsWith($"/api/client/{Addr}", f.Requests[0].RequestUri!.ToString());

        var ck = await Client(f).FetchAsync(new ExtraPoolAccount { Provider = "ckpool", Address = Addr }, null, Now);
        Assert.Equal(1200, ck.HashrateGh!.Value, 3);
        Assert.Equal(1, ck.ActiveWorkers);
        Assert.Equal(["bitaxe", "bitaxe2"], ck.Workers.Select(w => w.Name));
        Assert.Equal(9474970254, ck.BestDifficulty);
        Assert.Equal(113e6, ExtraPoolClient.Suffixed("113M"));
    }

    [Fact]
    public async Task Ocean_braiins_and_viabtc_deliver_earnings_and_payouts()
    {
        var f = new Fake(r =>
        {
            var u = r.RequestUri!.ToString();
            if (u.Contains("/statsnap/")) return (HttpStatusCode.OK, """{"result":{"hashrate_300s":"1200000000000","unpaid":"0.00001854"}}""");
            if (u.Contains("/user_hashrate_full/")) return (HttpStatusCode.OK, """{"result":{"hashrate_86400s":"1100000000000","workers":{"bitaxe":[{"hashrate_60s":"1200000000000","hashrate_300s":"1150000000000"}]}}}""");
            if (u.Contains("/earnpay/")) return (HttpStatusCode.OK, """{"result":{"earnings":[{"ts":"2026-10-15T06:27:52","block_hash":"000000000000000000021c220015ff95","satoshis_net_earned":1234}],"payouts":[{"ts":"2026-10-15T07:00:00","on_chain_txid":"abc","total_satoshis_net_paid":5000}]}}""");
            if (u.Contains("/accounts/profile/")) return (HttpStatusCode.OK, """{"btc":{"current_balance":"0.00012000","hash_rate_unit":"Gh/s","hash_rate_5m":1250.5,"hash_rate_24h":1200,"ok_workers":1}}""");
            if (u.Contains("/accounts/rewards/")) return (HttpStatusCode.OK, """{"btc":{"daily_rewards":[{"date":1791936000,"total_reward":"0.00000321"},{"date":1792022400,"total_reward":"0.00000100"},{"date":1792108800,"total_reward":"0.00000050"}]}}""");
            if (u.Contains("/accounts/payouts/")) return (HttpStatusCode.OK, """{"btc":{"onchain":[{"status":"confirmed","amount_sats":20000,"resolved_at_ts":1792000000,"tx_id":"t1"},{"status":"queued","amount_sats":1,"requested_at_ts":1792100000}],"lightning":[]}}""");
            if (u.Contains("/accounts/workers/")) return (HttpStatusCode.OK, """{"btc":{"workers":{"user.bitaxe":{"state":"ok","last_share":1792160000,"hash_rate_unit":"Gh/s","hash_rate_5m":1250.5}}}}""");
            if (u.Contains("/hashrate")) return (HttpStatusCode.OK, """{"code":0,"data":{"active_workers":1,"coin":"BTC","hashrate_10min":"747667906887","hashrate_24hour":"700000000000"},"message":"OK"}""");
            if (u.Contains("/profit/history")) return (HttpStatusCode.OK, """{"code":0,"data":{"data":[{"coin":"BTC","date":"2026-10-15","total_profit":"0.00002148"},{"coin":"BTC","date":"2026-10-16","total_profit":"0.00000500"}]},"message":"OK"}""");
            if (u.Contains("/wallet/payment/history")) return (HttpStatusCode.OK, """{"code":0,"data":{"data":[{"id":7,"coin":"BTC","amount":"0.001","tx":"tx7","create_time":1792000000}]},"message":"OK"}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var ocean = await Client(f).FetchAsync(new ExtraPoolAccount { Provider = "ocean", Address = Addr }, null, Now);
        Assert.Equal(1200, ocean.HashrateGh!.Value, 3);
        Assert.Equal(0.00001854m, ocean.Balance);
        Assert.Equal(0.00001234m, Assert.Single(ocean.Earnings).Amount);
        Assert.Equal(0.00005m, Assert.Single(ocean.Payouts).Amount);
        Assert.Equal("bitaxe", Assert.Single(ocean.Workers).Name);

        var braiins = await Client(f).FetchAsync(new ExtraPoolAccount { Provider = "braiins" }, "token-123", Now);
        Assert.Equal(1250.5, braiins.HashrateGh!.Value, 3);
        Assert.Equal(0.00012m, braiins.Balance);
        Assert.Equal(2, braiins.Earnings.Count);                                    // 14. und 15.10., nicht heute
        Assert.Single(braiins.Payouts);                                             // nur bestätigte
        Assert.Equal("bitaxe", Assert.Single(braiins.Workers).Name);
        Assert.Contains(f.Requests, r => r.Headers.TryGetValues("Pool-Auth-Token", out var v) && v.Single() == "token-123");

        var via = await Client(f).FetchAsync(new ExtraPoolAccount { Provider = "viabtc", Coin = "BTC" }, "key-9", Now);
        Assert.Equal(747.67, via.HashrateGh!.Value, 1);
        Assert.Equal("viabtc-BTC-2026-10-15", Assert.Single(via.Earnings).Key);       // 16.10. ist noch nicht vorbei
        Assert.Equal(0.001m, Assert.Single(via.Payouts).Amount);
        Assert.Contains(f.Requests, r => r.Headers.TryGetValues("X-API-KEY", out var v) && v.Single() == "key-9");
    }

    [Fact]
    public async Task Bad_input_is_explained_and_key_rejection_is_clear()
    {
        var f = new Fake(_ => (HttpStatusCode.Forbidden, "{}"));
        await Assert.ThrowsAsync<LocalizedException>(() => Client(f).FetchAsync(new ExtraPoolAccount { Provider = "ckpool", Address = "../../etc" }, null, Now));
        await Assert.ThrowsAsync<LocalizedException>(() => Client(f).FetchAsync(new ExtraPoolAccount { Provider = "braiins" }, null, Now));
        var ex = await Assert.ThrowsAsync<LocalizedException>(() => Client(f).FetchAsync(new ExtraPoolAccount { Provider = "braiins" }, "x", Now));
        Assert.Contains("Zugangsschlüssel", ex.Message);
        Assert.Empty(f.Requests.Where(r => r.RequestUri!.ToString().Contains("etc")));
    }

    [Fact]
    public async Task Hub_imports_earnings_additively_and_reports_new_payouts_only()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "t";
        var payouts = """[{"ts":"2026-10-15T07:00:00","on_chain_txid":"abc","total_satoshis_net_paid":5000}]""";
        var fake = new Fake(r =>
        {
            var u = r.RequestUri!.ToString();
            if (u.Contains("/statsnap/")) return (HttpStatusCode.OK, """{"result":{"hashrate_300s":"1000000000000","unpaid":"0"}}""");
            if (u.Contains("/earnpay/")) return (HttpStatusCode.OK, "{\"result\":{\"earnings\":[{\"ts\":\"2026-10-15T06:27:52\",\"block_hash\":\"b1\",\"satoshis_net_earned\":1234}],\"payouts\":" + payouts + "}}");
            return (HttpStatusCode.OK, """{"error":"No such user or user has no active workers"}""");
        });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = _dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) })
        { ExtraPoolHandler = fake };
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);

        hub.SaveExtraPools([new ExtraPoolAccount { Provider = "ocean", Address = Addr, Name = "Mein OCEAN" }], null, "Test");
        await hub.ExtraPoolsTickAsync();
        var view = Assert.Single(hub.ExtraPools);
        Assert.Null(view.Error);
        Assert.Equal(1000, view.Status!.HashrateGh!.Value, 3);
        Assert.Single(hub.TaxMonitor.LoadRewards(), r => r.TxId == "ocean-b1" && r.Amount == 0.00001234m && r.WalletLabel == "Mein OCEAN");
        Assert.DoesNotContain(sent, k => k.StartsWith("extra-pool-payout"));          // erste Abfrage: alte Auszahlung nur merken

        payouts = """[{"ts":"2026-10-15T07:00:00","on_chain_txid":"abc","total_satoshis_net_paid":5000},{"ts":"2026-10-16T07:00:00","on_chain_txid":"def","total_satoshis_net_paid":7000}]""";
        await hub.ExtraPoolsTickAsync();
        Assert.Single(sent, k => k.StartsWith("extra-pool-payout") && k.Contains("def"));
        Assert.Single(hub.TaxMonitor.LoadRewards(), r => r.TxId == "ocean-b1");          // nicht doppelt

        // 0.9.20: Kurzstand für Übersicht und E-Paper-Seite „Pool“
        var sum = Assert.Single(hub.ExtraPoolSummaries());
        Assert.Equal("Mein OCEAN", sum.Name);
        Assert.Equal(0.00007m, sum.LastPayout);
        Assert.Equal(new DateTime(2026, 10, 16, 7, 0, 0), sum.LastPayoutUtc);
        config.Display.Pages = new DisplayPages { Overview = false, Daily = false, Chart = false, Soak = false, PoolAccount = true };
        var page = hub.ComposeDisplay(DateTime.Now, nextPage: true);
        Assert.Equal(BitaxeTuner.Core.Display.DisplayScene.PoolAccount, page.Scene);
        Assert.Single(page.PoolPage!.Extra);
        Assert.Equal(BitaxeTuner.Core.Fans.PicoFanDevice.ImageBytes, BitaxeTuner.Core.Display.StatusRenderer.Render(page).Length);
        var preview = Environment.GetEnvironmentVariable("BT_EPAPER_PREVIEW");
        if (preview is not null) SixLabors.ImageSharp.ImageExtensions.SaveAsPng(BitaxeTuner.Core.Display.StatusRenderer.RenderImage(page), Path.Combine(preview, "epaper-pool-extra.png"));
    }

    [Fact]
    public async Task Live_public_pools_answer_in_the_expected_format()
    {
        // Nur mit BT_LIVE_POOLS=1 (fragt die echten öffentlichen Pools mit der Genesis-Adresse ab)
        if (Environment.GetEnvironmentVariable("BT_LIVE_POOLS") != "1") return;
        var client = new ExtraPoolClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        foreach (var p in new[] { "public-pool", "ckpool", "ocean" })
        {
            var s = await client.FetchAsync(new ExtraPoolAccount { Provider = p, Address = Addr }, null, DateTime.UtcNow);
            Assert.NotNull(s);
            if (Environment.GetEnvironmentVariable("BT_LIVE_POOLS_OUT") is { Length: > 0 } outFile)
                File.AppendAllText(outFile, $"{p}: {s.HashrateGh:0.###} GH/s, Worker {s.Workers.Count}, Best {s.BestDifficulty}, Guthaben {s.Balance}{Environment.NewLine}");
        }
    }
}
