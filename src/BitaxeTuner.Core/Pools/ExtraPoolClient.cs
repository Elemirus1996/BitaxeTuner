using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Pools;

/// <summary>Worker beim Pool (Hashrate in GH/s).</summary>
public sealed record ExtraPoolWorker(string Name, double? HashrateGh, DateTime? LastShareUtc, double? BestDifficulty);

/// <summary>Ertrag (Tag oder Block); <paramref name="Key"/> ist stabil und eindeutig je Konto.</summary>
public sealed record ExtraPoolEarning(string Key, DateTime TimeUtc, decimal Amount, string Note);

public sealed record ExtraPoolPayout(string Key, DateTime TimeUtc, decimal Amount, string? Tx);

/// <summary>Stand eines weiteren Pool-Kontos (Beträge in Coins von <see cref="Coin"/>).</summary>
public sealed record ExtraPoolStatus(
    string Coin, double? HashrateGh, double? Hashrate24hGh, int ActiveWorkers, double? BestDifficulty, decimal? Balance,
    IReadOnlyList<ExtraPoolWorker> Workers, IReadOnlyList<ExtraPoolEarning> Earnings, IReadOnlyList<ExtraPoolPayout> Payouts);

/// <summary>
/// 0.9.20: Abfrage weiterer Pool-Konten – nur lesend, Schnittstellen laut Anbieter:
/// <list type="bullet">
/// <item>public-pool: <c>GET {base}/api/client/{address}</c> (Quelle: benjamin-wilson/public-pool, client.controller).</item>
/// <item>ckpool: <c>GET {base}/users/{address}</c> (Hashrate als Text mit Einheit, z. B. „113M“).</item>
/// <item>OCEAN: <c>GET api.ocean.xyz/v1/statsnap|user_hashrate_full|earnpay/{address}</c>.</item>
/// <item>Braiins: <c>pool.braiins.com/accounts/{profile|rewards|payouts|workers}/json/btc</c>, Kopf <c>Pool-Auth-Token</c>.</item>
/// <item>ViaBTC: <c>{base}/res/openapi/v1/{hashrate|profit/history|wallet/payment/history}</c>, Kopf <c>X-API-KEY</c>.</item>
/// </list>
/// </summary>
public sealed class ExtraPoolClient(HttpClient http)
{
    public const string DefaultPublicPool = "https://public-pool.io:40557";
    public const string DefaultCkpool = "https://solo.ckpool.org";
    public const string DefaultOcean = "https://api.ocean.xyz";
    public const string DefaultBraiins = "https://pool.braiins.com";
    public const string DefaultViabtc = "https://www.viabtc.net";
    private const decimal SatsPerBtc = 100_000_000m;

    /// <summary>Pause zwischen Abfragen (Braiins: „etwa eine Anfrage je 5 Sekunden“); in Tests ohne Warten.</summary>
    public Func<TimeSpan, CancellationToken, Task> Pause { get; init; } = Task.Delay;
    private static readonly TimeSpan BraiinsGap = TimeSpan.FromSeconds(5.5);

    public async Task<ExtraPoolStatus> FetchAsync(ExtraPoolAccount a, string? secret, DateTime nowUtc, CancellationToken ct = default)
    {
        if (a.NeedsAddress && !IsAddress(a.Address)) throw new LocalizedException("Bitte eine gültige Wallet-Adresse eintragen.");
        if (a.NeedsKey && string.IsNullOrWhiteSpace(secret)) throw new LocalizedException("Zugangsschlüssel des Pools fehlt.");
        return a.Provider switch
        {
            "public-pool" => await PublicPoolAsync(a, ct),
            "ckpool" => await CkpoolAsync(a, ct),
            "ocean" => await OceanAsync(a, nowUtc, ct),
            "braiins" => await BraiinsAsync(a, secret!, nowUtc, ct),
            "viabtc" => await ViabtcAsync(a, secret!, nowUtc, ct),
            _ => throw new LocalizedException("Unbekannter Pool-Anbieter: {0}", a.Provider),
        };
    }

