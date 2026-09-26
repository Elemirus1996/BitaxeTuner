using System.Net;
using System.Net.Http;
using System.Text;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Web;

namespace BitaxeTuner.Tests;

public class AutomationEngineTests
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private static MinerInfo I(int f, int mv, double temp, double vr = 60) => new() { FrequencyMhz = f, CoreVoltageMv = mv, ChipTempC = temp, VrTempC = vr };

    private static DeviceConfig Device(Action<DeviceConfig> setup)
    {
        var d = new DeviceConfig { Name = "G", Host = "10.0.0.1" };
        setup(d);
        return d;
    }

    [Fact]
    public void Rules_act_only_when_approved_and_unchanged()
    {
        var d = Device(x => { x.ThermalGuard.Enabled = true; x.ThermalGuard.Minutes = 1; });
        var engine = new AutomationEngine(null);
        var t = new DateTime(2026, 9, 25, 12, 0, 0);

        engine.Evaluate(d, I(600, 1200, 70), Gamma, t, false, false);
        Assert.Null(engine.Evaluate(d, I(600, 1200, 70), Gamma, t.AddMinutes(2), false, false).Action); // nicht freigegeben

        d.ThermalGuard.Approve(d.Host);
        d.ThermalGuard.MaxChipTempC = 75;                    // nach Freigabe geändert → Freigabe ungültig
        Assert.False(d.ThermalGuard.IsApproved(d.Host));
        d.ThermalGuard.MaxChipTempC = 65;                    // zurück auf den freigegebenen Inhalt
        Assert.True(d.ThermalGuard.IsApproved(d.Host));
    }

    [Fact]
    public void Thermal_guard_steps_down_after_hold_time_and_recovers_when_cool()
    {
        var d = Device(x =>
        {
            x.ThermalGuard.Enabled = true; x.ThermalGuard.Minutes = 5; x.ThermalGuard.StepMhz = 25;
            x.ThermalGuard.MinFrequencyMhz = 550; x.ThermalGuard.RecoverMinutes = 60;
        });
        d.ThermalGuard.Approve(d.Host);
        var engine = new AutomationEngine(null);
        var t = new DateTime(2026, 9, 25, 12, 0, 0);

        Assert.Null(engine.Evaluate(d, I(600, 1200, 68), Gamma, t, false, false).Action);
        Assert.Null(engine.Evaluate(d, I(600, 1200, 68), Gamma, t.AddMinutes(4), false, false).Action);
        var a = engine.Evaluate(d, I(600, 1200, 68), Gamma, t.AddMinutes(5), false, false).Action;
        Assert.Equal((575, 1200), (a!.FrequencyMhz, a.CoreVoltageMv));
        Assert.Equal(600, engine.GuardOriginalFrequency(d.Host));

        // noch zu heiß → nach weiteren 5 min nächste Stufe, dann Minimum (550) und Hinweis
        Assert.Equal(550, engine.Evaluate(d, I(575, 1200, 67), Gamma, t.AddMinutes(10), false, false).Action!.FrequencyMhz);
        var atMin = engine.Evaluate(d, I(550, 1200, 67), Gamma, t.AddMinutes(15), false, false);
        Assert.Null(atMin.Action);
        Assert.Contains(atMin.Notices, n => n.Message.Contains("Minimum"));

        // kühl (≤ 60 °C) → nach 60 min schrittweise zurück bis 600
        var cool = t.AddMinutes(20);
        Assert.Null(engine.Evaluate(d, I(550, 1200, 58), Gamma, cool, false, false).Action);
        Assert.Equal(575, engine.Evaluate(d, I(550, 1200, 58), Gamma, cool.AddMinutes(60), false, false).Action!.FrequencyMhz);
        Assert.Equal(600, engine.Evaluate(d, I(575, 1200, 58), Gamma, cool.AddMinutes(120), false, false).Action!.FrequencyMhz);
        Assert.Null(engine.GuardOriginalFrequency(d.Host));
    }

    [Fact]
    public void Nothing_happens_while_busy_or_in_maintenance()
    {
        var d = Device(x => { x.ThermalGuard.Enabled = true; x.ThermalGuard.Minutes = 1; });
        d.ThermalGuard.Approve(d.Host);
        var engine = new AutomationEngine(null);
        var t = DateTime.Now;
        engine.Evaluate(d, I(600, 1200, 70), Gamma, t, false, false);
        Assert.Null(engine.Evaluate(d, I(600, 1200, 70), Gamma, t.AddMinutes(5), busy: true, false).Action);
        Assert.Null(engine.Evaluate(d, I(600, 1200, 70), Gamma, t.AddMinutes(6), false, maintenance: true).Action);
        // Wartung setzt die Überschreitungsdauer zurück
        Assert.Null(engine.Evaluate(d, I(600, 1200, 70), Gamma, t.AddMinutes(6.5), false, false).Action);
    }

    [Fact]
    public void Time_schedule_sets_matching_preset_with_gap_and_retry_limit()
    {
        var d = Device(x =>
        {
            x.Presets = [new("Hashrate", 700, 1250), new("Effizienz", 525, 1150)];
            x.Schedule.Enabled = true;
            x.Schedule.Entries = [new ScheduleEntry { Days = 127, FromHour = 22, ToHour = 6, Preset = "Hashrate" }];
            x.Schedule.DefaultPreset = "Effizienz";
        });
        d.Schedule.Approve(d.Host);
        var engine = new AutomationEngine(null);
        var night = new DateTime(2026, 9, 25, 23, 0, 0);

        var a = engine.Evaluate(d, I(525, 1150, 50), Gamma, night, false, false).Action;
        Assert.Equal((700, 1250, "Zeitplan"), (a!.FrequencyMhz, a.CoreVoltageMv, a.Rule));
        // Miner übernimmt nicht: frühestens nach 10 min erneut, höchstens 3 Versuche
        Assert.Null(engine.Evaluate(d, I(525, 1150, 50), Gamma, night.AddMinutes(5), false, false).Action);
        Assert.NotNull(engine.Evaluate(d, I(525, 1150, 50), Gamma, night.AddMinutes(11), false, false).Action);
        Assert.NotNull(engine.Evaluate(d, I(525, 1150, 50), Gamma, night.AddMinutes(22), false, false).Action);
        var stuck = engine.Evaluate(d, I(525, 1150, 50), Gamma, night.AddMinutes(33), false, false);
        Assert.Null(stuck.Action);
        Assert.Contains(stuck.Notices, n => n.Message.Contains("übernimmt"));

        // Morgens (Folgetag 5 Uhr, gehört zum Vortags-Fenster) noch Hashrate, ab 6 Uhr Standard
        Assert.Null(engine.Evaluate(d, I(700, 1250, 50), Gamma, night.AddHours(6), false, false).Action);
        var day = engine.Evaluate(d, I(700, 1250, 50), Gamma, night.AddHours(8), false, false).Action;
        Assert.Equal(525, day!.FrequencyMhz);
    }

    [Fact]
    public void Price_rule_uses_threshold_and_refuses_presets_outside_limits()
    {
        var cfg = new PriceSourceSettings { Source = "awattar-de" };
        var prices = new PriceService(() => cfg, new HttpClient());
        var now = new DateTime(2026, 9, 25, 12, 30, 0, DateTimeKind.Local);
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Local).ToUniversalTime();
        prices.SetPrices([new PricePoint(hour, hour.AddHours(1), 12.5)]);

        var d = Device(x =>
        {
            x.Presets = [new("Voll", 750, 1260), new("Spar", 500, 1100), new("Zu viel", 1200, 1400)];
            x.Schedule.Enabled = true; x.Schedule.Mode = "price"; x.Schedule.ThresholdCt = 15;
            x.Schedule.CheapPreset = "Voll"; x.Schedule.ExpensivePreset = "Spar";
        });
        d.Schedule.Approve(d.Host);
        var engine = new AutomationEngine(prices);
        Assert.Equal(750, engine.Evaluate(d, I(500, 1100, 50), Gamma, now, false, false).Action!.FrequencyMhz);

        d.Schedule.CheapPreset = "Zu viel";
        d.Schedule.Approve(d.Host);
        var r = new AutomationEngine(prices).Evaluate(d, I(500, 1100, 50), Gamma, now, false, false);
        Assert.Null(r.Action);
        Assert.Contains(r.Notices, n => n.Message.Contains("außerhalb der Grenzen"));
    }

    [Theory]
    [InlineData(2026, 9, 26, 23, true)]   // Samstag 23 Uhr, Sa aktiv
    [InlineData(2026, 9, 27, 3, true)]    // Sonntag 3 Uhr gehört zum Samstagabend
    [InlineData(2026, 9, 27, 23, false)]  // Sonntag 23 Uhr, So nicht aktiv
    [InlineData(2026, 9, 26, 3, false)]   // Samstag 3 Uhr gehört zum Freitag (nicht aktiv)
    public void Overnight_entry_respects_weekdays(int y, int m, int d, int h, bool expected)
    {
        var e = new ScheduleEntry { Days = 1 << (int)DayOfWeek.Saturday, FromHour = 22, ToHour = 6 };
        Assert.Equal(expected, e.Matches(new DateTime(y, m, d, h, 0, 0)));
    }
}

