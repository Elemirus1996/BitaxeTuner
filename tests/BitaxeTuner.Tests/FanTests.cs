using System.Text;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

public class FanControllerTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 14, 0, 0);

    private static MinerTemps M(string host, double vr, double asic = 55, bool online = true, int ageSeconds = 3) =>
        new(host, "Miner " + host, online, vr, asic, Now.AddSeconds(-ageSeconds));

    private static FanSettings Settings(Action<FanSettings>? edit = null)
    {
        var s = new FanSettings { Enabled = true };
        s.Channel(1).Role = "miner";
        s.Channel(1).MinerHost = "10.0.0.1";
        edit?.Invoke(s);
        return s;
    }

    [Theory]
    [InlineData(40, 25)]   // unter Start: Mindestwert
    [InlineData(50, 30)]   // Start
    [InlineData(60, 65)]   // Mitte: 30 + 0,5 × 70
    [InlineData(70, 100)]  // Volllast
    [InlineData(85, 100)]
    public void Curve_is_linear_between_start_and_full(double temp, int expected) =>
        Assert.Equal(expected, FanController.Evaluate(new FanCurve(), temp));

    [Fact]
    public void Hysteresis_keeps_speed_until_temperature_dropped_enough()
    {
        var c = new FanController();
        var s = Settings();
        Assert.Equal(65, c.Compute(s, [M("10.0.0.1", 60)], Now)[0].Percent);
        Assert.Equal(65, c.Compute(s, [M("10.0.0.1", 59)], Now)[0].Percent);   // 1 °C kühler: bleibt
        Assert.Equal(57, c.Compute(s, [M("10.0.0.1", 55.7)], Now)[0].Percent); // über 2 °C: runter auf Wert für 57,7 °C
        Assert.Equal(79, c.Compute(s, [M("10.0.0.1", 64)], Now)[0].Percent);   // wärmer: sofort hoch
    }

    [Fact]
    public void Offline_stale_or_unassigned_goes_to_full_speed_even_in_manual_mode()
    {
        var c = new FanController();
        var s = Settings(x => { x.Channel(1).Mode = "manual"; x.Channel(1).ManualPercent = 30; });
        Assert.Equal(30, c.Compute(s, [M("10.0.0.1", 60)], Now)[0].Percent);
        var offline = c.Compute(s, [M("10.0.0.1", 60, online: false)], Now)[0];
        Assert.Equal(100, offline.Percent);
        Assert.Contains("offline", offline.Reason);
        Assert.Equal(100, c.Compute(s, [M("10.0.0.1", 60, ageSeconds: 45)], Now)[0].Percent);
        Assert.Equal(100, c.Compute(s, [], Now)[0].Percent);
        Assert.All(c.Compute(s, [M("10.0.0.1", 60)], Now).Skip(1), t => Assert.Equal(100, t.Percent)); // nicht belegt
    }

    [Fact]
    public void Manual_low_speed_is_overridden_when_the_miner_is_too_hot()
    {
        // Audit H3: manuell 0 % bei heißem Miner → trotzdem 100 % ab der Volllast-Temperatur der Kurve (70 °C)
        var c = new FanController();
        var s = Settings(x => { x.Channel(1).Mode = "manual"; x.Channel(1).ManualPercent = 0; });
        Assert.Equal(0, c.Compute(s, [M("10.0.0.1", 60)], Now)[0].Percent);
        var hot = c.Compute(s, [M("10.0.0.1", 72)], Now)[0];
        Assert.Equal(100, hot.Percent);
        Assert.True(hot.SafetyOverride);
        Assert.Contains("Sicherheit", hot.Reason);
        Assert.Equal(0, c.Compute(s, [M("10.0.0.1", 65)], Now)[0].Percent);   // wieder kühl: manueller Wert
    }

    [Fact]
    public void Manual_case_fans_go_full_when_the_case_sensor_is_too_hot()
    {
        var c = new FanController();
        var s = Settings(x => { x.Channel(1).Role = "case"; x.Case.Mode = "manual"; x.Case.ManualPercent = 20; x.Case.Sensor = "case"; });
        c.CaseTemperature = 30;
        Assert.Equal(20, c.Compute(s, [], Now)[0].Percent);
        c.CaseTemperature = s.Case.Curve.FullTemp + 1;
        Assert.Equal(100, c.Compute(s, [], Now)[0].Percent);
    }

    [Fact]
    public void Missing_vr_uses_asic_temperature()
    {
        var t = new FanController().Compute(Settings(), [new MinerTemps("10.0.0.1", "A", true, null, 60, Now)], Now)[0];
        Assert.Equal(65, t.Percent);
        Assert.Contains("ASIC", t.Reason);
    }

    [Fact]
    public void Case_group_follows_hottest_selected_miner_and_respects_night_cap()
    {
        var s = Settings(x =>
        {
            x.Channel(4).Role = "case";
            x.Channel(5).Role = "case";
            x.Case.Curve = new FanCurve { StartTemp = 45, StartPercent = 30, FullTemp = 70, MinPercent = 25, Hysteresis = 0 };
        });
        var miners = new[] { M("10.0.0.1", 50), M("10.0.0.2", 60), M("10.0.0.3", 66) };

        var day = new FanController().Compute(s, miners, Now);
        Assert.Equal(day[3].Percent, day[4].Percent);
        Assert.Equal(FanController.Evaluate(s.Case.Curve, 66), day[3].Percent);

        s.Case.Miners = ["10.0.0.1", "10.0.0.2"]; // nur die kühleren
        Assert.Equal(FanController.Evaluate(s.Case.Curve, 60), new FanController().Compute(s, miners, Now)[3].Percent);

        s.Case.NightEnabled = true;
        s.Case.NightMaxPercent = 40;
        var at23 = Now.Date.AddHours(23);
        MinerTemps Fresh(MinerTemps m) => m with { LastOk = at23 };
        var night = new FanController().Compute(s, miners.Select(Fresh).ToList(), at23)[3];
        Assert.Equal(40, night.Percent);
        Assert.Contains("Nacht", night.Reason);
        // Volllast-Temperatur erreicht: Nachtbetrieb gilt nicht
        Assert.Equal(100, new FanController().Compute(s, [Fresh(M("10.0.0.1", 72)), Fresh(M("10.0.0.2", 50))], at23)[3].Percent);
    }

    [Fact]
    public void Case_group_uses_unknown_percent_when_a_miner_has_no_data()
    {
        var s = Settings(x => { x.Channel(4).Role = "case"; x.Case.UnknownPercent = 80; });
        var t = new FanController().Compute(s, [M("10.0.0.1", 50), M("10.0.0.2", 50, online: false)], Now)[3];
        Assert.Equal(80, t.Percent);
        Assert.Contains("Miner 10.0.0.2", t.Reason);
    }

    [Theory]
    [InlineData(22, 7, 23, true)]
    [InlineData(22, 7, 3, true)]
    [InlineData(22, 7, 12, false)]
    [InlineData(1, 5, 3, true)]
    public void Night_window_wraps_midnight(int from, int to, int hour, bool night) =>
        Assert.Equal(night, FanController.IsNight(new CaseFanSettings { NightFromHour = from, NightToHour = to }, Now.Date.AddHours(hour)));
}

