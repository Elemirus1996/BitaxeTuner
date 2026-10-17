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
    public void Heat_rule_follows_room_temperature_with_a_band_and_waits_without_reading()
    {
        // 0.9.21 Miner als Heizung
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        double? room = 18;
        var d = new DeviceConfig { Name = "G", Host = "10.0.0.1" };
        d.Presets = [new("Voll", 750, 1260), new("Spar", 500, 1100)];
        d.Schedule.Enabled = true; d.Schedule.Mode = "heat"; d.Schedule.HeatSensor = "pico:wz"; d.Schedule.HeatTargetC = 20; d.Schedule.HeatHysteresisC = 0.5;
        d.Schedule.CheapPreset = "Voll"; d.Schedule.ExpensivePreset = "Spar";
        d.Schedule.Approve(d.Host);
        var asked = new List<string>();
        var engine = new AutomationEngine(null) { RoomTemp = (key, _) => { asked.Add(key); return room; } };

        var cold = engine.Evaluate(d, I(500, 1100), Gamma, now, false, false);
        Assert.Equal(750, cold.Action!.FrequencyMhz);                     // zu kalt → volle Leistung
        Assert.Contains("Raum", cold.Status);
        Assert.Equal("pico:wz", asked[0]);
        room = 20.3;
        Assert.Null(engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(20), false, false).Action);   // im Band: bleibt
        room = 20.6;
        Assert.Equal(500, engine.Evaluate(d, I(750, 1260), Gamma, now.AddMinutes(40), false, false).Action!.FrequencyMhz);   // warm → Sparen
        room = null;
        var unknown = engine.Evaluate(d, I(500, 1100), Gamma, now.AddMinutes(60), false, false);
        Assert.Null(unknown.Action);
        Assert.Single(unknown.Notices);

        // Ziel, Band und Fühler gehören zur Freigabe
        d.Schedule.HeatTargetC = 21;
        Assert.False(d.Schedule.IsApproved(d.Host));
    }

    [Fact]
    public async Task Room_temperature_comes_from_a_pico_sensor_or_home_assistant()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir, new Fake(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("sensor.wz_temp")
            ? """{"state":"19.4","attributes":{"unit_of_measurement":"°C"}}""" : "{}")));
        hub.SaveSolarSettings(Ha, "token", "Test");
        hub.Config.Devices.Add(new DeviceConfig { Name = "G", Host = "10.0.0.1" });
        hub.Config.Devices[0].Schedule.Enabled = true;
        hub.Config.Devices[0].Schedule.Mode = "heat";
        hub.Config.Devices[0].Schedule.HeatSensor = "ha:sensor.wz_temp";
        var now = DateTime.Now;
        Assert.Null(hub.RoomTemp("ha:sensor.wz_temp", now));
        await hub.PollRoomTempsAsync(["ha:sensor.wz_temp"], now);
        Assert.Equal(19.4, hub.RoomTemp("ha:sensor.wz_temp", now.AddMinutes(5)));
        Assert.Null(hub.RoomTemp("ha:sensor.wz_temp", now.AddMinutes(11)));   // zu alt
        Assert.Contains(hub.RoomSensors(now), s => s.Key == "ha:sensor.wz_temp");
        Assert.Null(hub.RoomTemp("pico:fehlt", now));
        Assert.Null(hub.RoomTemp("anders", now));
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