public class PriceSourceTests
{
    [Fact]
    public void Parses_awattar_response_to_ct_per_kwh()
    {
        var p = AwattarPriceSource.Parse("""{"object":"list","data":[{"start_timestamp":1790370000000,"end_timestamp":1790373600000,"marketprice":186.76,"unit":"Eur/MWh"}]}""");
        Assert.Equal(18.676, Assert.Single(p).CtPerKwh, 3);
        Assert.Equal(TimeSpan.FromHours(1), p[0].EndUtc - p[0].StartUtc);
    }

    [Fact]
    public void Parses_tibber_response_and_reports_errors()
    {
        var json = """{"data":{"viewer":{"homes":[{"currentSubscription":{"priceInfo":{"today":[{"total":0.2874,"startsAt":"2026-09-25T00:00:00.000+02:00"}],"tomorrow":[]}}}]}}}""";
        var p = Assert.Single(TibberPriceSource.Parse(json));
        Assert.Equal(28.74, p.CtPerKwh, 3);
        Assert.Equal(new DateTime(2026, 9, 24, 22, 0, 0), p.StartUtc);
        Assert.Throws<InvalidOperationException>(() => TibberPriceSource.Parse("""{"errors":[{"message":"invalid token"}]}"""));
    }
}

public class SoakTests
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private static MinerInfo I(double hash, int f = 600, int mv = 1200, double temp = 55, double err = 0.5) =>
        new() { HashRateGh = hash, ExpectedHashRateGh = 1224, FrequencyMhz = f, CoreVoltageMv = mv, ChipTempC = temp, VrTempC = 60, ErrorPercent = err };

    [Fact]
    public void Passes_when_stable_until_end()
    {
        var start = new DateTime(2026, 9, 25, 12, 0, 0);
        var soak = new SoakTestState(start, start.AddHours(2), 600, 1200);
        var m = new SoakMonitor();
        for (var t = start; t < soak.Until; t = t.AddMinutes(1))
            Assert.Equal(SoakOutcome.Running, m.Feed(soak, I(1200), Gamma, t, false).Outcome);
        Assert.Equal(SoakOutcome.Passed, m.Feed(soak, I(1200), Gamma, soak.Until, false).Outcome);
    }

    [Fact]
    public void Fails_on_low_hashrate_after_warmup_and_aborts_on_changed_setting()
    {
        var start = new DateTime(2026, 9, 25, 12, 0, 0);
        var soak = new SoakTestState(start, start.AddHours(24), 600, 1200);
        var m = new SoakMonitor();
        SoakResult r = new(SoakOutcome.Running, "");
        for (var i = 0; i <= 30 && r.Outcome == SoakOutcome.Running; i++)
            r = m.Feed(soak, I(1000), Gamma, start.AddMinutes(i), false); // 82 %
        Assert.Equal(SoakOutcome.Failed, r.Outcome);
        Assert.Contains("Hashrate", r.Message);

        Assert.Equal(SoakOutcome.Aborted, new SoakMonitor().Feed(soak, I(1200, f: 625), Gamma, start.AddMinutes(1), false).Outcome);
    }

    [Fact]
    public void Suggests_next_lower_stable_setting()
    {
        var results = new[]
        {
            new StepResult { FrequencyMhz = 550, CoreVoltageMv = 1150, Outcome = StepOutcome.Stable },
            new StepResult { FrequencyMhz = 575, CoreVoltageMv = 1190, Outcome = StepOutcome.Stable },
            new StepResult { FrequencyMhz = 575, CoreVoltageMv = 1170, Outcome = StepOutcome.Stable },
            new StepResult { FrequencyMhz = 600, CoreVoltageMv = 1200, Outcome = StepOutcome.Stable },
            new StepResult { FrequencyMhz = 590, CoreVoltageMv = 1150, Outcome = StepOutcome.Unstable },
        };
        Assert.Equal((575, 1170), SoakMonitor.SuggestLower(results, 600));
        Assert.Null(SoakMonitor.SuggestLower(results, 550));
    }
}

