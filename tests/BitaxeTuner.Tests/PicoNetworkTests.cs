using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>
/// Pico über WLAN (0.9.7): das echte Pico-Programm (btfan.py) läuft unter normalem Python mit nachgebildeter Hardware
/// (tools/pico-sim/run_btfan.py). Ohne Python auf dem Rechner werden diese Tests übersprungen (gelten als bestanden).
/// </summary>
public sealed class PicoNetworkTests : IDisposable
{
    private readonly TempDir _dir = new();
    private Process? _pico;

    private static string? Python()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (p is null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return name;
            }
            catch { /* nicht vorhanden */ }
        }
        return null;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir ?? throw new InvalidOperationException("Repository nicht gefunden"), .. parts]);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Simulierten Pico starten; liefert Port und Schlüssel oder null ohne Python.</summary>
    private (int Port, byte[] Key)? StartPico(string role = "fans", string? firmware = null)
    {
        if (Python() is not { } python) return null;
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var port = FreePort();
        File.WriteAllText(_dir.File("btcfg.json"),
            $$"""{"role": "{{role}}", "ssid": "test", "psk": "geheim123", "key": "{{Convert.ToHexString(key).ToLowerInvariant()}}", "port": {{port}}, "bind": "127.0.0.1"}""");
        var source = firmware ?? PicoFanDevice.Firmware;
        File.WriteAllText(_dir.File("fw.py"), source);
        _pico = Process.Start(new ProcessStartInfo(python)
        {
            ArgumentList = { RepoFile("tools", "pico-sim", "run_btfan.py"), _dir.Path, _dir.File("fw.py") },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        _pico.OutputDataReceived += (_, _) => { };
        _pico.ErrorDataReceived += (_, _) => { };
        _pico.BeginOutputReadLine();
        _pico.BeginErrorReadLine();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                return (port, key);
            }
            catch (SocketException) { Thread.Sleep(100); }
        }
        throw new InvalidOperationException("Simulierter Pico startet nicht");
    }

    public void Dispose()
    {
        try { if (_pico is { HasExited: false }) _pico.Kill(); } catch { /* schon beendet */ }
        _pico?.WaitForExit(3000);
        _pico?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Fan_pico_works_over_wlan_with_signed_lines()
    {
        if (StartPico() is not var (port, key)) return;
        using var device = PicoFanDevice.ConnectNetwork(NetworkLineTransport.Connect("127.0.0.1", port, key));
        Assert.Equal(PicoFanDevice.RoleFans, device.Role);
        Assert.Contains("WLAN", device.Description);
        Assert.Equal(6, (await device.ExchangeAsync([10, 20, 30, 40, 50, 60])).Length);
        var image = new byte[PicoFanDevice.ImageBytes];
        await device.ShowImageAsync(image);                                         // ~500 signierte Zeilen
        Assert.Equal(6, (await device.PollAsync()).Length);
    }

    [Fact]
    public async Task Hardware_watchdog_is_fed_while_the_program_runs()
    {
        if (StartPico() is not var (port, key)) return;
        using var device = PicoFanDevice.ConnectNetwork(NetworkLineTransport.Connect("127.0.0.1", port, key));
        await device.ShowImageAsync(new byte[PicoFanDevice.ImageBytes]);
        await Task.Delay(9500);                                                     // länger als der Watchdog (8 s)
        Assert.False(_pico!.HasExited);
        Assert.Equal(6, (await device.ExchangeAsync([10, 20, 30, 40, 50, 60])).Length);
    }

    [Fact]
    public void Hanging_program_is_restarted_by_the_hardware_watchdog()
    {
        const string loop = "    while True:\n        try:\n            wdt.feed()\n";
        Assert.Contains(loop, PicoFanDevice.Firmware);
        var hanging = PicoFanDevice.Firmware.Replace(loop, "    while True:\n        try:\n            time.sleep(30)\n");
        if (StartPico(firmware: hanging) is null) return;
        Assert.True(_pico!.WaitForExit(15000));
        Assert.Equal(4, _pico.ExitCode);                                            // Simulator: "WATCHDOG RESET"
    }

    [Fact]
    public void Error_in_the_loop_is_reported_and_the_program_keeps_running()
    {
        // 8.1: früher endete das Programm bei einem Fehler, und der Watchdog startete den Pico neu
        const string loop = "    while True:\n        try:\n            wdt.feed()\n";
        var faulty = PicoFanDevice.Firmware.Replace(loop, loop +
            "            if not globals().get('_boom'):\n                globals()['_boom'] = 1\n                raise ValueError('boom')\n");
        if (StartPico(firmware: faulty) is not var (port, key)) return;
        using var io = NetworkLineTransport.Connect("127.0.0.1", port, key);
        io.Write("HELLO\r\n");
        Assert.StartsWith("OK BTFAN " + PicoFanDevice.FirmwareVersion, io.ReadLine(TimeSpan.FromSeconds(3)));
        var info = io.ReadLine(TimeSpan.FromSeconds(3));
        Assert.StartsWith("INFO reset=", info);
        Assert.Contains("ValueError", info);
        Assert.Contains("Pico-Programm", Core.Host.MinerHub.PicoInfoText(info!));
        io.Write("GET\r\n");
        Assert.StartsWith("RPM", io.ReadLine(TimeSpan.FromSeconds(3)));             // läuft weiter
        Assert.Null(Core.Host.MinerHub.PicoInfoText("INFO reset=power err=-"));   // normaler Start: nichts protokollieren
        Assert.Contains("Watchdog", Core.Host.MinerHub.PicoInfoText("INFO reset=wdt err=-"));
    }

    [Fact]
    public void Wrong_key_is_rejected_and_nothing_runs()
    {
        if (StartPico() is not var (port, _)) return;
        var ex = Assert.Throws<IOException>(() => NetworkLineTransport.Connect("127.0.0.1", port, new byte[32]));
        Assert.Contains("Schlüssel", ex.Message);
    }

    [Fact]
    public async Task Tampered_line_ends_the_connection()
    {
        if (StartPico() is not var (port, key)) return;
        using var io = NetworkLineTransport.Connect("127.0.0.1", port, key);
        using var raw = new TcpClient();
        // zweite, nicht angemeldete Verbindung kann nichts ausrichten und verdrängt die erste nicht
        raw.Connect(IPAddress.Loopback, port);
        var s = raw.GetStream();
        s.Write("SET 0 0 0 0 0 0 ~1 0000000000000000\n"u8);
        await Task.Delay(300);
        io.Write("GET\r\n");
        Assert.StartsWith("RPM", io.ReadLine(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Display_pico_has_its_own_pins_and_no_fans()
    {
        if (StartPico("display") is not var (port, key)) return;
        using var device = PicoFanDevice.ConnectNetwork(NetworkLineTransport.Connect("127.0.0.1", port, key));
        Assert.Equal(PicoFanDevice.RoleDisplay, device.Role);
        Assert.Empty(await device.PollAsync());
        await device.ShowImageAsync(new byte[PicoFanDevice.ImageBytes]);
    }

    [Fact]
    public void Older_program_is_updated_over_wlan_and_verified()
    {
        var old = PicoFanDevice.Firmware.Replace($"VERSION = \"{PicoFanDevice.FirmwareVersion}\"", "VERSION = \"6x\"");
        Assert.NotEqual(PicoFanDevice.Firmware, old);
        if (StartPico(firmware: old) is not var (port, key)) return;
        var io = NetworkLineTransport.Connect("127.0.0.1", port, key);
        Assert.Equal("6x", io.Version);
        Assert.Throws<PicoUpdatedException>(() => PicoFanDevice.ConnectNetwork(io));
        Assert.True(_pico!.WaitForExit(5000));                                     // Neustart nach dem Update
        Assert.Equal(3, _pico.ExitCode);
        Assert.Equal(PicoFanDevice.Firmware, File.ReadAllText(_dir.File("main.py")).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("bitaxetuner-fans.local", "bitaxetuner-fans.local", NetworkLineTransport.DefaultPort)]
    [InlineData("192.168.1.20:9000", "192.168.1.20", 9000)]
    [InlineData("pico", "pico", NetworkLineTransport.DefaultPort)]
    public void Host_and_port_are_split(string value, string host, int port) =>
        Assert.Equal((host, port), MinerHub.SplitHostPort(value));

    [Fact]
    public void Setup_config_is_checked_and_gets_a_fresh_key()
    {
        var a = PicoNetworkConfig.Create("fans", "Heimnetz", "sehrgeheim", null);
        var b = PicoNetworkConfig.Create("fans", "Heimnetz", "sehrgeheim", null);
        Assert.NotEqual(a.KeyHex, b.KeyHex);
        Assert.Equal("bitaxetuner-fans", a.Host);
        Assert.Equal("bitaxetuner-display", PicoNetworkConfig.Create("display", "x", "", null).Host);
        Assert.Throws<LocalizedException>(() => PicoNetworkConfig.Create("fans", "", "sehrgeheim", null));
        Assert.Throws<LocalizedException>(() => PicoNetworkConfig.Create("fans", "x", "kurz", null));
        Assert.Throws<LocalizedException>(() => PicoNetworkConfig.Create("fans", "x", "sehrgeheim", "Mein Pico"));
        Assert.Throws<LocalizedException>(() => PicoNetworkConfig.Create("fans", "Wäsche", "sehrgeheim", null));
    }

    [Fact]
    public async Task Setup_over_usb_writes_role_and_wlan_but_keeps_the_password_off_the_server()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var pico = new FakePico { ProgramRunning = true };
        var config = new AppConfig();
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            SerialTransportFactory = _ => pico,
            DisplayDeviceFactory = _ => new SimulatedFanDevice(PicoFanDevice.RoleDisplay),
        });
        var result = await hub.SetupPicoNetworkAsync("display", "COM9", "Heimnetz", "sehrgeheim-123", null);

        Assert.Equal("bitaxetuner-display.local", result.Host);
        Assert.Equal("192.0.2.10", result.Ip);
        Assert.Contains("\"role\":\"display\"", pico.Files["btcfg.json"]);
        Assert.Contains("sehrgeheim-123", pico.Files["btcfg.json"]);
        Assert.Equal(PicoFanDevice.Firmware, pico.MainPy);
        Assert.True(config.Display.OwnDevice);
        Assert.Equal("wlan", config.Display.Connection);
        Assert.Equal(64, hub.Secrets.Get(MinerHub.PicoKeyDisplay)!.Length);
        config.Save(_dir.File("config.json"));
        Assert.DoesNotContain("sehrgeheim-123", File.ReadAllText(_dir.File("config.json")));
        Assert.DoesNotContain("sehrgeheim-123", File.ReadAllText(_dir.File("secrets.json")));
    }

    [Fact]
    public async Task Display_goes_to_its_own_pico_while_fans_stay_on_the_fan_pico()
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var fans = new SimulatedFanDevice();
        var display = new SimulatedFanDevice(PicoFanDevice.RoleDisplay);
        var config = new AppConfig { Devices = [new DeviceConfig { Name = "Gamma", Host = "10.0.0.11" }] };
        config.Fans.Enabled = true;
        config.Display.Enabled = true;
        config.Display.Device = "own";
        config.Display.Inverted = true;
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => fans,
            DisplayDeviceFactory = _ => display,
        });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        await hub.PollNowAsync();
        await hub.FanTickAsync();

        Assert.True(hub.FanStatus.Connected);
        Assert.Single(display.Images);
        Assert.Empty(fans.Images);
        Assert.True(hub.DisplayStatus.Connected);
        // Taster am Display-Pico wirken wie am Lüfter-Pico
        display.Press("BTN 3");
        await hub.FanTickAsync();
        Assert.Equal(FanOverride.Full, hub.FanOverride);
    }

    [Fact]
    public void Inverted_display_swaps_black_and_white_but_keeps_red()
    {
        var m = new DisplayModel("Test", DateTime.Now, 1200, 15, 12.5, 1, 1, null, "Auto", false, false, [], ["Warnung"]);
        var normal = StatusRenderer.Render(m with { Scene = DisplayScene.Alarm });
        var inverted = StatusRenderer.Render(m with { Scene = DisplayScene.Alarm, Inverted = true });
        const int plane = PicoFanDevice.ImageBytes / 2;
        static int White(byte[] b) => b.Take(plane).Sum(x => System.Numerics.BitOperations.PopCount(x));
        Assert.True(White(normal) > plane * 4);                                   // überwiegend weiß
        Assert.True(White(inverted) < plane * 4);                                 // überwiegend schwarz
        Assert.Equal(normal.Skip(plane), inverted.Skip(plane));                   // Rot-Ebene unverändert
    }
}
