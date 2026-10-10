using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Pools;

/// <summary>Ein Worker beim Pool (Name = Teil hinter dem Punkt im Pool-Benutzer des Miners).</summary>
/// <param name="Mode">Abrechnung, im Pool-Dashboard umschaltbar: PPS, PROP, A-PROP, PPLNS, SOLO, PARTY.</param>
public sealed record PoolWorker(string Name, bool Alive, double HashrateHs, string NowMining, bool MergedMining, string Mode, DateTime? LastShareUtc)
{
    /// <summary>Solo beim Pool: ein gefundener Block gehört dem Miner (abzüglich Gebühr) – zählt zur Solo-Chance.</summary>
    public bool IsSolo => Mode.Contains("SOLO", StringComparison.OrdinalIgnoreCase);

    /// <summary>Party-Mining: Blockbelohnung wird unter den Mitgliedern der Party aufgeteilt.</summary>
    public bool IsParty => Mode.Contains("PARTY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Guthaben je Coin beim Pool. <paramref name="Code"/>: Kürzel bei <see cref="CoinType.Other"/> (z. B. „WJK“).</summary>
public sealed record PoolBalance(CoinType Coin, decimal Confirmed, decimal Unconfirmed, string? Code = null)
{
    public string Symbol => Coin.Symbol(Code);
}

/// <summary>
/// Buchung beim Pool: Gutschrift („Credit“) oder Auszahlung („Debit_AC“, „Debit_MP“ …). 0.9.13: <paramref name="Code"/> =
/// Kürzel bei <see cref="CoinType.Other"/>, <paramref name="Slug"/> = Pool-Name bei Mining-Dutch (für spätere Abfragen).
/// </summary>
/// <param name="FromShares">Gutschrift aus dem Mining (txid enthält die Shares); false = z. B. BTC aus dem automatischen
/// Umtausch; null = ältere Einträge ohne Angabe.</param>
public sealed record PoolTransaction(long Id, CoinType Coin, string Type, decimal Amount, DateTime TimeUtc, long? Height,
    string? Code = null, string? Slug = null, bool? FromShares = null)
{
    public bool IsCredit => Type.Equals("Credit", StringComparison.OrdinalIgnoreCase);
    public bool IsPayout => Type.StartsWith("Debit", StringComparison.OrdinalIgnoreCase);
    public string Symbol => Coin.Symbol(Code);
}

/// <summary>Coin-Pool bei Mining-Dutch: Name (Slug), Kürzel laut Dashboard, Kurs-Rohwert aus dem Dashboard.</summary>
public sealed record PoolCoinInfo(string Slug, string Currency, decimal? ExchangePrice);

/// <summary>
/// 0.9.12: Pool-Konto bei Mining-Dutch (MPOS-basiert). Abfragen je Coin-Pool:
/// <c>pools/{coin}.php?page=api&amp;action=…&amp;api_key=…</c>. Der Pool sperrt bei vielen Anfragen – zwischen zwei
/// Anfragen liegen mindestens <see cref="MinGap"/>. Zahlen kommen teils als Text („0.00012345“), Zeiten als
/// „JJJJ-MM-TT hh:mm:ss“ in der Zeit des Pools (Niederlande).
/// </summary>
public sealed class MiningDutchClient : IDisposable
{
    /// <summary>Pool-Namen bei Mining-Dutch je Coin (BCH bietet der Pool nicht an).</summary>
    public static readonly IReadOnlyDictionary<CoinType, string> Slugs = new Dictionary<CoinType, string>
    {
        [CoinType.Bitcoin] = "bitcoin",
        [CoinType.DigiByte] = "digibyte",
        [CoinType.Namecoin] = "namecoin",
        [CoinType.Elastos] = "elastos",
        [CoinType.Peercoin] = "peercoin",
        [CoinType.Emercoin] = "emercoin",
    };

    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(6);

    /// <summary>Meldung bei abgelehntem Schlüssel – dann bricht die ganze Runde ab (alle weiteren Anfragen scheitern auch).</summary>
    public static readonly string KeyRejected = L.N("Mining-Dutch hat den API-Schlüssel abgelehnt.");
    private static readonly TimeZoneInfo PoolZone = FindZone();

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _gap;
    private DateTime _last = DateTime.MinValue;

    public MiningDutchClient(string apiKey, HttpMessageHandler? handler = null, TimeSpan? gap = null)
    {
        _key = apiKey.Trim();
        _gap = gap ?? MinGap;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://www.mining-dutch.nl/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
    }

    /// <summary>
    /// Worker des Kontos. Die Werte hängen davon ab, über welchen Pool man fragt: der SHA-256-Pool (Profit-Wechsel, auch
    /// SOLO) liefert die volle Hashrate, der Bitcoin-Pool nur den Anteil dort. 0.9.13: beide abfragen und je Worker den
    /// Eintrag mit der höheren Hashrate nehmen.
    /// </summary>
    public async Task<List<PoolWorker>> GetWorkersAsync(CancellationToken ct = default)
    {
        var merged = new Dictionary<string, PoolWorker>(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in new[] { "sha256", "bitcoin" })
            foreach (var w in await GetWorkersFromAsync(slug, ct))
                if (!merged.TryGetValue(w.Name, out var known) || w.HashrateHs > known.HashrateHs) merged[w.Name] = w;
        return merged.Values.ToList();
    }

    private async Task<List<PoolWorker>> GetWorkersFromAsync(string slug, CancellationToken ct)
    {
        var data = await GetAsync(slug, "getuserworkers", ct);
        var list = new List<PoolWorker>();
        if (data.TryGetProperty("miners", out var miners) && miners.ValueKind == JsonValueKind.Array)
            foreach (var m in miners.EnumerateArray())
            {
                var name = Str(m, "username");
                if (name.Length == 0) continue;
                var last = Num(m, "lastshare") is { } ls && ls > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)ls).UtcDateTime : (DateTime?)null;
                list.Add(new PoolWorker(name, Num(m, "alive") is > 0, Num(m, "hashrate") ?? 0, Str(m, "nowMining"),
                    Num(m, "mergedMining") is > 0, Str(m, "mode"), last));
            }
        return list;
    }