/// <summary>Nachgebildeter Pico: MicroPython-Raw-REPL und unser Lüfterprogramm.</summary>
internal sealed class FakePico : ILineTransport
{
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _in = new();
    private bool _raw;
    public bool ProgramRunning { get; set; }
    public string? MainPy => Files.GetValueOrDefault("main.py");
    /// <summary>Über das Raw-REPL geschriebene Dateien (main.py, btcfg.json).</summary>
    public Dictionary<string, string> Files { get; } = [];
    private readonly StringBuilder _file = new();
    private string _fileName = "";
    public List<string> SetCommands { get; } = [];
    private List<byte>? _image;
    public byte[]? Shown { get; private set; }
    public string? NextRpmLine { get; set; }

    /// <summary>Taster am Pico (unaufgefordert gesendete Zeile).</summary>
    public void Press(string evt) => _out.Append(evt + "\r\n");

    public void Write(string text)
    {
        foreach (var ch in text) Feed(ch);
    }

    private void Feed(char ch)
    {
        if (ch == '\x03') { ProgramRunning = false; _in.Clear(); _out.Append("\r\n>>> "); return; }
        if (!ProgramRunning && ch == '\x01') { _raw = true; _in.Clear(); _out.Append("raw REPL; CTRL-B to exit\r\n>"); return; }
        if (_raw && ch == '\x04') { ExecRaw(_in.ToString()); _in.Clear(); return; }
        if (ch == '\x02') { _raw = false; _in.Clear(); return; }
        if (!_raw && ch == '\x04') { ProgramRunning = MainPy is not null; _in.Clear(); return; } // Soft-Reset
        if (!_raw && ProgramRunning && ch is '\n' or '\r')
        {
            var line = _in.ToString().Trim();
            _in.Clear();
            if (line == "HELLO") _out.Append(Files.TryGetValue("btcfg.json", out var cfg) && cfg.Contains("\"role\":\"display\"")
                ? $"OK BTFAN {PicoFanDevice.FirmwareVersion} 0 display\r\n" : $"OK BTFAN {PicoFanDevice.FirmwareVersion} 6 fans\r\n");
            else if (line == "NET") _out.Append(Files.ContainsKey("btcfg.json") ? "OK NET 1 192.0.2.10\r\n" : "OK NET 0 -\r\n");
            else if (line.StartsWith("SET ")) { SetCommands.Add(line); _out.Append((NextRpmLine ?? "RPM 1200 1300 0 0 0 0") + "\r\n"); }
            else if (line == "GET") _out.Append("RPM 0 0 0 0 0 0\r\n");
            else if (line.StartsWith("IMG ")) { _image = new List<byte>(); _out.Append("OK IMG\r\n"); }
            else if (line.StartsWith("D ")) _image?.AddRange(Convert.FromBase64String(line[2..]));
            else if (line == "SHOW") { Shown = _image?.ToArray(); _out.Append("OK SHOW\r\n"); }
            return;
        }
        _in.Append(ch);
    }

