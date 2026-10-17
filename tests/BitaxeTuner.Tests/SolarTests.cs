using System.Net;
using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Plugs;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Tests;

/// <summary>0.9.17: PV-Leistung als Automatik-Quelle (Messsteckdose oder Home Assistant) und Ratgeber-Ziel „Stromsparen“.</summary>
public class SolarTests
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private static MinerInfo I(int f, int mv) => new() { FrequencyMhz = f, CoreVoltageMv = mv, ChipTempC = 50, VrTempC = 50 };

    private sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (code, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    private static MinerHub Hub(TempDir dir, HttpMessageHandler handler) =>
        new(new AppConfig(), new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false }) { SolarHttpHandler = handler };

    private static readonly SolarSettings Ha = new() { Source = "homeassistant", HaUrl = "http://ha.local:8123/proxy/", HaEntity = "sensor.pv_power" };

    [Fact]
    public async Task Home_assistant_sensor_is_read_in_watts_with_bearer_token()
    {
        using var dir = new TempDir();
        var unit = "kW";
        var fake = new Fake(_ => (HttpStatusCode.OK, $$$"""{"state":"1.25","attributes":{"unit_of_measurement":"{{{unit}}}"}}"""));
        using var hub = Hub(dir, fake);

        // Ohne Token: verständlicher Fehler, keine Abfrage
        await Assert.ThrowsAsync<LocalizedException>(() => hub.ReadHomeAssistantAsync(Ha));
        Assert.Empty(fake.Requests);

        hub.SaveSolarSettings(Ha, "geheim", "Test");
        Assert.Equal(1250, await hub.ReadHomeAssistantAsync(hub.Config.Solar));
        var req = fake.Requests.Single();
        Assert.Equal("http://ha.local:8123/proxy/api/states/sensor.pv_power", req.RequestUri!.ToString());   // Pfad bleibt erhalten
        Assert.Equal("Bearer geheim", req.Headers.Authorization!.ToString());
        Assert.DoesNotContain("geheim", File.ReadAllText(Path.Combine(dir.Path, "config.json")));     // Token nur in secrets.json

        unit = "W";
        Assert.Equal(1.25, await hub.ReadHomeAssistantAsync(hub.Config.Solar));
    }

    [Fact]
    public async Task Home_assistant_errors_are_explained()
    {
        using var dir = new TempDir();
        var code = HttpStatusCode.Unauthorized;
        var body = "{}";
        using var hub = Hub(dir, new Fake(_ => (code, body)));
        hub.SaveSolarSettings(Ha, "x", "Test");

        Assert.Contains("Zugriffstoken", (await Assert.ThrowsAsync<LocalizedException>(() => hub.ReadHomeAssistantAsync(hub.Config.Solar))).Message);
        code = HttpStatusCode.NotFound;
        Assert.Contains("sensor.pv_power", (await Assert.ThrowsAsync<LocalizedException>(() => hub.ReadHomeAssistantAsync(hub.Config.Solar))).Message);
        code = HttpStatusCode.OK;
        body = """{"state":"unavailable"}""";
        Assert.Contains("keinen Zahlenwert", (await Assert.ThrowsAsync<LocalizedException>(() => hub.ReadHomeAssistantAsync(hub.Config.Solar))).Message);

        // Ungültige Eingaben werden beim Speichern abgelehnt
        Assert.Throws<LocalizedException>(() => hub.SaveSolarSettings(new SolarSettings { Source = "homeassistant", HaUrl = "ftp://x", HaEntity = "sensor.a" }, null, "Test"));
        Assert.Throws<LocalizedException>(() => hub.SaveSolarSettings(new SolarSettings { Source = "homeassistant", HaUrl = "http://x", HaEntity = "pv" }, null, "Test"));
        Assert.Throws<LocalizedException>(() => hub.SaveSolarSettings(new SolarSettings { Source = "plug", PlugId = "fehlt" }, null, "Test"));
        Assert.Throws<LocalizedException>(() => hub.SaveSolarSettings(new SolarSettings { Source = "anker" }, null, "Test"));
    }

    [Fact]
    public async Task Forecast_from_open_meteo_sends_only_a_rounded_location_and_gives_an_outlook()
    {
        // 0.9.21 PV-Prognose
        using var dir = new TempDir();
        var today = DateTime.Today;
        string H(int h) => today.AddHours(h).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var hours = Enumerable.Range(0, 24).ToList();
        // Einstrahlung: 9–15 Uhr 600 W/m², sonst 50
        var body = "{\"hourly\":{\"time\":[" + string.Join(",", hours.Select(h => $"\"{H(h)}\"")) + "],\"global_tilted_irradiance\":[" +
                   string.Join(",", hours.Select(h => h is >= 9 and < 15 ? "600" : "50")) + "]}}";
        var urls = new List<string>();
        using var hub = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false })
        {
            SolarForecastHandler = new Fake(r => { urls.Add(r.RequestUri!.ToString()); return (HttpStatusCode.OK, body); }),
        };
        Assert.Throws<LocalizedException>(() => hub.SaveSolarSettings(new SolarSettings { ForecastEnabled = true, PeakKw = 1 }, null, "Test"));   // ohne Standort
        hub.SaveSolarSettings(new SolarSettings { ForecastEnabled = true, Latitude = 52.516274, Longitude = 13.377704, PeakKw = 0.8, TiltDeg = 35, AzimuthDeg = -45 }, null, "Test");

        await hub.FetchSolarForecastAsync(today.AddHours(8));
        var url = Assert.Single(urls);
        Assert.Contains("latitude=52.52&longitude=13.38", url);           // nur auf ~1 km gerundet
        Assert.Contains("tilt=35&azimuth=-45", url);
        Assert.DoesNotContain("52.5162", url);
        // 600 W/m² × 0,8 kWp × 0,85 = 408 W
        Assert.Equal(408, hub.SolarForecastW(today.AddHours(10).AddMinutes(30)));
        Assert.Equal(300, hub.SolarOutlookThresholdW());                  // ohne PV-Automatik: 300 W
        Assert.Equal((today.AddHours(9), today.AddHours(15)), hub.SolarOutlook(today.AddHours(8), 300));
        Assert.Null(hub.SolarOutlook(today.AddHours(16), 300));

        // Fehler: Hinweis statt Absturz
        hub.SolarForecastHandler = new Fake(_ => (HttpStatusCode.InternalServerError, "x"));
        await hub.FetchSolarForecastAsync(today.AddHours(9));
        Assert.NotNull(hub.SolarForecast.Error);
        Assert.Equal(408, hub.SolarForecastW(today.AddHours(10)));         // alte Prognose bleibt
    }

    [Fact]
    public void Short_clouds_are_bridged_for_twenty_minutes_when_the_forecast_is_sunny()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        double? pv = 500;
        double? forecast = 600;
        var d = new DeviceConfig { Name = "G", Host = "10.0.0.1" };
        d.Presets = [new("Voll", 750, 1260), new("Spar", 500, 1100)];
        d.Schedule.Enabled = true; d.Schedule.Mode = "solar"; d.Schedule.SolarHighW = 300; d.Schedule.SolarLowW = 100;
        d.Schedule.CheapPreset = "Voll"; d.Schedule.ExpensivePreset = "Spar";
        d.Schedule.Approve(d.Host);
        var engine = new AutomationEngine(null) { SolarPowerW = _ => pv, SolarForecastW = _ => forecast };

        Assert.Equal(750, engine.Evaluate(d, I(500, 1100), Gamma, now, false, false).Action!.FrequencyMhz);
        pv = 40;                                                          // Wolke
        var cloud = engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(11), false, false);
        Assert.Null(cloud.Action);
        Assert.Contains("Wolke", cloud.Status);
        Assert.Null(engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(25), false, false).Action);   // 14 min: noch warten
        Assert.Equal(500, engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(32), false, false).Action!.FrequencyMhz);   // 21 min: herunter

        // Prognose ebenfalls trüb → sofort herunter
        var e2 = new AutomationEngine(null) { SolarPowerW = _ => pv, SolarForecastW = _ => 50 };
        pv = 500;
        e2.Evaluate(d, I(500, 1100), Gamma, now, false, false);
        pv = 40;
        Assert.Equal(500, e2.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(11), false, false).Action!.FrequencyMhz);
    }

    [Fact]
    public void Solar_rule_switches_with_hysteresis_and_waits_when_unknown()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        double? pv = 500;
        var d = new DeviceConfig { Name = "G", Host = "10.0.0.1" };
        d.Presets = [new("Voll", 750, 1260), new("Spar", 500, 1100)];
        d.Schedule.Enabled = true; d.Schedule.Mode = "solar"; d.Schedule.SolarHighW = 300; d.Schedule.SolarLowW = 100;
        d.Schedule.CheapPreset = "Voll"; d.Schedule.ExpensivePreset = "Spar";
        d.Schedule.Approve(d.Host);
        var engine = new AutomationEngine(null) { SolarPowerW = _ => pv };

        // Viel Sonne → volle Leistung
        var r = engine.Evaluate(d, I(500, 1100), Gamma, now, false, false);
        Assert.Equal(750, r.Action!.FrequencyMhz);
        Assert.Contains("PV", r.Status);

        // Zwischen den Schwellen → bleibt bei „Voll“
        pv = 200;
        Assert.Null(engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(20), false, false).Action);

        // Wenig Sonne → Stromsparen
        pv = 50;
        Assert.Equal(500, engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(40), false, false).Action!.FrequencyMhz);

        // Unbekannt → nichts ändern, nur Hinweis
        pv = null;
        var unknown = engine.Evaluate(d, I(500, 1100), Gamma, now.AddMinutes(60), false, false);
        Assert.Null(unknown.Action);
        Assert.Single(unknown.Notices);

        // Schwellen gehören zur Freigabe; andere Arten behalten ihre Prüfsumme
        d.Schedule.SolarHighW = 400;
        Assert.False(d.Schedule.IsApproved(d.Host));
        d.Schedule.Mode = "price";
        var sig = d.Schedule.Signature(d.Host);
        d.Schedule.SolarLowW = 10;
        Assert.Equal(sig, d.Schedule.Signature(d.Host));
    }

    [Fact]
    public void Power_save_goal_picks_the_lowest_stable_power()
    {
        static StepResult R(int f, int mv, double gh, double w) => new()
        {
            FrequencyMhz = f, CoreVoltageMv = mv, AvgHashRateGh = gh, AvgPowerW = w, Outcome = StepOutcome.Stable, Timestamp = DateTime.Now.AddHours(-1),
        };
        var results = new[] { R(400, 1100, 850, 12.0), R(525, 1150, 1100, 18.0), R(700, 1300, 1450, 29.0) };
        var r = EfficiencyAdvisor.Evaluate("h", "Gamma", Gamma, 600, 1200, 1250, 22.5, "aktuell", results, [], 30, AdvisorGoal.PowerSave);
        Assert.Equal((400, 1100), (r.Recommended!.FrequencyMhz, r.Recommended.CoreVoltageMv));
        Assert.True(r.Recommended.DeltaW < 0);

        // Schon fast am Minimum (< 3 % weniger) → lohnt nicht
        var close = EfficiencyAdvisor.Evaluate("h", "Gamma", Gamma, 410, 1100, 860, 12.2, "aktuell", results, [], 30, AdvisorGoal.PowerSave);
        Assert.Null(close.Recommended);
    }

    [Fact]
    public void Pv_plug_is_not_counted_as_consumption()
    {
        var miners = new Dictionary<string, double> { ["a"] = 15 };
        var pv = new SmartPlugConfig { Role = "pv" };
        var total = new SmartPlugConfig { Role = "total" };
        var e = EnergyBalance.Compute(new SmartPlugSettings { Items = [pv, total] }, miners, p => p == pv ? 800 : 40);
        Assert.Equal(40, e.TotalPowerW);
        Assert.Contains("pv", SmartPlugConfig.Roles);
    }
}