    public Task<PoolBalance> GetBalanceAsync(CoinType coin, CancellationToken ct = default) => GetBalanceAsync(Slug(coin), coin, null, ct);

    /// <summary>Guthaben in einem Coin-Pool (auch Coins außerhalb der festen Liste, dann mit Kürzel).</summary>
    public async Task<PoolBalance> GetBalanceAsync(string slug, CoinType coin, string? code, CancellationToken ct = default)
    {
        var data = await GetAsync(slug, "getuserbalance", ct);
        return new PoolBalance(coin, Dec(data, "confirmed") ?? 0, Dec(data, "unconfirmed") ?? 0, coin == CoinType.Other ? code : null);
    }

    /// <summary>Kürzel und Kurs-Rohwert eines Coin-Pools (Dashboard).</summary>
    public async Task<PoolCoinInfo> GetCoinInfoAsync(string slug, CancellationToken ct = default)
    {
        var data = await GetAsync(slug, "getdashboarddata", ct);
        var pool = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("pool", out var p) ? p : default;
        var info = pool.ValueKind == JsonValueKind.Object && pool.TryGetProperty("info", out var i) ? i : default;
        var currency = Str(info, "currency").Trim().ToUpperInvariant();
        return new PoolCoinInfo(slug, currency.Length is > 0 and <= 12 ? currency : slug.ToUpperInvariant(), Dec(pool, "exchangePrice"));
    }

    /// <summary>Letzte Buchungen des Coins (der Pool liefert nur die jüngsten Einträge – daher regelmäßig abfragen).</summary>
    public Task<List<PoolTransaction>> GetTransactionsAsync(CoinType coin, CancellationToken ct = default) =>
        GetTransactionsAsync(Slug(coin), coin, null, ct);

    public async Task<List<PoolTransaction>> GetTransactionsAsync(string slug, CoinType coin, string? code, CancellationToken ct = default)
    {
        var data = await GetAsync(slug, "getusertransactions", ct);
        var list = new List<PoolTransaction>();
        if (data.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
            foreach (var t in txs.EnumerateArray())
            {
                if (Num(t, "id") is not { } id || Dec(t, "amount") is not { } amount) continue;
                if (ParseTime(Str(t, "timestamp")) is not { } time) continue;
                var height = Num(t, "height") is { } hh && hh > 0 ? (long)hh : (long?)null;
                // Mining-Gutschriften tragen im txid-Feld die Shares ({"shares":…}); Umtausch-Gutschriften nicht
                var fromShares = Str(t, "txid").Contains("shares", StringComparison.OrdinalIgnoreCase);
                list.Add(new PoolTransaction((long)id, coin, Str(t, "type"), Math.Abs(amount), time, height,
                    coin == CoinType.Other ? code : null, slug, fromShares));
            }
        return list;
    }

    /// <summary>„JJJJ-MM-TT hh:mm:ss“ in der Zeit des Pools (Europe/Amsterdam) → UTC.</summary>
    public static DateTime? ParseTime(string text)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), PoolZone);
    }

    public static string Slug(CoinType coin) =>
        Slugs.TryGetValue(coin, out var s) ? s : throw new LocalizedException("{0} gibt es bei Mining-Dutch nicht.", coin.Symbol());

    private async Task<JsonElement> GetAsync(string slug, string action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _last + _gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
            using var response = await _http.GetAsync($"pools/{slug}.php?page=api&action={action}&api_key={Uri.EscapeDataString(_key)}", ct);
            if ((int)response.StatusCode is 401 or 403)
                throw new LocalizedException(KeyRejected);
            if (!response.IsSuccessStatusCode)
                throw new LocalizedException("Mining-Dutch antwortet mit HTTP {0} ({1}).", (int)response.StatusCode, slug);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try { root = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { throw new LocalizedException("Mining-Dutch lieferte keine gültige Antwort ({0}).", slug); }
            return root.TryGetProperty(action, out var a) && a.TryGetProperty("data", out var d) ? d : default;
        }
        catch (HttpRequestException ex)
        {
            throw new LocalizedException("Mining-Dutch nicht erreichbar: {0}", ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LocalizedException("Mining-Dutch antwortet nicht (Zeitüberschreitung).");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? Dec(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Europe/Amsterdam", "W. Europe Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* nächster Name */ }
        return TimeZoneInfo.Utc;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
