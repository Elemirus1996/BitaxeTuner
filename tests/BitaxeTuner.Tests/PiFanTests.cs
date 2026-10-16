using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.20: Lüfter des Servers (Raspberry Pi 5) – Anzeige, Kurven nach CPU, Regelung nach Miner-Temperatur.</summary>
public sealed class PiFanTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _sys = new();
    private readonly AppConfig _config = new();
    private readonly MinerHub _hub;

    public PiFanTests()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        _config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.11.1" });
        _hub = new MinerHub(_config, new MinerHubOptions { DataDirectory = _dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) })
        {
            PiFanSysRoot = _sys.Path,
            PiFanControlOverride = true,
        };
        Write("/sys/class/thermal/cooling_device0/type", "cpufreq-cpu0");
        Write("/sys/class/thermal/cooling_device1/type", "pwm-fan");
        Write("/sys/class/thermal/cooling_device1/max_state", "4");
        Write("/sys/class/thermal/cooling_device1/cur_state", "1");
        Write("/sys/class/thermal/thermal_zone0/type", "cpu-thermal");
        Write("/sys/class/thermal/thermal_zone0/policy", "step_wise");
        Write("/sys/class/hwmon/hwmon2/name", "pwmfan");
        Write("/sys/class/hwmon/hwmon2/fan1_input", "2950");
        Cpu(65);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
        _sys.Dispose();
    }

    private void Write(string path, string value)
    {
        var f = _sys.Path + path;
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, value + "\n");
    }

    private void Cpu(double c) => Write("/sys/class/thermal/thermal_zone0/temp", ((int)(c * 1000)).ToString());
    private string? Request() => File.Exists(_dir.File("pifan-request")) ? File.ReadAllText(_dir.File("pifan-request")).Trim() : null;
    private static MinerTemps Miner(double vr, DateTime now) => new("10.0.11.1", "Gamma", true, vr, vr - 5, now);

    [Fact]
    public void Status_is_read_from_sysfs_and_auto_leaves_the_firmware_alone()
    {
        var st = _hub.PiFan;
        Assert.True(st.Present);
        Assert.Equal(2950, st.Rpm);
        Assert.Equal(65, st.CpuTemp);
        Assert.Equal((1, 4), (st.Level, st.MaxLevel));
        Assert.True(st.Firmware);

        _hub.TickPiFan(DateTime.Now, []);                       // Standard „auto“: nichts anfordern
        Assert.Null(Request());
        Assert.False(File.Exists(_dir.File("pifan-heartbeat")));
    }

    [Fact]
    public void Cpu_curve_switches_up_at_once_and_down_with_three_degrees_margin()
    {
        var now = DateTime.Now;
        _hub.SavePiFan(new PiFanSettings { Mode = "curve", Preset = "balanced" }, "Test");
        _hub.TickPiFan(now, []);
        Assert.Equal("level 2", Request());                      // 65 °C: ab 50 und ab 60
        Assert.True(int.Parse(File.ReadAllText(_dir.File("pifan-heartbeat")).Trim()) >= 180);

        Cpu(58);                                                 // 58 + 3 = 61 → bleibt 2
        _hub.TickPiFan(now.AddMinutes(1), []);
        Assert.Equal("level 2", Request());
        Cpu(56);                                                 // 56 + 3 = 59 → 1
        _hub.TickPiFan(now.AddMinutes(2), []);
        Assert.Equal("level 1", Request());

        Assert.Equal(0, MinerHub.CpuLevel("quiet", 55, null, 4));
        Assert.Equal(1, MinerHub.CpuLevel("cool", 30, null, 4));   // „kühl“ nie ganz aus
        Assert.Equal(4, MinerHub.CpuLevel("balanced", 90, null, 4));

        _hub.SavePiFan(new PiFanSettings { Mode = "auto" }, "Test");
        _hub.TickPiFan(now.AddMinutes(3), []);
        Assert.Equal("auto", Request());                         // zurück an die Firmware
    }

    [Fact]
    public void Miner_mode_follows_the_hottest_miner_but_never_below_the_cpu_curve()
    {
        var now = DateTime.Now;
        _hub.SavePiFan(new PiFanSettings { Mode = "miners", Curve = new FanCurve { StartTemp = 50, StartPercent = 30, FullTemp = 70, MinPercent = 0 } }, "Test");
        Cpu(45);
        _hub.TickPiFan(now, [Miner(68, now)]);                   // 68 °C → über 80 % → Stufe 4
        Assert.Equal("level 4", Request());

        Cpu(70);                                                 // Miner kühl, CPU warm → CPU-Kurve „ausgewogen“ zählt
        _hub.TickPiFan(now.AddMinutes(1), [Miner(40, now.AddMinutes(1))]);
        Assert.Equal("level 3", Request());

        _hub.TickPiFan(now.AddMinutes(2), []);                   // keine Miner-Daten → sicherheitshalber voll
        Assert.Equal("level 4", Request());
    }

    [Fact]
    public void Without_cpu_temperature_or_control_it_is_safe()
    {
        _hub.SavePiFan(new PiFanSettings { Mode = "curve", Preset = "quiet" }, "Test");
        File.Delete(_sys.Path + "/sys/class/thermal/thermal_zone0/temp");
        _hub.TickPiFan(DateTime.Now, []);
        Assert.Equal("level 4", Request());                      // ohne CPU-Temperatur: voll

        File.Delete(_dir.File("pifan-request"));
        _hub.PiFanControlOverride = false;                       // Systemdateien fehlen: nur anzeigen
        _hub.TickPiFan(DateTime.Now.AddMinutes(1), []);
        Assert.Null(Request());
    }

    [Fact]
    public void Fan_helper_and_units_are_installed_and_packaged()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        string Repo(params string[] p) => File.ReadAllText(Path.Combine([dir!, .. p]));
        var install = Repo("deploy", "linux", "install.sh");
        var build = Repo("build.ps1");
        var image = Repo("deploy", "pi-image", "build-image.sh");
        foreach (var f in new[] { "bitaxetuner-pifan.sh", "bitaxetuner-pifan.path", "bitaxetuner-pifan-guard.timer" })
        {
            Assert.Contains(f.Replace(".sh", ""), install);
            Assert.Contains(f, build);
        }
        Assert.Contains("bitaxetuner-pifan-guard.timer", image);
        Assert.Contains("PathModified=/var/lib/bitaxetuner/pifan-request", Repo("deploy", "linux", "bitaxetuner-pifan.path"));
    }

    [Fact]
    public void Stalled_fan_is_reported_in_the_system_health()
    {
        Write("/sys/class/thermal/cooling_device1/cur_state", "3");
        Write("/sys/class/hwmon/hwmon2/fan1_input", "0");
        var check = _hub.SystemChecks(DateTime.Now).Single(c => c.Key == "server-fan");
        Assert.Equal("bad", check.State);
        Write("/sys/class/hwmon/hwmon2/fan1_input", "4100");
        Assert.Equal("ok", _hub.SystemChecks(DateTime.Now).Single(c => c.Key == "server-fan").State);
    }
}
