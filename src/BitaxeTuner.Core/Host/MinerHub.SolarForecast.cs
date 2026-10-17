using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Erwartete PV-Leistung einer Stunde (Ortszeit, Beginn der Stunde).</summary>
public sealed record SolarForecastHour(DateTime Hour, double ExpectedW);

/// <summary>Stand der PV-Prognose: Stunden, Abrufzeit, Fehler.</summary>
public sealed record SolarForecastStatus(IReadOnlyList<SolarForecastHour> Hours, DateTime? FetchedAt, string? Error);

/// <summary>
/// 0.9.21: PV-Prognose von Open-Meteo (kostenlos, ohne Konto). Gesendet wird nur der auf zwei Nachkommastellen gerundete
/// Standort (etwa 1 km) sowie Neigung und Ausrichtung der Module; zurück kommt die Einstrahlung auf die Modulebene je Stunde.
/// Erwartete Leistung = Einstrahlung (W/m²) / 1000 × Anlagengröße (kWp) × 0,85 (übliche Verluste). Stündlich neu.
/// Nutzen: die PV-Automatik übersteht kurze Wolken (Messwert kurz niedrig, Prognose hoch → höchstens 20 min warten) und
/// zeigt eine Vorschau („heute etwa 10–16 Uhr volle Leistung“).
/// </summary>
public sealed partial class MinerHub
{
    public const double SolarPerformanceRatio = 0.85;
    public static readonly TimeSpan SolarCloudGrace = TimeSpan.FromMinutes(20);
    private List<SolarForecastHour> _solarForecast = [];
    private DateTime? _solarForecastAt;
    private string? _solarForecastError;
    private DateTime _solarForecastTried = DateTime.MinValue;
    private bool _solarForecastBusy;

    /// <summary>Für Tests: eigener HTTP-Handler für Open-Meteo.</summary>
    internal HttpMessageHandler? SolarForecastHandler { get; set; }

    public SolarForecastStatus SolarForecast => new(_solarForecast, _solarForecastAt, _solarForecastError);

    private bool SolarForecastConfigured(SolarSettings s) =>
        s.ForecastEnabled && s.Latitude is not null && s.Longitude is not null && s.PeakKw > 0;

    /// <summary>Erwartete PV-Leistung der laufenden Stunde (null = keine Prognose).</summary>
    public double? SolarForecastW(DateTime now)
    {
        if (!SolarForecastConfigured(Config.Solar)) return null;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        return _solarForecast.FirstOrDefault(h => h.Hour == hour)?.ExpectedW;
    }

    /// <summary>Für die Automatik: Prognose nur, wenn „kurze Wolken überbrücken“ eingeschaltet ist.</summary>
    private double? SolarForecastForAutomation(DateTime now) => Config.Solar.SmoothClouds ? SolarForecastW(now) : null;

    /// <summary>Zeitraum (heute bzw. ab jetzt bis Mitternacht), in dem die Prognose mindestens <paramref name="highW"/> erwartet.</summary>
    public (DateTime From, DateTime To)? SolarOutlook(DateTime now, double highW)
    {
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var sunny = _solarForecast.Where(h => h.Hour >= start && h.Hour.Date == now.Date && h.ExpectedW >= highW).Select(h => h.Hour).ToList();
        return sunny.Count == 0 ? null : (sunny.Min(), sunny.Max().AddHours(1));
    }

    /// <summary>Schwelle „viel Sonne“ für die Vorschau: die niedrigste der PV-Automatiken (Miner und Gruppen), sonst 300 W.</summary>
    public double SolarOutlookThresholdW()
    {
        var highs = Config.Devices.Select(d => d.Schedule).Concat(Config.GroupSchedules.Select(g => g.Schedule))
            .Where(s => s.Enabled && s.Mode == "solar").Select(s => Math.Max(s.SolarHighW, s.SolarLowW)).ToList();
        return highs.Count > 0 ? highs.Min() : 300;
    }

    /// <summary>Aus dem Abfragetakt: höchstens stündlich neu holen (nach Fehler frühestens nach 15 min).</summary>
    private void TickSolarForecast(DateTime now)
    {
        var s = Config.Solar;
        if (!SolarForecastConfigured(s) || _solarForecastBusy) return;
        if (!Options.OnlineChecks && SolarForecastHandler is null) return;
        var due = _solarForecastAt is not { } at || now - at >= TimeSpan.FromHours(1) || at.Date != now.Date;
        if (!due || now - _solarForecastTried < TimeSpan.FromMinutes(15)) return;
        _solarForecastTried = now;
        _ = FetchSolarForecastAsync(now);
    }

    internal static string SolarForecastUrl(SolarSettings s) => string.Create(CultureInfo.InvariantCulture,
        $"https://api.open-meteo.com/v1/forecast?latitude={Math.Round(s.Latitude!.Value, 2):0.##}&longitude={Math.Round(s.Longitude!.Value, 2):0.##}" +
        $"&hourly=global_tilted_irradiance&tilt={Math.Clamp(s.TiltDeg, 0, 90)}&azimuth={Math.Clamp(s.AzimuthDeg, -180, 180)}&timezone=auto&forecast_days=2");

    internal async Task FetchSolarForecastAsync(DateTime now)
    {
        _solarForecastBusy = true;
        try
        {
            var s = Config.Solar;
            using var http = SolarForecastHandler is { } h ? new HttpClient(h, false) : new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(SolarForecastUrl(s)));
            var hourly = doc.RootElement.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().Select(t => DateTime.ParseExact(t.GetString()!, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)).ToList();
            var gti = hourly.GetProperty("global_tilted_irradiance").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToList();
            var list = new List<SolarForecastHour>();
            for (var i = 0; i < Math.Min(times.Count, gti.Count); i++)
                list.Add(new SolarForecastHour(times[i], Math.Round(Math.Max(0, gti[i]) / 1000 * s.PeakKw * 1000 * SolarPerformanceRatio)));
            _solarForecast = list;
            _solarForecastAt = now;
            _solarForecastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            _solarForecastError = L.T("PV-Prognose nicht abrufbar: {0}", ex.Message);
        }
        finally
        {
            _solarForecastBusy = false;
        }
    }
}
