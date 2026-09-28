using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>
/// EUR-Kurs über die CoinGecko-API.
///
/// Primär <c>/coins/{id}/market_chart/range</c> mit einem Fenster von ±2 Stunden
/// um den Zuflusszeitpunkt. Laut Doku liefert der Endpunkt für Zeiträume bis
/// 1 Tag Stundenwerte, innerhalb der letzten 24 Stunden 5-Minuten-Werte. Genommen
/// wird der Punkt, der dem Zuflusszeitpunkt am nächsten liegt.
///
/// Rückfall: <c>/coins/{id}/history</c> — das ist ein Schnappschuss um 00:00 UTC
/// des Tages (kein Tagesdurchschnitt), Datumsformat laut Doku dd-mm-yyyy.
///
/// Ohne Key bzw. mit Demo-Key reicht der Zugriff nur 365 Tage zurück. Ältere
/// Kurse müssen manuell eingetragen werden.
/// </summary>
public sealed class CoinGeckoPriceService : IPriceService, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(364);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public CoinGeckoPriceService(string? demoApiKey = null)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.coingecko.com/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
        if (!string.IsNullOrWhiteSpace(demoApiKey))
            _http.DefaultRequestHeaders.Add("x-cg-demo-api-key", demoApiKey.Trim());
    }

    public async Task<(PriceQuote? Quote, string? FailureReason)> GetEurPriceAsync(
        CoinType coin, DateTime atUtc, CancellationToken ct = default)
    {
        atUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc);

        if (DateTime.UtcNow - atUtc > MaxAge)
            return (null, L.T("Älter als 365 Tage – CoinGecko liefert ohne Bezahl-Plan keine Daten, bitte Kurs manuell eintragen."));

        try
        {
            var quote = await FromRangeAsync(coin, atUtc, ct) ?? await FromHistoryAsync(coin, atUtc, ct);
            if (quote is null)
                return (null, L.T("CoinGecko lieferte keinen Kurs – wird beim nächsten Durchlauf erneut versucht."));
            return (quote, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, L.T("Kursabfrage fehlgeschlagen ({0}) – wird erneut versucht.", ex.Message));
        }
    }

    /// <summary>Kurspunkt aus der Zeitreihe, der dem Zeitpunkt am nächsten liegt.</summary>
    private async Task<PriceQuote?> FromRangeAsync(CoinType coin, DateTime atUtc, CancellationToken ct)
    {
        var from = new DateTimeOffset(atUtc - Window).ToUnixTimeSeconds();
        var toTime = atUtc + Window;
        if (toTime > DateTime.UtcNow) toTime = DateTime.UtcNow;
        var to = new DateTimeOffset(toTime).ToUnixTimeSeconds();

        var url = $"api/v3/coins/{coin.CoinGeckoId()}/market_chart/range?vs_currency=eur&from={from}&to={to}";
        using var doc = await GetJsonAsync(url, ct);
        if (doc is null) return null;

        if (!doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array)
            return null;

        var targetMs = new DateTimeOffset(atUtc).ToUnixTimeMilliseconds();
        long bestMs = 0;
        decimal? bestPrice = null;
        var bestDistance = long.MaxValue;

        foreach (var point in prices.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;
            if (!TryNumber(point[0], out var msDec) || !TryNumber(point[1], out var price)) continue;

            var ms = (long)msDec;
            var distance = Math.Abs(ms - targetMs);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMs = ms;
                bestPrice = price;
            }
        }

        if (bestPrice is null) return null;

        var pointTime = DateTimeOffset.FromUnixTimeMilliseconds(bestMs).UtcDateTime;
        var minutes = (int)Math.Round(TimeSpan.FromMilliseconds(bestDistance).TotalMinutes);
        return new PriceQuote(bestPrice.Value, pointTime,
            $"CoinGecko Zeitreihe, Kurspunkt {pointTime:yyyy-MM-dd HH:mm} UTC ({minutes} min Abstand)");
    }

    /// <summary>Rückfall: Schnappschuss um 00:00 UTC des Tages.</summary>
    private async Task<PriceQuote?> FromHistoryAsync(CoinType coin, DateTime atUtc, CancellationToken ct)
    {
        var date = atUtc.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var url = $"api/v3/coins/{coin.CoinGeckoId()}/history?date={date}&localization=false";

        using var doc = await GetJsonAsync(url, ct);
        if (doc is null) return null;

        if (doc.RootElement.TryGetProperty("market_data", out var md) &&
            md.TryGetProperty("current_price", out var cp) &&
            cp.TryGetProperty("eur", out var eur) &&
            TryNumber(eur, out var value))
        {
            return new PriceQuote(value, atUtc.Date,
                L.T("CoinGecko Tagesschnappschuss 00:00 UTC ({0:d}), Rückfallwert", atUtc));
        }

        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryNumber(JsonElement el, out decimal value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        if (el.TryGetDecimal(out value)) return true;
        if (!el.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
        value = (decimal)d;
        return true;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
