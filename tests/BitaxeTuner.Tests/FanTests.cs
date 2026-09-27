using System.Text;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
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
    public string? MainPy { get; private set; }
    private readonly StringBuilder _file = new();
    public List<string> SetCommands { get; } = [];

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
            if (line == "HELLO") _out.Append("OK BTFAN 1 6\r\n");
            else if (line.StartsWith("SET ")) { SetCommands.Add(line); _out.Append("RPM 1200 1300 0 0 0 0\r\n"); }
            return;
        }
        _in.Append(ch);
    }

    private void ExecRaw(string code)
    {
        code = code.TrimStart('\r');
        if (code == "f=open('main.py','w')") _file.Clear();
        else if (code == "f.close()") MainPy = _file.ToString();
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
        Assert.Contains("v1", device.Description);

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
