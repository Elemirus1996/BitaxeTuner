using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Stand einer Adresse aus einem einzigen Blockchair-Abruf.</summary>
public sealed record AddressSnapshot(long BalanceSat, int TxCount, IReadOnlyList<IncomingTransaction> Incoming, DateTime FetchedUtc);

/// <summary>
/// Eingehende Transaktionen, Guthaben und Netzwerk-Difficulty über die
/// Blockchair-API, gemeinsam für BTC und BCH.
///
/// Ein Abruf je Adresse: <c>?transaction_details=true</c> liefert hash, time,
/// balance_change und block_id direkt im Adress-Dashboard. Ohne API-Key sind
/// für private Nutzung rund 1.440 Requests pro Tag und 30 pro Minute
/// vorgesehen; bei Überschreitung antwortet Blockchair mit 402, 429 oder 430.
///
/// Adress-Abrufe werden einige Minuten gecacht. Steuer-Monitor und Wallet-Panel
/// fragen dieselben Adressen ab — ohne Cache würde sich der Verbrauch verdoppeln.
/// </summary>
public sealed class BlockchairBlockchainService : IBlockchainService, IDisposable
{
    private const int TxLimit = 50;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(2500);

    /// <summary>Wie lange ein Adress-Abruf wiederverwendet wird.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(9);

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AddressSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRequest = DateTime.MinValue;

    public BlockchairBlockchainService(string? apiKey = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = new HttpClient { BaseAddress = new Uri("https://api.blockchair.com/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
    }

    public async Task<IReadOnlyList<IncomingTransaction>> GetIncomingTransactionsAsync(
        string address, CoinType coin, CancellationToken ct = default)
        => (await GetAddressAsync(address, coin, ct)).Incoming;

    /// <summary>
    /// Guthaben, Transaktionsanzahl und bestätigte Eingänge in einem Request,
    /// innerhalb von <see cref="CacheDuration"/> aus dem Cache.
    /// </summary>
    public async Task<AddressSnapshot> GetAddressAsync(string address, CoinType coin, CancellationToken ct = default)
    {
        var addr = NormalizeAddress(address, coin);
        var key = $"{coin.Symbol()}:{addr}";

        if (TryGetCached(key, out var cached)) return cached;

        var url = $"{coin.BlockchairSlug()}/dashboards/address/{addr}?limit={TxLimit}&transaction_details=true&state=latest";
        if (_apiKey is not null) url += $"&key={Uri.EscapeDataString(_apiKey)}";

        using var doc = await GetJsonAsync(url, ct);
        var snapshot = Parse(doc);

        lock (_cache) _cache[key] = snapshot;
        return snapshot;
    }

    /// <summary>Aktuelle Netzwerk-Difficulty aus /{chain}/stats.</summary>
    public async Task<double?> GetDifficultyAsync(CoinType coin, CancellationToken ct = default)
    {
        var url = $"{coin.BlockchairSlug()}/stats";
        if (_apiKey is not null) url += $"?key={Uri.EscapeDataString(_apiKey)}";

        using var doc = await GetJsonAsync(url, ct);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("difficulty", out var d))
        {
            if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var v)) return v;
            if (d.ValueKind == JsonValueKind.String &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)) return sv;
        }
        return null;
    }

    private bool TryGetCached(string key, [NotNullWhen(true)] out AddressSnapshot? snapshot)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out snapshot) && DateTime.UtcNow - snapshot.FetchedUtc < CacheDuration)
                return true;
        }
        snapshot = null;
        return false;
    }

    private static AddressSnapshot Parse(JsonDocument doc)
    {
        var incoming = new List<IncomingTransaction>();
        long balance = 0;
        var count = 0;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new AddressSnapshot(0, 0, incoming, DateTime.UtcNow);

        // Blockchair schlüsselt nach der Adresse, wie sie in der URL stand
        JsonElement addressData = default;
        foreach (var prop in data.EnumerateObject())
        {
            addressData = prop.Value;
            break;
        }
        if (addressData.ValueKind != JsonValueKind.Object)
            return new AddressSnapshot(0, 0, incoming, DateTime.UtcNow);

        if (addressData.TryGetProperty("address", out var a))
        {
            balance = a.TryGetProperty("balance", out var bal) && bal.TryGetInt64(out var bv) ? bv : 0;
            count = a.TryGetProperty("transaction_count", out var tc) && tc.TryGetInt32(out var cv) ? cv : 0;
        }

        if (addressData.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tx in txs.EnumerateArray())
            {
                if (tx.ValueKind != JsonValueKind.Object) continue;

                var hash = tx.TryGetProperty("hash", out var h) ? h.GetString() : null;
                if (string.IsNullOrEmpty(hash)) continue;

                var change = tx.TryGetProperty("balance_change", out var bc) && bc.TryGetInt64(out var v) ? v : 0;
                if (change <= 0) continue;

                long? blockId = tx.TryGetProperty("block_id", out var b) && b.TryGetInt64(out var bid) && bid > 0 ? bid : null;
                if (blockId is null) continue; // unbestätigt: erst dokumentieren, wenn im Block

                var time = DateTime.UtcNow;
                if (tx.TryGetProperty("time", out var t) && t.GetString() is { } ts &&
                    DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    time = parsed;

                incoming.Add(new IncomingTransaction(hash, time, change / 100_000_000m, blockId));
            }
        }

        return new AddressSnapshot(balance, count, incoming, DateTime.UtcNow);
    }

    /// <summary>Blockchair erwartet BCH-Adressen ohne "bitcoincash:"-Präfix.</summary>
    private static string NormalizeAddress(string address, CoinType coin)
    {
        var a = address.Trim();
        if (coin == CoinType.BitcoinCash && a.StartsWith("bitcoincash:", StringComparison.OrdinalIgnoreCase))
            a = a["bitcoincash:".Length..];
        return a;
    }

    /// <summary>Gedrosselter Abruf, höchstens ein Request alle 2,5 Sekunden.</summary>
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;

            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode is 402 or 429 or 430)
                throw new HttpRequestException(L.T("Blockchair-Limit erreicht (HTTP {0})", (int)resp.StatusCode));
            resp.EnsureSuccessStatusCode();

            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
