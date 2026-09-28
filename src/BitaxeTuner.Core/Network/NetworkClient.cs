using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

public sealed record BlockDto(long Height, DateTime Time, string Pool, string PoolSlug,
                              int TxCount, long RewardSat, long SizeBytes);

public sealed record PoolDto(string Name, string Slug, int BlockCount);

/// <summary>
/// Netzwerkdaten von mempool.space: zuletzt gefundene Blöcke und Pool-Ranking.
/// </summary>
public sealed class NetworkClient : IDisposable
{
    private const string Base = "https://mempool.space/api";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public NetworkClient() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");

    /// <summary>Die letzten 15 Blöcke inklusive Pool-Zuordnung.</summary>
    public async Task<List<BlockDto>> GetBlocksAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{Base}/v1/blocks", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var list = new List<BlockDto>();
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            var height = b.TryGetProperty("height", out var h) ? h.GetInt64() : 0;
            var ts = b.TryGetProperty("timestamp", out var t) ? t.GetInt64() : 0;
            var txCount = b.TryGetProperty("tx_count", out var tc) ? tc.GetInt32() : 0;
            var size = b.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

            var pool = L.T("unbekannt");
            var slug = "";
            long reward = 0;

            if (b.TryGetProperty("extras", out var extras))
            {
                if (extras.TryGetProperty("pool", out var p))
                {
                    if (p.TryGetProperty("name", out var pn)) pool = pn.GetString() ?? pool;
                    if (p.TryGetProperty("slug", out var ps)) slug = ps.GetString() ?? "";
                }
                if (extras.TryGetProperty("reward", out var r) && r.TryGetInt64(out var rv)) reward = rv;
            }

            list.Add(new BlockDto(height, DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime,
                                  pool, slug, txCount, reward, size));
        }

        return list.OrderByDescending(x => x.Height).ToList();
    }

    /// <summary>
    /// Pool-Ranking nach gefundenen Blöcken. Zeitraum: 24h, 3d, 1w, 1m, 3m, 6m, 1y, 2y, 3y oder "all".
    /// </summary>
    public async Task<List<PoolDto>> GetPoolsAsync(string period, CancellationToken ct)
    {
        var url = period is "all" or "" ? $"{Base}/v1/mining/pools" : $"{Base}/v1/mining/pools/{period}";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var list = new List<PoolDto>();
        if (!doc.RootElement.TryGetProperty("pools", out var pools)) return list;

        foreach (var p in pools.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var slug = p.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
            var count = p.TryGetProperty("blockCount", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            if (count > 0) list.Add(new PoolDto(name, slug, count));
        }

        return list.OrderByDescending(x => x.BlockCount).ToList();
    }

    public void Dispose() => _http.Dispose();
}
