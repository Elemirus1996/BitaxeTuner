using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Aktueller Stand der PV-Quelle.</summary>
public sealed record SolarStatus(string Source, double? PowerW, DateTime? At, string? Error);

/// <summary>
/// 0.9.17: PV-Leistung für die Automatik-Art „PV-Leistung“ (viel Sonne → volle Leistung, wenig Sonne → Stromsparen).
/// Quelle wählbar: eingetragene Messsteckdose (Shelly, Tasmota, FRITZ!DECT) oder Home-Assistant-Sensor (auch Anker Solix
/// über dessen HA-Integration). Home Assistant wird höchstens alle 30 Sekunden abgefragt; ein Wert gilt 5 Minuten.
/// </summary>
public sealed partial class MinerHub
{
    private static readonly TimeSpan SolarMaxAge = TimeSpan.FromMinutes(5);
    private (double PowerW, DateTime At)? _solarHa;
    private string? _solarError;
    private DateTime _solarPolledAt = DateTime.MinValue;
    private bool _solarBusy;
    private HttpClient? _solarHttp;

    /// <summary>Für Tests: eigener HTTP-Handler für Home Assistant.</summary>
    internal HttpMessageHandler? SolarHttpHandler { get; set; }

    [GeneratedRegex(@"^[a-z_]+\.[a-z0-9_]+$")]
    private static partial Regex HaEntityPattern();

    /// <summary>Aktuelle PV-Leistung in W (null = unbekannt oder zu alt).</summary>
    public double? SolarPowerW(DateTime now)
    {
        var s = Config.Solar;
        return s.Source switch
        {
            "plug" => PlugSolarW(s.PlugId, now),
            "homeassistant" => _solarHa is { } v && now - v.At <= SolarMaxAge ? v.PowerW : null,
            _ => null,
        };
    }

    /// <summary>Frischer Messwert einer Messsteckdose als PV-Leistung (Betrag, W) – für „Testen“.</summary>
    public double? PlugSolarW(string plugId, DateTime now) => FreshReading(plugId, now) is { } r ? Math.Abs(r.PowerW) : null;

    public SolarStatus SolarStatusNow(DateTime now) => new(Config.Solar.Source, SolarPowerW(now),
        Config.Solar.Source == "homeassistant" ? _solarHa?.At : null, Config.Solar.Source == "none" ? null : _solarError);

    /// <summary>Aus dem Abfragetakt: Home Assistant höchstens alle 30 Sekunden abfragen.</summary>
    private void TickSolar(DateTime now)
    {
        if (Config.Solar.Source != "homeassistant" || _solarBusy || now - _solarPolledAt < TimeSpan.FromSeconds(30)) return;
        if (!Options.OnlineChecks && SolarHttpHandler is null) return;
        _solarPolledAt = now;
        _ = PollSolarAsync(now);
    }

    private async Task PollSolarAsync(DateTime now)
    {
        _solarBusy = true;
        try
        {
            var w = await ReadHomeAssistantAsync(Config.Solar);
            _solarHa = (w, now);
            _solarError = null;
        }
        catch (Exception ex) when (ex is LocalizedException or HttpRequestException or TaskCanceledException or JsonException)
        {
            _solarError = ex.Message;
        }
        finally
        {
            _solarBusy = false;
        }
    }

    /// <summary>Sensor aus Home Assistant lesen (REST /api/states/&lt;entity&gt;), Ergebnis in W.</summary>
    public async Task<double> ReadHomeAssistantAsync(SolarSettings s, string? token = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate((s.HaUrl ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new LocalizedException("Home Assistant: bitte die Adresse mit http:// oder https:// eintragen.");
        if (!HaEntityPattern().IsMatch((s.HaEntity ?? "").Trim()))
            throw new LocalizedException("Home Assistant: Sensor im Format „sensor.name“ eintragen.");
        if (string.IsNullOrWhiteSpace(token)) token = Secrets.Get(SolarTokenKey);
        if (string.IsNullOrWhiteSpace(token)) throw new LocalizedException("Home Assistant: Zugriffstoken fehlt.");
        _solarHttp ??= SolarHttpHandler is { } h ? new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) } : new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/states/" + s.HaEntity.Trim()));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        using var resp = await _solarHttp.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new LocalizedException("Home Assistant lehnt das Zugriffstoken ab.");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new LocalizedException("Home Assistant kennt den Sensor „{0}“ nicht.", s.HaEntity);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : null;
        if (!double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new LocalizedException("Home Assistant meldet für „{0}“ keinen Zahlenwert ({1}).", s.HaEntity, state ?? "–");
        var unit = doc.RootElement.TryGetProperty("attributes", out var a) && a.TryGetProperty("unit_of_measurement", out var u) ? u.GetString() : "W";
        return Math.Abs(unit?.Trim().ToLowerInvariant() switch { "kw" => value * 1000, "mw" => value * 1_000_000, _ => value });
    }

    public const string SolarTokenKey = "Solar.HaToken";

    /// <summary>Einstellungen prüfen und bereinigt zurückgeben (Server und Desktop).</summary>
    public static SolarSettings NormalizeSolar(SolarSettings s, IEnumerable<SmartPlugConfig> plugs)
    {
        if (!SolarSettings.Sources.Contains(s.Source)) throw new LocalizedException("Unbekannte PV-Quelle: {0}", s.Source);
        if (s.Source == "plug" && plugs.All(p => p.Id != s.PlugId))
            throw new LocalizedException("Bitte die Messsteckdose mit der PV-Leistung auswählen.");
        if (s.Source == "homeassistant")
        {
            if (!Uri.TryCreate((s.HaUrl ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                throw new LocalizedException("Home Assistant: bitte die Adresse mit http:// oder https:// eintragen.");
            if (!HaEntityPattern().IsMatch((s.HaEntity ?? "").Trim()))
                throw new LocalizedException("Home Assistant: Sensor im Format „sensor.name“ eintragen.");
        }
        return new SolarSettings { Source = s.Source, PlugId = (s.PlugId ?? "").Trim(), HaUrl = (s.HaUrl ?? "").Trim().TrimEnd('/'), HaEntity = (s.HaEntity ?? "").Trim() };
    }

    /// <summary>Übernehmen (ohne zu speichern); Token nur, wenn neu eingegeben (leer = unverändert). Setzt den Lesestand zurück.</summary>
    public void ApplySolarSettings(SolarSettings normalized, string? newToken)
    {
        if (!string.IsNullOrWhiteSpace(newToken)) Secrets.Set(SolarTokenKey, newToken.Trim());
        Config.Solar = normalized;
        _solarHa = null;
        _solarError = null;
        _solarPolledAt = DateTime.MinValue;
    }

    /// <summary>Einstellungen prüfen und speichern (Server).</summary>
    public void SaveSolarSettings(SolarSettings s, string? newToken, string by)
    {
        ApplySolarSettings(NormalizeSolar(s, Config.Plugs.Items), newToken);
        Config.Save();
        LogEvent(null, Monitoring.EventCategories.Settings, L.T("PV-Quelle gespeichert: {0} – {1}", s.Source, by));
    }
}