    /// <summary>Nur Zeichen, die in Bitcoin-Adressen vorkommen (verhindert fremde Pfade in der URL).</summary>
    public static bool IsAddress(string? s) =>
        s is { Length: >= 20 and <= 100 } && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == ':');

    private static string Base(ExtraPoolAccount a, string fallback)
    {
        var b = (a.BaseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return fallback;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new LocalizedException("Adresse der Pool-API muss mit http:// oder https:// beginnen.");
        return b;
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (n, v) in headers) req.Headers.TryAddWithoutValidation(n, v);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LocalizedException("Der Pool lehnt den Zugangsschlüssel ab (bei ViaBTC auch IP-Freigabe prüfen).");
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new LocalizedException("Der Pool kennt diese Adresse bzw. dieses Konto nicht.");
        if (resp.StatusCode == HttpStatusCode.TooManyRequests) throw new LocalizedException("Der Pool bremst zu viele Abfragen – später erneut.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    // ---------- public-pool ----------

    private async Task<ExtraPoolStatus> PublicPoolAsync(ExtraPoolAccount a, CancellationToken ct)
    {
        var j = await GetAsync($"{Base(a, DefaultPublicPool)}/api/client/{a.Address.Trim()}", ct);
        var workers = Arr(j, "workers").Select(w => new ExtraPoolWorker(Str(w, "name") ?? "?", Num(w, "hashRate") is { } h ? h / 1e9 : null,
            Time(w, "lastSeen"), Num(w, "bestDifficulty"))).ToList();
        return new ExtraPoolStatus("BTC", workers.Sum(w => w.HashrateGh ?? 0), null, workers.Count, Num(j, "bestDifficulty"), null, workers, [], []);
    }

    // ---------- ckpool ----------

    private async Task<ExtraPoolStatus> CkpoolAsync(ExtraPoolAccount a, CancellationToken ct)
    {
        var j = await GetAsync($"{Base(a, DefaultCkpool)}/users/{a.Address.Trim()}", ct);
        var workers = Arr(j, "worker").Select(w =>
        {
            var name = Str(w, "workername") ?? "?";
            var dot = name.LastIndexOf('.');
            return new ExtraPoolWorker(dot >= 0 ? name[(dot + 1)..] : name, Suffixed(Str(w, "hashrate5m")) is { } h ? h / 1e9 : null,
                Unix(w, "lastshare"), Num(w, "bestever"));
        }).ToList();
        return new ExtraPoolStatus("BTC", Suffixed(Str(j, "hashrate5m")) is { } hr ? hr / 1e9 : null, Suffixed(Str(j, "hashrate1d")) is { } d ? d / 1e9 : null,
            workers.Count(w => (w.HashrateGh ?? 0) > 0), Num(j, "bestever"), null, workers, [], []);
    }

    /// <summary>„113M“, „35.5G“, „0“ → H/s (Faktor 1000).</summary>
    internal static double? Suffixed(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var f = char.ToUpperInvariant(s[^1]) switch { 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, 'P' => 1e15, 'E' => 1e18, _ => 1 };
        var n = f == 1 ? s : s[..^1];
        return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * f : null;
    }

    // ---------- OCEAN ----------

    private async Task<ExtraPoolStatus> OceanAsync(ExtraPoolAccount a, DateTime nowUtc, CancellationToken ct)
    {
        var b = Base(a, DefaultOcean);
        var addr = a.Address.Trim();
        var snap = Obj(await GetAsync($"{b}/v1/statsnap/{addr}", ct), "result");
        var workers = new List<ExtraPoolWorker>();
        double? h24 = null;
        try
        {
            var full = Obj(await GetAsync($"{b}/v1/user_hashrate_full/{addr}", ct), "result");
            h24 = Num(full, "hashrate_86400s") is { } x ? x / 1e9 : null;
            if (full.ValueKind == JsonValueKind.Object && full.TryGetProperty("workers", out var ws) && ws.ValueKind == JsonValueKind.Object)
                foreach (var w in ws.EnumerateObject())
                {
                    var first = w.Value.ValueKind == JsonValueKind.Array ? w.Value.EnumerateArray().FirstOrDefault() : w.Value;
                    workers.Add(new ExtraPoolWorker(w.Name, (Num(first, "hashrate_300s") ?? Num(first, "hashrate_60s")) is { } wh ? wh / 1e9 : null, null, null));
                }
        }
        catch (LocalizedException) { /* ohne aktive Worker liefert OCEAN hier einen Fehler */ }
        catch (HttpRequestException) { }
        var start = nowUtc.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = nowUtc.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ep = Obj(await GetAsync($"{b}/v1/earnpay/{addr}/{start}/{end}", ct), "result");
        var earnings = Arr(ep, "earnings").Select(e =>
        {
            var t = Time(e, "ts") ?? Time(e, "time") ?? Time(e, "block_ts") ?? nowUtc;
            var hash = Str(e, "block_hash") ?? t.ToString("O");
            return new ExtraPoolEarning("ocean-" + hash, t, (Num(e, "satoshis_net_earned") is { } s ? (decimal)s : 0) / SatsPerBtc,
                L.T("OCEAN: Ertrag aus Block {0}", Str(e, "block_hash") is { Length: > 12 } bh ? bh[..12] + "…" : "?"));
        }).Where(e => e.Amount > 0).ToList();
        var payouts = Arr(ep, "payouts").Select(p =>
        {
            var t = Time(p, "ts") ?? Time(p, "time") ?? nowUtc;
            var tx = Str(p, "on_chain_txid") ?? Str(p, "txid") ?? Str(p, "generation_txn_hash");
            return new ExtraPoolPayout("ocean-pay-" + (tx ?? t.ToString("O")), t,
                (Num(p, "total_satoshis_net_paid") is { } s ? (decimal)s : 0) / SatsPerBtc, tx);
        }).Where(p => p.Amount > 0).ToList();
        return new ExtraPoolStatus("BTC", Num(snap, "hashrate_300s") is { } hr ? hr / 1e9 : null, h24,
            workers.Count(w => (w.HashrateGh ?? 0) > 0), null, Dec(snap, "unpaid"), workers, earnings, payouts);
    }

    // ---------- Braiins ----------

    private async Task<ExtraPoolStatus> BraiinsAsync(ExtraPoolAccount a, string token, DateTime nowUtc, CancellationToken ct)
    {
        var b = Base(a, DefaultBraiins);
        (string, string) auth = ("Pool-Auth-Token", token.Trim());
        var profile = Obj(await GetAsync($"{b}/accounts/profile/json/btc/", ct, auth), "btc");
        var unit = Str(profile, "hash_rate_unit");
        var from = nowUtc.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await Pause(BraiinsGap, ct);
        var rewards = Obj(await GetAsync($"{b}/accounts/rewards/json/btc?from={from}&to={to}", ct, auth), "btc");
        var today = nowUtc.Date;
        var earnings = Arr(rewards, "daily_rewards")
            .Select(r => (Day: Unix(r, "date"), Amount: Dec(r, "total_reward") ?? 0))
            .Where(r => r.Day is { } d && d.Date < today && r.Amount > 0)   // nur abgeschlossene Tage
            .Select(r => new ExtraPoolEarning($"braiins-{r.Day!.Value:yyyy-MM-dd}", r.Day!.Value.AddDays(1).AddSeconds(-1), r.Amount,
                L.T("Braiins Pool: Ertrag am {0:d}", r.Day!.Value)))
            .ToList();
        await Pause(BraiinsGap, ct);
        var payoutsJson = await GetAsync($"{b}/accounts/payouts/json/btc?from={from}&to={to}", ct, auth);
        var pj = payoutsJson.ValueKind == JsonValueKind.Object && payoutsJson.TryGetProperty("btc", out var inner) ? inner : payoutsJson;
        var payouts = Arr(pj, "onchain").Concat(Arr(pj, "lightning"))
            .Where(p => Str(p, "status") == "confirmed")
            .Select(p => new ExtraPoolPayout("braiins-pay-" + (Str(p, "tx_id") ?? Str(p, "resolved_at_ts") ?? Str(p, "requested_at_ts")),
                Unix(p, "resolved_at_ts") ?? Unix(p, "requested_at_ts") ?? nowUtc, (Dec(p, "amount_sats") ?? 0) / SatsPerBtc, Str(p, "tx_id")))
            .ToList();
        await Pause(BraiinsGap, ct);
        var workersJson = Obj(await GetAsync($"{b}/accounts/workers/json/btc", ct, auth), "btc");
        var workers = new List<ExtraPoolWorker>();
        if (workersJson.ValueKind == JsonValueKind.Object && workersJson.TryGetProperty("workers", out var ws) && ws.ValueKind == JsonValueKind.Object)
            foreach (var w in ws.EnumerateObject())
            {
                var name = w.Name;
                var dot = name.IndexOf('.');
                workers.Add(new ExtraPoolWorker(dot >= 0 ? name[(dot + 1)..] : name, ToGh(Num(w.Value, "hash_rate_5m"), Str(w.Value, "hash_rate_unit") ?? unit),
                    Unix(w.Value, "last_share"), null));
            }
        return new ExtraPoolStatus("BTC", ToGh(Num(profile, "hash_rate_5m"), unit), ToGh(Num(profile, "hash_rate_24h"), unit),
            (int)(Num(profile, "ok_workers") ?? workers.Count), null, Dec(profile, "current_balance"), workers, earnings, payouts);
    }

    /// <summary>Hashrate mit Einheit („Gh/s“, „Th/s“ …) → GH/s.</summary>
    internal static double? ToGh(double? value, string? unit) => value is not { } v ? null : (unit ?? "Gh/s").Trim().ToLowerInvariant() switch
    {
        "h/s" => v / 1e9, "kh/s" => v / 1e6, "mh/s" => v / 1e3, "gh/s" => v, "th/s" => v * 1e3, "ph/s" => v * 1e6, "eh/s" => v * 1e9, _ => v,
    };

    // ---------- ViaBTC ----------

    private async Task<ExtraPoolStatus> ViabtcAsync(ExtraPoolAccount a, string key, DateTime nowUtc, CancellationToken ct)
    {
        var b = Base(a, DefaultViabtc) + "/res/openapi/v1";
        var coin = a.Coin is "BCH" ? "BCH" : "BTC";
        (string, string) auth = ("X-API-KEY", key.Trim());
        var hr = ViaData(await GetAsync($"{b}/hashrate?coin={coin}", ct, auth));
        var start = nowUtc.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var profit = ViaData(await GetAsync($"{b}/profit/history?coin={coin}&start_date={start}&utc=true&limit=100", ct, auth));
        var today = nowUtc.Date;
        var earnings = Arr(profit, "data")
            .Select(r => (Day: DateTime.TryParse(Str(r, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null,
                Amount: Dec(r, "total_profit") ?? 0))
            .Where(r => r.Day is { } d && d.Date < today && r.Amount > 0)
            .Select(r => new ExtraPoolEarning($"viabtc-{coin}-{r.Day!.Value:yyyy-MM-dd}", r.Day!.Value.Date.AddDays(1).AddSeconds(-1), r.Amount,
                L.T("ViaBTC: Ertrag am {0:d}", r.Day!.Value)))
            .ToList();
        var pay = ViaData(await GetAsync($"{b}/wallet/payment/history?coin={coin}&start_date={start}&utc=true&limit=100", ct, auth));
        var payouts = Arr(pay, "data").Select(p => new ExtraPoolPayout($"viabtc-pay-{Str(p, "id") ?? Str(p, "tx")}",
            Unix(p, "create_time") ?? nowUtc, Dec(p, "amount") ?? 0, Str(p, "tx"))).Where(p => p.Amount > 0).ToList();
        return new ExtraPoolStatus(coin, Num(hr, "hashrate_10min") is { } h ? h / 1e9 : null, Num(hr, "hashrate_24hour") is { } d24 ? d24 / 1e9 : null,
            (int)(Num(hr, "active_workers") ?? 0), null, null, [], earnings, payouts);
    }

    /// <summary>ViaBTC-Hülle {code, data, message}: code ≠ 0 = Fehler des Pools.</summary>
    private static JsonElement ViaData(JsonElement j)
    {
        if (Num(j, "code") is { } code && code != 0)
            throw new LocalizedException("ViaBTC meldet: {0}", Str(j, "message") ?? code.ToString(CultureInfo.InvariantCulture));
        return Obj(j, "data");
    }

    // ---------- JSON-Helfer (Zahlen kommen teils als Text) ----------

    private static JsonElement Obj(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static double? Num(JsonElement j, string name) =>
        Str(j, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? Dec(JsonElement j, string name) =>
        Str(j, name) is { } s && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime? Unix(JsonElement j, string name) =>
        Num(j, name) is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime : null;

    private static DateTime? Time(JsonElement j, string name)
    {
        var s = Str(j, name);
        if (s is null) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var u) && u > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)(u > 1e11 ? u / 1000 : u)).UtcDateTime : null;
    }
}
