using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Tests;

/// <summary>0.9.26: Benchmark-Vergleich mit der Community (freiwillig, anonym).</summary>
public class CommunityBenchTests
{
    private const string Stats = """
        { "updated": "2026-10-23T17:00:00Z", "models": {
            "bitaxe-gamma": { "n": 30, "jth": [14.38, 15.39, 15.86, 16.86, 18.48], "gh": 1109.9, "temp": 57.5, "freq": 550, "mv": 1175 },
            "kaputt": { "n": 3, "jth": [16, 15, 14, 13, 12], "gh": 1, "temp": 1, "freq": 1, "mv": 1 },
            "wenig": { "n": 2, "jth": [10, 11, 12, 13, 14], "gh": 1, "temp": 1, "freq": 1, "mv": 1 } } }
        """;

    [Fact]
    public void Payload_contains_only_the_measured_values()
    {
        var json = CommunityBench.Payload(new BenchSample("bitaxe-gamma", "BM1370", "v2.10.0", 550, 1175, 1123.456, 15.678, 57.04));
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList();
        Assert.Equal(["app", "asic", "freq", "fw", "gh", "jth", "kind", "model", "mv", "temp", "v"], names);
        Assert.Equal(15.68, doc.RootElement.GetProperty("jth").GetDouble());
    }

    [Fact]
    public void Stats_are_read_and_ranked()
    {
        var stats = CommunityBench.ParseStats(Stats);
        Assert.Equal(["bitaxe-gamma", "wenig"], stats.Keys.Order());       // nicht aufsteigende Quantile verworfen
        var g = stats["bitaxe-gamma"];
        Assert.Equal(0.5, g.BetterThan(15.86));
        Assert.True(g.BetterThan(13.5) >= 0.9);
        Assert.True(g.BetterThan(20) <= 0.1);
        Assert.Null(stats["wenig"].BetterThan(12));                         // zu wenige Einsendungen
        Assert.Equal("im oberen Drittel", CommunityBench.RankText(0.7));
        Assert.Empty(CommunityBench.ParseStats("kein json"));
    }

    private sealed class RealLikeClient(string address, MinerInfo info) : IMinerClient
    {
        public string Address => address;
        public Task<MinerInfo> GetInfoAsync(CancellationToken ct = default) => Task.FromResult(info);
        public Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) => Task.FromResult<AsicInfo?>(null);
        public Task ApplySettingsAsync(int f, int mv, TuningSource s = TuningSource.Manual, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFanAsync(int a, int m, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Only_opted_in_stable_miners_are_sent_at_most_weekly()
    {
        using var dir = new TempDir();
        var info = new MinerInfo { HashRateGh = 1150, PowerW = 18, ChipTempC = 57, FrequencyMhz = 550, CoreVoltageMv = 1175, AsicModel = "BM1370", FirmwareVersion = "v2.10.0", DeviceModel = "Gamma", UptimeSeconds = 200000 };
        var config = new AppConfig { FilePath = Path.Combine(dir.Path, "config.json") };
        config.Devices.Add(new DeviceConfig { Name = "Gamma Keller", Host = "10.0.26.1", ProfileId = "bitaxe-gamma", ShareBenchmark = true });
        config.Devices.Add(new DeviceConfig { Name = "Gamma Flur", Host = "10.0.26.2", ProfileId = "bitaxe-gamma" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new RealLikeClient(h, info) });
        await hub.PollNowAsync();
        var now = DateTime.Now;
        foreach (var host in new[] { "10.0.26.1", "10.0.26.2" })
            for (var m = 0; m < 23 * 60; m++) hub.History!.AddSample(host, now.AddMinutes(-m - 1), 1150, 57, 18, true);

        var sent = new List<BenchSample>();
        var fetched = 0;
        hub.BenchSender = s => { lock (sent) sent.Add(s); return Task.CompletedTask; };
        hub.BenchStatsFetch = () => { Interlocked.Increment(ref fetched); return Task.FromResult(Stats); };
        hub.TickCommunity(now);
        for (var i = 0; i < 500 && (sent.Count == 0 || Volatile.Read(ref fetched) == 0 || hub.CommunityStats.Count == 0); i++) await Task.Delay(20);

        var s = Assert.Single(sent);
        Assert.Equal(("bitaxe-gamma", 550, 1175), (s.Model, s.FreqMhz, s.CoreMv));
        Assert.InRange(s.Jth, 15.6, 15.7);
        Assert.NotNull(config.Devices[0].BenchSharedAt);
        Assert.Equal(1, fetched);
        var cmp = hub.CommunityOf(hub.Devices[0], now)!;
        Assert.Equal((30, "bitaxe-gamma"), (cmp.N, cmp.Model));
        Assert.Equal(0.61, cmp.BetterThan);                                  // 15,65 J/TH: zwischen 25-%- (15,39) und 50-%-Wert (15,86)
        Assert.Contains("J/TH", cmp.Text);

        hub.TickCommunity(now.AddHours(2));
        await Task.Delay(200);
        Assert.Single(sent);                                                // höchstens einmal pro Woche
    }

    [Fact]
    public void Nobody_opted_in_means_no_contact()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.26.3" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var calls = 0;
        hub.BenchSender = _ => { calls++; return Task.CompletedTask; };
        hub.BenchStatsFetch = () => { calls++; return Task.FromResult(Stats); };
        hub.TickCommunity(DateTime.Now);
        Thread.Sleep(150);
        Assert.Equal(0, calls);
    }
}