    private void ExecRaw(string code)
    {
        code = code.TrimStart('\r');
        var open = Regex.Match(code, @"^f=open\('([^']+)','w'\)$");
        if (open.Success) { _file.Clear(); _fileName = open.Groups[1].Value; }
        else if (code == "f.close()") Files[_fileName] = _file.ToString();
        else
        {
            var m = Regex.Match(code, @"^f\.write\('(.*)'\)$", RegexOptions.Singleline);
            if (!m.Success) { _out.Append("OK\x04Traceback: unexpected\x04>"); return; }
            _file.Append(Regex.Unescape(m.Groups[1].Value.Replace("\\'", "'")));
        }
        _out.Append("OK\x04\x04>");
    }

    public string? ReadLine(TimeSpan timeout)
    {
        var s = _out.ToString();
        var i = s.IndexOf('\n');
        if (i < 0) return null;
        _out.Remove(0, i + 1);
        return s[..i].TrimEnd('\r');
    }

    public string? ReadUntil(string marker, TimeSpan timeout)
    {
        var s = _out.ToString();
        var i = s.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        _out.Remove(0, i + marker.Length);
        return s[..(i + marker.Length)];
    }

    public void Discard() => _out.Clear();
    public void Dispose() { }
}

public class PicoProtocolTests
{
    [Fact]
    public async Task Installs_firmware_when_only_micropython_runs_then_talks_to_it()
    {
        var pico = new FakePico();
        var log = new List<string>();
        using var device = PicoFanDevice.Connect(pico, "/dev/ttyACM0", log.Add);

        Assert.Equal(PicoFanDevice.Firmware, pico.MainPy);          // Datei byte-genau übertragen
        Assert.Contains(log, l => l.Contains("wird aufgespielt"));
        Assert.Contains("v" + PicoFanDevice.FirmwareVersion, device.Description);

        var rpm = await device.ExchangeAsync([40, 50, 100, 100, 100, 100]);
        Assert.Equal([1200, 1300, 0, 0, 0, 0], rpm);
        Assert.Equal("SET 40 50 100 100 100 100", pico.SetCommands.Single());
    }

