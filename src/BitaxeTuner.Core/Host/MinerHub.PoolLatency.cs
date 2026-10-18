using System.Diagnostics;
using System.Net.Sockets;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.24 Pool-Latenz: alle 10 Minuten je Pool (aus den Pool-Vorlagen und den aktiven Pools der Miner) die Zeit für den
/// TCP-Verbindungsaufbau zum Stratum-Port messen – nur Verbindungsaufbau, keine Anmeldung, nichts wird gesendet.
/// Ergebnis in history.db (Tabelle pool_latency, Aufbewahrung wie der Verlauf), Auswertung im Pool-Vergleich.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan PoolLatencyInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PoolLatencyTimeout = TimeSpan.FromSeconds(3);
    private DateTime _poolLatencyAt = DateTime.MinValue;
    private int _poolLatencyBusy;

    /// <summary>Für Tests: Messung ersetzen (Host, Port → ms oder null = nicht erreichbar).</summary>
    internal Func<string, int, Task<double?>>? PoolProbe { get; set; }

    /// <summary>Alle zu messenden Pools als „url:port“ (Schlüssel wie im Pool-Vergleich).</summary>
    internal IReadOnlyList<string> LatencyPools()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Config.PoolPresets)
            if (PoolKey(p.Url, p.Port) is { Length: > 0 } k) keys.Add(k);
        foreach (var s in States)
        {
            if (s.Info is not { } i) continue;
            if (PoolKey(i.stratumURL, i.stratumPort) is { Length: > 0 } a) keys.Add(a);
            if (PoolKey(i.fallbackStratumURL, i.fallbackStratumPort) is { Length: > 0 } b) keys.Add(b);
        }
        return keys.Where(k => k.Contains(':')).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>„stratum+tcp://pool.example:3333“ → („pool.example“, 3333).</summary>
    internal static (string Host, int Port)? SplitPoolKey(string key)
    {
        var s = key;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var colon = s.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(s[(colon + 1)..], out var port) || port is < 1 or > 65535) return null;
        var host = s[..colon].Split('/')[0].Trim();
        return host.Length == 0 ? null : (host, port);
    }

    /// <summary>Aus dem Abfragetakt: höchstens alle 10 Minuten messen, im Hintergrund (blockiert die Abfragen nicht).</summary>
    internal void TickPoolLatency(DateTime now)
    {
        if (History is null || now - _poolLatencyAt < PoolLatencyInterval) return;
        if (!Options.OnlineChecks && PoolProbe is null) return;
        if (Interlocked.Exchange(ref _poolLatencyBusy, 1) == 1) return;
        _poolLatencyAt = now;
        var pools = LatencyPools();
        var history = History;
        var probe = PoolProbe ?? ConnectMsAsync;
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await Task.WhenAll(pools.Select(async key =>
                {
                    if (SplitPoolKey(key) is not { } hp) return (key, (double?)null, false);
                    try { return (key, await probe(hp.Host, hp.Port), true); }
                    catch { return (key, (double?)null, true); }
                }));
                foreach (var (key, ms, valid) in results)
                    if (valid) history.AddPoolLatency(key, now, ms);
            }
            catch { /* nicht kritisch */ }
            finally { Interlocked.Exchange(ref _poolLatencyBusy, 0); }
        });
    }

    /// <summary>Zeit für den TCP-Verbindungsaufbau in ms, null = nicht erreichbar (3 s).</summary>
    internal static async Task<double?> ConnectMsAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(PoolLatencyTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return null; }
    }
}
