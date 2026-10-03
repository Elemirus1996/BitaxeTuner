using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Automation;

/// <summary>Strompreis einer Stunde in ct/kWh.</summary>
public sealed record PricePoint(DateTime StartUtc, DateTime EndUtc, double CtPerKwh);

public interface IPriceSource
{
    string Name { get; }
    Task<IReadOnlyList<PricePoint>> GetPricesAsync(CancellationToken ct);
}

/// <summary>
/// aWATTar Day-Ahead-Börsenpreis (DE/AT), öffentlich, ohne Konto. <c>GET https://api.awattar.{de|at}/v1/marketdata</c>
/// liefert <c>{"data":[{"start_timestamp":ms,"end_timestamp":ms,"marketprice":€/MWh}]}</c> (geprüft 25.09.2026).
/// Achtung: reiner Börsenpreis ohne Netzentgelte und Steuern.
/// </summary>
public sealed class AwattarPriceSource(HttpClient http, string country) : IPriceSource
{
    public string Name => L.T("aWATTar {0} (Börsenpreis netto)", country.ToUpperInvariant());

    public async Task<IReadOnlyList<PricePoint>> GetPricesAsync(CancellationToken ct)
    {
        var json = await http.GetStringAsync($"https://api.awattar.{country}/v1/marketdata", ct).ConfigureAwait(false);
        return Parse(json);
    }

    public static List<PricePoint> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PricePoint>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in data.EnumerateArray())
        {
            if (!e.TryGetProperty("start_timestamp", out var s) || !e.TryGetProperty("end_timestamp", out var en) ||
                !e.TryGetProperty("marketprice", out var p)) continue;
            list.Add(new PricePoint(
                DateTimeOffset.FromUnixTimeMilliseconds(s.GetInt64()).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(en.GetInt64()).UtcDateTime,
                p.GetDouble() / 10.0)); // €/MWh → ct/kWh
        }
        return list;
    }
}

/// <summary>
/// Tibber (persönlicher API-Token). GraphQL <c>POST https://api.tibber.com/v1-beta/gql</c>,
/// <c>priceInfo { today/tomorrow { total startsAt } }</c> – Endpreis inkl. Steuern in €/kWh (Währung des Vertrags).
/// </summary>
public sealed class TibberPriceSource(HttpClient http, string token) : IPriceSource
{
    public string Name => L.T("Tibber (Endpreis)");

    private const string Query =
        "{ viewer { homes { currentSubscription { priceInfo { today { total startsAt } tomorrow { total startsAt } } } } } }";

    public async Task<IReadOnlyList<PricePoint>> GetPricesAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tibber.com/v1-beta/gql")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = Query }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403) throw new InvalidOperationException(L.T("Tibber-Token ungültig"));
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static List<PricePoint> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PricePoint>();
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException("Tibber: " + errors[0].GetProperty("message").GetString());
        var homes = doc.RootElement.GetProperty("data").GetProperty("viewer").GetProperty("homes");
        foreach (var home in homes.EnumerateArray())
        {
            if (!home.TryGetProperty("currentSubscription", out var sub) || sub.ValueKind != JsonValueKind.Object) continue;
            var info = sub.GetProperty("priceInfo");
            var raw = new List<(DateTime Start, double Ct)>();
            foreach (var day in new[] { "today", "tomorrow" })
            {
                if (!info.TryGetProperty(day, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in arr.EnumerateArray())
                    raw.Add((DateTimeOffset.Parse(e.GetProperty("startsAt").GetString()!, CultureInfo.InvariantCulture).UtcDateTime,
                             e.GetProperty("total").GetDouble() * 100));
            }
            // Audit F4: Ende = Beginn des nächsten Eintrags (Stunden- oder Viertelstundenpreise); letzter wie der vorige, sonst 1 h
            raw = raw.OrderBy(r => r.Start).ToList();
            for (var i = 0; i < raw.Count; i++)
            {
                var length = i + 1 < raw.Count ? raw[i + 1].Start - raw[i].Start
                    : i > 0 ? raw[i].Start - raw[i - 1].Start : TimeSpan.FromHours(1);
                if (length <= TimeSpan.Zero || length > TimeSpan.FromHours(1)) length = TimeSpan.FromHours(1);
                list.Add(new PricePoint(raw[i].Start, raw[i].Start + length, raw[i].Ct));
            }
            break; // erstes Zuhause mit Vertrag
        }
        return list;
    }
}

/// <summary>Hält die Preise der gewählten Quelle vor (Abruf höchstens alle 30 min, bei Fehler alle 5 min).</summary>
public sealed class PriceService(Func<PriceSourceSettings> settings, HttpClient http)
{
    private IReadOnlyList<PricePoint> _prices = [];
    private DateTime _fetched = DateTime.MinValue;
    private string? _sourceKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? LastError { get; private set; }
    public string SourceName { get; private set; } = L.T("keine");
    public IReadOnlyList<PricePoint> Prices => _prices;

    public static IPriceSource? Create(PriceSourceSettings s, HttpClient http) => s.Source switch
    {
        "awattar-de" => new AwattarPriceSource(http, "de"),
        "awattar-at" => new AwattarPriceSource(http, "at"),
        "tibber" when !string.IsNullOrWhiteSpace(s.TibberToken) => new TibberPriceSource(http, s.TibberToken),
        _ => null,
    };

    /// <summary>Preis zur Zeit <paramref name="utcNow"/>, null wenn unbekannt.</summary>
    public double? PriceAt(DateTime utcNow) =>
        _prices.FirstOrDefault(p => p.StartUtc <= utcNow && utcNow < p.EndUtc)?.CtPerKwh;

    public async Task RefreshAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var s = settings();
        var key = s.Source + "|" + s.TibberToken;
        var stale = _prices.Count == 0 || PriceAt(utcNow) is null;
        var age = utcNow - _fetched;
        if (key == _sourceKey && (age < TimeSpan.FromMinutes(5) || (!stale && LastError is null && age < TimeSpan.FromMinutes(30))))
            return;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            _sourceKey = key;
            _fetched = utcNow;
            var source = Create(s, http);
            if (source is null)
            {
                _prices = [];
                SourceName = L.T("keine");
                LastError = s.Source == "tibber" ? L.T("Tibber-Token fehlt") : null;
                return;
            }
            SourceName = source.Name;
            _prices = await source.GetPricesAsync(ct).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Nur für Tests.</summary>
    internal void SetPrices(IReadOnlyList<PricePoint> prices, string name = "Test")
    {
        _prices = prices;
        SourceName = name;
        _fetched = DateTime.UtcNow;
        _sourceKey = settings().Source + "|" + settings().TibberToken;
    }
}