public class WebViewServerTests
{
    private static WebViewServer.Request Get(string path, string? cookie = null) =>
        new("GET", path, cookie is null ? new() : new() { ["cookie"] = cookie }, "");

    [Fact]
    public void Status_requires_pin_login_and_locks_after_five_failures()
    {
        var server = new WebViewServer(() => WebViewSettings.HashPin("4711"), () => """{"ok":true}""");
        var now = DateTime.UtcNow;

        Assert.Equal(401, server.Handle(Get("/api/status"), "10.0.0.5", now).Status);
        Assert.Contains("PIN", Encoding.UTF8.GetString(server.Handle(Get("/"), "10.0.0.5", now).Body));

        for (var i = 0; i < 5; i++)
            Assert.Equal(401, server.Handle(new("POST", "/login", new(), "pin=0000"), "10.0.0.5", now).Status);
        Assert.Equal(429, server.Handle(new("POST", "/login", new(), "pin=4711"), "10.0.0.5", now).Status); // gesperrt
        Assert.Equal(401, server.Handle(new("POST", "/login", new(), "pin=0000"), "10.0.0.6", now).Status); // andere Adresse unabhängig

        var ok = server.Handle(new("POST", "/login", new(), "pin=4711"), "10.0.0.5", now.AddMinutes(6));
        Assert.Equal(303, ok.Status);
        var cookie = ok.Headers!["Set-Cookie"].Split(';')[0];
        Assert.Contains("HttpOnly", ok.Headers["Set-Cookie"]);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(server.Handle(Get("/api/status", cookie), "10.0.0.5", now.AddMinutes(6)).Body));