    [Fact]
    public void Running_current_firmware_is_not_reinstalled()
    {
        var pico = new FakePico { ProgramRunning = true };
        using var device = PicoFanDevice.Connect(pico, "COM5");
        Assert.Null(pico.MainPy);
    }

    [Fact]
    public void Python_string_literal_escapes_everything_needed()
    {
        Assert.Equal(@"'a\'b\\c\nd\tä'".Replace("ä", "\\u00e4"), PicoFanDevice.PyString("a'b\\c\nd\tä"));
        Assert.DoesNotContain(PicoFanDevice.Firmware, c => c > 126); // Programm bleibt ASCII
    }
}

public class FanHubTests
{
    [Fact]
    public async Task Switching_fan_control_off_sends_full_speed_instead_of_keeping_the_last_value()
    {
        // Audit N-S1: ein bloßes GET hielte den Pico auf dem letzten Wert (z. B. 0 % nachts)
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var sim = new SimulatedFanDevice();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "A", Host = "10.0.9.1" });
        config.Fans.Enabled = true;
        config.Fans.Channel(1).Role = "miner";
        config.Fans.Channel(1).MinerHost = "10.0.9.1";
        config.Fans.Channel(1).Mode = "manual";
        config.Fans.Channel(1).ManualPercent = 20;
        config.Fans.Channel(1).Curve.FullTemp = 95;
        config.Display.Enabled = true;                  // Anzeige am Lüfter-Pico: Pico bleibt verbunden
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => sim,
        });
        await hub.PollNowAsync();
        await hub.FanTickAsync();
        Assert.Equal(20, sim.LastPercent[0]);

        config.Fans.Enabled = false;
        await hub.FanTickAsync();
        Assert.All(sim.LastPercent, p => Assert.Equal(100, p));
    }

    [Fact]
    public async Task Hub_sends_targets_to_pico_and_reports_stalled_fan()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var sim = new SimulatedFanDevice();
        var now = DateTime.Now;
        var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "t" } };
        config.Devices.Add(new DeviceConfig { Name = "A", Host = "10.0.9.1" });
        config.Fans.Enabled = true;
        config.Fans.Channel(1).Role = "miner";
        config.Fans.Channel(1).MinerHost = "10.0.9.1";
        config.Fans.Channel(1).Mode = "manual";
        config.Fans.Channel(1).ManualPercent = 45;
        config.Fans.Channel(1).Curve.FullTemp = 95;   // simulierter VR ist warm – Sicherheitsgrenze (Audit H3) hier nicht erreichen
        config.Fans.Channel(4).Role = "case";
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => sim,
            Clock = () => now,
        });
        var sent = new List<string>();
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);

        await hub.PollNowAsync();
        now = DateTime.Now;
        await hub.FanTickAsync();

        Assert.True(hub.FanStatus.Connected);
        Assert.Equal(45, sim.LastPercent[0]);
        Assert.Equal(100, sim.LastPercent[1]);                            // nicht belegt
        Assert.Equal(FanController.Evaluate(config.Fans.Case.Curve, hub.Devices[0].State.Info!.vrTemp), sim.LastPercent[3]);
        Assert.Equal(900 + 45 * 60, hub.FanStatus.Channels[0].Rpm);

        sim.Stalled.Add(1);
        for (var i = 0; i < 4; i++)
        {
            now = now.AddSeconds(4);
            await hub.FanTickAsync();
        }
        Assert.True(hub.FanStatus.Channels[0].Stalled);
        Assert.Contains("fanstall:1", sent);

        // Pico getrennt: Status zeigt es, keine Ausnahme
        sim.Fail = true;
        await hub.FanTickAsync();
        Assert.False(hub.FanStatus.Connected);
        Assert.Contains("Verbindung zum Pico verloren", hub.FanStatus.Error);
    }
}

