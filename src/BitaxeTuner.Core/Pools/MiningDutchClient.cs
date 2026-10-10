using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Pools;

/// <summary>Ein Worker beim Pool (Name = Teil hinter dem Punkt im Pool-Benutzer des Miners).</summary>
public sealed record PoolWorker(string Name, bool Alive, double HashrateHs, string NowMining, bool MergedMining, string Mode, DateTime? LastShareUtc);

/// <summary>Guthaben je Coin beim Pool.</summary>
public sealed record PoolBalance(CoinType Coin, decimal Confirmed, decimal Unconfirmed);

/// <summary>Buchung beim Pool: Gutschrift („Credit“) oder Auszahlung („Debit_AC“, „Debit_MP“ …).</summary>
public sealed record PoolTransaction(long Id, CoinType Coin, string Type, decimal Amount, DateTime TimeUtc, long? Height)
{
    public bool IsCredit => Type.Equals("Credit", StringComparison.OrdinalIgnoreCase);
    public bool IsPayout => Type.StartsWith("Debit", StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>Worker des Kontos (die Liste gilt kontoweit, abgefragt über den Bitcoin-Pool).</summary>
    public async Task<List<PoolWorker>> GetWorkersAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("bitcoin", "getuserworkers", ct);
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

    public async Task<PoolBalance> GetBalanceAsync(CoinType coin, CancellationToken ct = default)
    {
        var data = await GetAsync(Slug(coin), "getuserbalance", ct);
        return new PoolBalance(coin, Dec(data, "confirmed") ?? 0, Dec(data, "unconfirmed") ?? 0);
    }

    /// <summary>Letzte Buchungen des Coins (der Pool liefert nur die jüngsten Einträge – daher regelmäßig abfragen).</summary>
    public async Task<List<PoolTransaction>> GetTransactionsAsync(CoinType coin, CancellationToken ct = default)
    {
        var data = await GetAsync(Slug(coin), "getusertransactions", ct);
        var list = new List<PoolTransaction>();
        if (data.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
            foreach (var t in txs.EnumerateArray())
            {
                if (Num(t, "id") is not { } id || Dec(t, "amount") is not { } amount) continue;
                if (ParseTime(Str(t, "timestamp")) is not { } time) continue;
                var height = Num(t, "height") is { } hh && hh > 0 ? (long)hh : (long?)null;
                list.Add(new PoolTransaction((long)id, coin, Str(t, "type"), Math.Abs(amount), time, height));
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
