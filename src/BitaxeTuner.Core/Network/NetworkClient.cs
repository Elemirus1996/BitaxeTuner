using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

public sealed record BlockDto(long Height, DateTime Time, string Pool, string PoolSlug,
                              int TxCount, long RewardSat, long SizeBytes);

public sealed record PoolDto(string Name, string Slug, int BlockCount);

/// <summary>0.9.16: empfohlene Gebühren (sat/vB) laut mempool.space.</summary>
public sealed record FeesDto(int Fastest, int HalfHour, int Hour, int Economy, int Minimum);

/// <summary>Nächste Difficulty-Anpassung: Fortschritt in %, erwartete Änderung in %, Restblöcke, geschätzter Zeitpunkt.</summary>
public sealed record DifficultyDto(double ProgressPercent, double ExpectedChangePercent, int RemainingBlocks, DateTime? Eta, long NextHeight);

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
        using var resp = await MempoolLimit.GetAsync(_http, $"{Base}/v1/blocks", ct).ConfigureAwait(false);
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

    /// <summary>Stand der nächsten Difficulty-Anpassung (alle 2016 Blöcke).</summary>
    public async Task<DifficultyDto> GetDifficultyAsync(CancellationToken ct)
    {
        using var resp = await MempoolLimit.GetAsync(_http, $"{Base}/v1/difficulty-adjustment", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseDifficulty(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>0.9.16: empfohlene Gebühren (/v1/fees/recommended).</summary>
    public async Task<FeesDto> GetFeesAsync(CancellationToken ct)
    {
        using var resp = await MempoolLimit.GetAsync(_http, $"{Base}/v1/fees/recommended", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseFees(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static FeesDto ParseFees(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        int Get(string name) => r.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? (int)Math.Round(d) : 0;
        return new FeesDto(Get("fastestFee"), Get("halfHourFee"), Get("hourFee"), Get("economyFee"), Get("minimumFee"));
    }

    /// <summary>0.9.15: aktuelle BTC-Difficulty (Ausweichquelle, wenn Blockchair sperrt).</summary>
    public async Task<double?> GetCurrentDifficultyAsync(CancellationToken ct)
    {
        using var resp = await MempoolLimit.GetAsync(_http, $"{Base}/v1/mining/hashrate/3d", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseCurrentDifficulty(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static double? ParseCurrentDifficulty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("currentDifficulty", out var v) && v.TryGetDouble(out var d) && d > 0 ? d : null;
    }

    public static DifficultyDto ParseDifficulty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        double D(string name) => r.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
        var eta = D("estimatedRetargetDate");
        return new DifficultyDto(D("progressPercent"), D("difficultyChange"), (int)D("remainingBlocks"),
            eta > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)eta).LocalDateTime : null, (long)D("nextRetargetHeight"));
    }

    /// <summary>
    /// Pool-Ranking nach gefundenen Blöcken. Zeitraum: 24h, 3d, 1w, 1m, 3m, 6m, 1y, 2y, 3y oder "all".
    /// </summary>
    public async Task<List<PoolDto>> GetPoolsAsync(string period, CancellationToken ct)
    {
        var url = period is "all" or "" ? $"{Base}/v1/mining/pools" : $"{Base}/v1/mining/pools/{period}";

        using var resp = await MempoolLimit.GetAsync(_http, url, ct).ConfigureAwait(false);
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