public class FanApiTests
{
    [Fact]
    public async Task Fan_settings_are_validated_saved_and_applied()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "Devices": [ { "Name": "A", "Host": "192.168.70.1" } ] }""");
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var sim = new SimulatedFanDevice();
        using var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(b, s =>
            {
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<BitaxeTuner.Server.ServerSettings>(s);
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(s, new BitaxeTuner.Server.ServerSettings { DataDirectory = dir.Path });
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(s, new MinerHubOptions
                {
                    ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
                    OnlineChecks = false,
                    FanDeviceFactory = _ => sim,
                });
            }));
        var auth = (BitaxeTuner.Server.Security.AuthStore)f.Services.GetService(typeof(BitaxeTuner.Server.Security.AuthStore))!;
        auth.Setup(auth.SetupCode!, "sehr-geheim-123");
        var (_, token) = auth.CreateToken("t");
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var fans = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Text.Json.JsonElement>(c, "/api/v1/fans");
        Assert.False(fans.GetProperty("enabled").GetBoolean());
        var settings = System.Text.Json.Nodes.JsonNode.Parse(fans.GetProperty("settings").GetRawText())!;
        settings["enabled"] = true;
        settings["channels"]![0]!["role"] = "miner";
        settings["channels"]![0]!["minerHost"] = "192.168.70.1";
        settings["channels"]![0]!["curve"]!["fullTemp"] = 40; // unter Start-Temperatur

        var bad = await c.PutAsync("/api/v1/fans", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Volllast", await bad.Content.ReadAsStringAsync());

        settings["channels"]![0]!["curve"]!["fullTemp"] = 72;
        settings["channels"]![0]!["mode"] = "manual";
        settings["channels"]![0]!["manualPercent"] = 55;
        var ok = await c.PutAsync("/api/v1/fans", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        Assert.Equal(55, sim.LastPercent[0]);
        Assert.True(AppConfig.Load(dir.File("config.json")).Fans.Enabled);
    }
}

public class DisplayAndButtonTests
{
    private static DisplayModel Model(bool offline = false) => new("BitaxeTuner", new DateTime(2026, 9, 27, 14, 32, 0), 1670, 29.9, 17.9, offline ? 1 : 2, 2, 24.1,
        "Automatik", false, false,
        [
            new DisplayMiner("Gamma Wohnzimmer", true, false, 1050, 57, 70, 60, false, false, false, null),
            new DisplayMiner("Supra", !offline, false, offline ? null : 620, offline ? null : 58, offline ? null : 71, 45, false, false, false, offline ? "keine Verbindung" : null),
        ],
        offline ? ["Supra offline"] : []);

    private static int RedPixels(byte[] planes) => planes.Skip(48000).Sum(b => System.Numerics.BitOperations.PopCount(b));
    private static int BlackPixels(byte[] planes) => planes.Take(48000).Sum(b => 8 - System.Numerics.BitOperations.PopCount(b));

    [Fact]
    public void Renders_two_planes_with_red_only_for_warnings()
    {
        var ok = StatusRenderer.Render(Model());
        Assert.Equal(PicoFanDevice.ImageBytes, ok.Length);
        Assert.True(BlackPixels(ok) > 5000, "Text muss sichtbar sein");
        Assert.Equal(0, RedPixels(ok));

        var bad = StatusRenderer.Render(Model(offline: true));
        Assert.True(RedPixels(bad) > 500, "Offline-Miner und Warnung in Rot");
    }

    [Fact]
    public void Umlauts_and_degree_sign_render_without_missing_glyphs()
    {
        // „Lüfter“ und „°C“ gezeichnet: gleiche Breite wie ohne Zeichen wäre ein Hinweis auf fehlende Glyphen
        var a = StatusRenderer.Render(Model() with { FanMode = "Automatik · Gehäuse 45 %" });
        var b = StatusRenderer.Render(Model() with { FanMode = "Automatik · Gehause 45 %" });
        Assert.NotEqual(BlackPixels(a), BlackPixels(b));
    }

    [Fact]
    public void Off_override_keeps_safety_rules()
    {
        var now = DateTime.Now;
        var s = new FanSettings { Enabled = true };
        s.Channel(1).Role = "miner";
        s.Channel(1).MinerHost = "a";
        s.Channel(2).Role = "miner";
        s.Channel(2).MinerHost = "b";
        var c = new FanController { Override = FanOverride.Off };
        var t = c.Compute(s, [new MinerTemps("a", "A", true, 60, 55, now), new MinerTemps("b", "B", true, 75, 60, now)], now);
        Assert.Equal(0, t[0].Percent);
        Assert.Equal(100, t[1].Percent);                         // VR über 100-%-Punkt der Kurve
        Assert.True(t[1].SafetyOverride);
        Assert.Equal(100, c.Compute(s, [new MinerTemps("a", "A", false, null, null, null)], now)[0].Percent); // offline

        c.Override = FanOverride.Full;
        Assert.All(c.Compute(s, [new MinerTemps("a", "A", true, 40, 40, now), new MinerTemps("b", "B", true, 40, 40, now)], now).Take(2),
            x => Assert.Equal(100, x.Percent));
    }

    [Fact]
    public async Task Pico_receives_image_and_reports_buttons()
    {
        var pico = new FakePico { ProgramRunning = true };
        using var device = PicoFanDevice.Connect(pico, "COM5");
        var planes = StatusRenderer.Render(Model());
        pico.Press("BTN 3");
        await device.ShowImageAsync(planes);
        Assert.Equal(planes, pico.Shown);
        pico.Press("BTN 4 LONG");
        pico.Press("BTN 3 LONG");
        await device.ExchangeAsync([50, 50, 50, 50, 50, 50]);
        Assert.Equal(["BTN 3", "BTN 4 LONG", "BTN 3 LONG"], device.DrainEvents());
        Assert.Empty(device.DrainEvents());
    }

    [Fact]
    public async Task Hub_handles_buttons_and_limits_display_refreshes()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var sim = new SimulatedFanDevice();
        var now = DateTime.Now;
        var rebooted = 0;
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "A", Host = "10.0.8.1" });
        config.Fans.Enabled = true;
        config.Fans.Channel(1).Role = "miner";
        config.Fans.Channel(1).MinerHost = "10.0.8.1";
        config.Display.Enabled = true;
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => sim,
            Clock = () => now,
            SystemReboot = () => { rebooted++; return Task.CompletedTask; },
        });
        await hub.PollNowAsync();
        now = DateTime.Now;
        await hub.FanTickAsync();
        Assert.Single(sim.Images);                                     // erstes Bild sofort

        sim.Press("BTN 3 LONG");                                       // Taste 3, 5 s gehalten = aus
        await hub.FanTickAsync();
        Assert.Equal(FanOverride.Off, hub.FanOverride);
        await hub.FanTickAsync();
        Assert.Contains(sim.LastPercent[0], new[] { 0, 100 });          // aus – oder Sicherheitsregel bei heißem VR
        Assert.Single(sim.Images);                                     // Mindestpause 3 min: noch kein neues Bild

        now = now.AddMinutes(3).AddSeconds(1);
        await hub.FanTickAsync();
        Assert.Equal(2, sim.Images.Count);                             // Änderung („Aus“) nach der Pause angezeigt

        // Taste 1: Anzeige weiter – nach 30 s statt 3 min, höchstens 10-mal pro Stunde
        sim.Press("BTN 1");
        now = now.AddSeconds(10);
        await hub.FanTickAsync();
        Assert.Equal(2, sim.Images.Count);
        now = now.AddSeconds(25);
        await hub.FanTickAsync();
        Assert.Equal(3, sim.Images.Count);
        Assert.Equal(FanOverride.Off, hub.FanOverride);                // Taste 1 ändert die Lüfter nicht mehr
        for (var i = 0; i < 12; i++)
        {
            sim.Press("BTN 1");
            now = now.AddSeconds(31);
            await hub.FanTickAsync();
        }
        Assert.Equal(2 + MinerHub.DisplayUserRefreshPerHour, sim.Images.Count);   // 10 per Taste in der Stunde, dann wieder 3 min

        sim.Press("BTN 3");
        await hub.FanTickAsync();
        await hub.FanTickAsync();
        Assert.Equal(100, sim.LastPercent[0]);
        sim.Press("BTN 2");
        await hub.FanTickAsync();
        Assert.Equal(FanOverride.None, hub.FanOverride);

        sim.Press("BTN 4 LONG");
        await hub.FanTickAsync();
        for (var i = 0; i < 20 && rebooted == 0; i++) await Task.Delay(20);
        Assert.Equal(1, rebooted);
        Assert.Equal(1, sim.Resets);
    }
}

public class CaseSensorTests
{
    [Fact]
    public void Case_group_can_follow_the_case_sensor()
    {
        var now = DateTime.Now;
        var s = new FanSettings { Enabled = true };
        s.Channel(4).Role = "case";
        s.Case.Sensor = "case";
        s.Case.Curve = new FanCurve { StartTemp = 30, StartPercent = 30, FullTemp = 45, MinPercent = 20, Hysteresis = 0 };
        var c = new FanController { CaseTemperature = 37.5 };
        var t = c.Compute(s, [], now)[3];
        Assert.Equal(FanController.Evaluate(s.Case.Curve, 37.5), t.Percent);
        Assert.Contains("Gehäuse 37,5 °C", t.Reason);

        c.CaseTemperature = null;                                  // Fühler fehlt/defekt
        Assert.Equal(s.Case.UnknownPercent, c.Compute(s, [], now)[3].Percent);
    }

    private const string Psu = "28ff641e0f16044a", Room = "28aa11b2c3d4e5f6";

    [Fact]
    public async Task Pico_reports_each_sensor_with_its_rom_id()
    {
        var pico = new FakePico { ProgramRunning = true };
        using var device = PicoFanDevice.Connect(pico, "COM5");
        pico.NextRpmLine = $"RPM 1000 0 0 0 0 0 T {Psu}=31.5 {Room.ToUpperInvariant()}=29.25";
        var rpm = await device.ExchangeAsync([50, 50, 50, 50, 50, 50]);
        Assert.Equal(1000, rpm[0]);
        Assert.Equal([new TempReading(Psu, 31.5), new TempReading(Room, 29.25)], device.Temperatures);
    }

    [Fact]
    public void Old_firmware_format_is_still_understood()
    {
        Assert.Equal([new TempReading("#1", 31.5), new TempReading("#2", 29.25)], PicoFanDevice.ParseTemperatures("31.5 29.25"));
        Assert.Empty(PicoFanDevice.ParseTemperatures("x=abc 85x"));
    }

    private static (MinerHub Hub, SimulatedFanDevice Sim, List<string> Sent, Func<DateTime> Now, Action<int> Advance, TempDir Dir) Hub(Action<AppConfig>? edit = null)
    {
        var dir = new TempDir();
        var sim = new SimulatedFanDevice();
        var now = DateTime.Now;
        var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "t" } };
        config.Fans.Enabled = true;
        config.Fans.Channel(4).Role = "case";
        config.Fans.Case.Sensor = "case";
        edit?.Invoke(config);
        config.Save(Path.Combine(dir.Path, "config.json"));
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            FanDeviceFactory = _ => sim,
            Clock = () => now,
        });
        var sent = new List<string>();
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        return (hub, sim, sent, () => now, s => now = now.AddSeconds(s), dir);
    }

    [Fact]
    public async Task New_sensors_are_registered_named_and_warn_individually()
    {
        var (hub, sim, sent, _, advance, dir) = Hub(c => c.Fans.CaseTempWarn = 50);
        using var _d = dir;
        using var _h = hub;
        sim.Sensors.Add(new TempReading(Psu, 38));
        sim.Sensors.Add(new TempReading(Room, 30));
        await hub.FanTickAsync();   // Verbindung, erste Messung
        advance(2);
        await hub.FanTickAsync();

        Assert.Equal([Psu, Room], hub.Config.Fans.Sensors.Select(s => s.Id));
        Assert.Equal(["Fühler 1", "Fühler 2"], hub.Config.Fans.Sensors.Select(s => s.Name));
        Assert.All(hub.Config.Fans.Sensors, s => Assert.Equal(50, s.WarnTemp));
        // dauerhaft gespeichert
        Assert.Equal(2, AppConfig.Load(Path.Combine(dir.Path, "config.json")).Fans.Sensors.Count);

        hub.Config.Fans.Sensors[0].Name = "Netzteil";
        hub.Config.Fans.Sensors[0].WarnTemp = 45;
        hub.Config.Fans.Sensors[1].Name = "Miner-Raum";
        hub.Config.Fans.Sensors[1].WarnTemp = 35;
        sim.Sensors[0] = new TempReading(Psu, 46.5);                   // nur das Netzteil ist zu warm
        advance(2);
        await hub.FanTickAsync();

        var psu = hub.FanStatus.Sensors!.Single(s => s.Id == Psu);
        var room = hub.FanStatus.Sensors!.Single(s => s.Id == Room);
        Assert.True(psu.Hot);
        Assert.False(room.Hot);
        Assert.Contains($"temp-hot:{Psu}", sent);
        Assert.DoesNotContain($"temp-hot:{Room}", sent);
        Assert.Equal(46.5, hub.FanStatus.CaseTemp);

        var model = hub.BuildDisplayModel(DateTime.Now);
        Assert.Equal(["Netzteil", "Miner-Raum"], model.Temps!.Select(t => t.Name));
        Assert.Contains(model.Alerts, a => a.StartsWith("Netzteil 46,5"));
    }

    [Fact]
    public async Task Case_fans_follow_only_selected_sensors_and_go_safe_when_one_is_missing()
    {
        var (hub, sim, sent, _, advance, dir) = Hub(c =>
        {
            c.Fans.Case.Curve = new FanCurve { StartTemp = 30, StartPercent = 30, FullTemp = 50, MinPercent = 20, Hysteresis = 0 };
            c.Fans.Sensors.Add(new TempSensorSettings { Id = Psu, Name = "Netzteil", WarnTemp = 60, CaseFans = false });
            c.Fans.Sensors.Add(new TempSensorSettings { Id = Room, Name = "Miner-Raum", WarnTemp = 45 });
        });
        using var _d = dir;
        using var _h = hub;
        sim.Sensors.Add(new TempReading(Psu, 48));    // wärmer, zählt aber nicht für die Gehäuselüfter
        sim.Sensors.Add(new TempReading(Room, 36));
        await hub.FanTickAsync();
        advance(2);
        await hub.FanTickAsync();
        Assert.Equal(FanController.Evaluate(hub.Config.Fans.Case.Curve, 36), sim.LastPercent[3]);

        // Raum-Fühler fällt aus → Gehäuselüfter auf „unbekannt“ (100 %), nach 2 Minuten Meldung
        sim.Sensors.RemoveAt(1);
        advance(40);
        await hub.FanTickAsync();
        Assert.Null(hub.FanStatus.Sensors!.Single(s => s.Id == Room).Temp);
        Assert.Equal(hub.Config.Fans.Case.UnknownPercent, sim.LastPercent[3]);
        Assert.DoesNotContain($"temp-missing:{Room}", sent);
        advance(120);
        await hub.FanTickAsync();
        Assert.Contains($"temp-missing:{Room}", sent);
        Assert.Contains(hub.BuildDisplayModel(DateTime.Now).Alerts, a => a == "Fühler Miner-Raum fehlt");
    }

    [Fact]
    public void Sensor_names_are_validated_by_the_api()
    {
        var f = new FanSettings();
        f.Sensors.Add(new TempSensorSettings { Id = " 28FF641E0F16044A ", Name = "  Netzteil ", WarnTemp = 500 });
        f.Sensors.Add(new TempSensorSettings { Id = "28ff641e0f16044a", Name = "doppelt" });
        f.Sensors.Add(new TempSensorSettings { Id = "#1", Name = "alt" });
        using var dir = new TempDir();
        using var hub = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        BitaxeTuner.Server.Api.Endpoints.ValidateFans(f, hub);
        var s = Assert.Single(f.Sensors);
        Assert.Equal(("28ff641e0f16044a", "Netzteil", 100.0), (s.Id, s.Name, s.WarnTemp));

        f.Sensors[0].Name = " ";
        Assert.Throws<InvalidOperationException>(() => BitaxeTuner.Server.Api.Endpoints.ValidateFans(f, hub));
    }
}
