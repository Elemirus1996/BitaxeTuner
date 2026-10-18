using System.Net;
using System.Net.Sockets;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24 Pool-Latenz: Verbindungsaufbau messen, speichern, im Pool-Vergleich zeigen, schnellsten Pool vorschlagen.</summary>
public class PoolLatencyTests
{
    [Fact]
    public void Pool_keys_are_split_into_host_and_port()
    {
        Assert.Equal(("pool.example.org", 3333), MinerHub.SplitPoolKey("stratum+tcp://pool.example.org:3333"));
        Assert.Equal(("pool.example.org", 4444), MinerHub.SplitPoolKey("pool.example.org:4444"));
        Assert.Null(MinerHub.SplitPoolKey("pool.example.org"));
        Assert.Null(MinerHub.SplitPoolKey("pool.example.org:99999"));
    }

    [Fact]
    public async Task Connect_time_is_measured_and_a_closed_port_is_unreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var ms = await MinerHub.ConnectMsAsync("127.0.0.1", port);
            Assert.NotNull(ms);
            Assert.True(ms < 3000);
        }
        finally { listener.Stop(); }
        Assert.Null(await MinerHub.ConnectMsAsync("127.0.0.1", port));      // jetzt geschlossen
    }

    [Fact]
    public async Task Measurements_are_stored_and_the_fastest_reliable_pool_is_suggested()
    {
        using var dir = new TempDir();
        var config = new AppConfig
        {
            PoolPresets =
            [
                new PoolPreset { Id = "a", Name = "Schnell", Url = "fast.example.org", Port = 3333 },
                new PoolPreset { Id = "b", Name = "Langsam", Url = "slow.example.org", Port = 3333 },
                new PoolPreset { Id = "c", Name = "Wackelig", Url = "flaky.example.org", Port = 3333 },
            ],
        };
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        Assert.Equal(["fast.example.org:3333", "flaky.example.org:3333", "slow.example.org:3333"], hub.LatencyPools());

        var round = 0;
        hub.PoolProbe = (host, _) => Task.FromResult<double?>(host switch
        {
            "fast.example.org" => 12,
            "slow.example.org" => 95,
            _ => round % 3 == 0 ? null : 5,      // schnell, aber jede dritte Messung nicht erreichbar
        });
        var start = DateTime.Now.AddHours(-2);
        for (round = 0; round < 8; round++)
        {
            hub.TickPoolLatency(start.AddMinutes(round * 10));
            for (var i = 0; i < 100 && hub.History!.QueryPoolLatency(start.AddHours(-1), DateTime.Now).Values.Sum(v => v.Total) < (round + 1) * 3; i++)
                await Task.Delay(20);
        }
        hub.TickPoolLatency(start.AddMinutes(75));                       // innerhalb von 10 min: keine weitere Messung
        await Task.Delay(100);

        var result = hub.PoolComparisonFor("24h", DateTime.Now);
        var fast = result.Rows.Single(r => r.Pool == "fast.example.org:3333");
        Assert.Equal((12.0, 100.0, 8), (fast.ConnectMs!.Value, fast.ConnectOkPercent!.Value, fast.ConnectSamples));
        Assert.Equal(0, fast.Miners);                                    // Vorlage ohne Miner erscheint trotzdem
        var flaky = result.Rows.Single(r => r.Pool == "flaky.example.org:3333");
        Assert.True(flaky.ConnectOkPercent < 95);
        Assert.Equal("fast.example.org:3333", result.Fastest?.Pool);      // der wackelige ist schneller, aber nicht zuverlässig
    }
}