        Assert.Equal(401, server.Handle(Get("/api/status", cookie), "10.0.0.5", now.AddDays(8)).Status); // Sitzung abgelaufen
    }

    [Fact]
    public void Empty_pin_never_logs_in_and_there_is_no_write_endpoint()
    {
        var server = new WebViewServer(() => "", () => "{}");
        Assert.Equal(401, server.Handle(new("POST", "/login", new(), "pin="), "10.0.0.5", DateTime.UtcNow).Status);
        foreach (var path in new[] { "/api/system", "/api/apply", "/api/restart" })
            Assert.Equal(404, server.Handle(new("POST", path, new(), ""), "10.0.0.5", DateTime.UtcNow).Status);
    }

    [Theory]
    [InlineData("192.168.178.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Only_private_addresses_are_allowed(string ip, bool allowed) =>
        Assert.Equal(allowed, WebViewServer.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task Serves_real_http_over_tcp()
    {
        using var server = new WebViewServer(() => WebViewSettings.HashPin("1234"), () => "{}");
        server.Start(0, IPAddress.Loopback);
        Assert.True(server.IsRunning, server.LastError);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var login = await http.PostAsync($"http://127.0.0.1:{server.Port}/login", new FormUrlEncodedContent([new("pin", "1234")]));
        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        var page = await http.GetAsync($"http://127.0.0.1:{server.Port}/");
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").First());
    }
}

public class ScheduleDaysTests
{
    [Theory]
    [InlineData("täglich", 127, "täglich")]
    [InlineData("Mo-Fr", 0b0111110, "Mo-Fr")]
    [InlineData("sa,so", 0b1000001, "Sa,So")]
    [InlineData("Fr-Mo", 0b1100011, "Mo,Fr,Sa,So")]
    [InlineData("Mi", 0b0001000, "Mi")]
    public void Parses_and_formats_weekdays(string input, int mask, string text)
    {
        var e = new ScheduleEntry { DaysText = input };
        Assert.Equal(mask, e.Days);
        Assert.Equal(text, e.DaysText);
    }

    [Fact]
    public void Invalid_days_keep_previous_value() =>
        Assert.Equal(0b0111110, new ScheduleEntry { Days = 0b0111110, DaysText = "Montag bis Freitag" }.Days);
}
