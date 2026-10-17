using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.23 Pico-Übersicht: X25519 für die Kopplung, Liste der Picos und Aktionen im Hub.</summary>
public class PicoOverviewTests
{
    [Fact]
    public void X25519_matches_rfc7748()
    {
        // RFC 7748, Abschnitt 6.1
        var a = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var b = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var pubA = X25519.ScalarMult(a, X25519.BasePoint);
        var pubB = X25519.ScalarMult(b, X25519.BasePoint);
        Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", Convert.ToHexString(pubA).ToLowerInvariant());
        Assert.Equal("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", Convert.ToHexString(pubB).ToLowerInvariant());
        var shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
        Assert.Equal(shared, Convert.ToHexString(X25519.ScalarMult(a, pubB)).ToLowerInvariant());
        Assert.Equal(shared, Convert.ToHexString(X25519.ScalarMult(b, pubA)).ToLowerInvariant());
    }

    [Fact]
    public void Stat_line_is_parsed()
    {
        var s = PicoFanDevice.ParseStat("OK STAT rssi=-61 up=7200 mem=81234 pair=1");
        Assert.Equal(-61, s.Rssi);
        Assert.Equal(7200, s.Uptime);
        Assert.Equal(81234, s.Memory);
        Assert.True(s.Pairing);
        Assert.Null(PicoFanDevice.ParseStat("OK STAT rssi=- up=5 mem=1 pair=0").Rssi);
    }

    [Fact]
    public async Task Hub_lists_picos_and_blinks_restarts()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var sim = new SimulatedFanDevice();
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "A", Host = "10.0.9.1" });
        config.Fans.Enabled = true;
        config.Display.Enabled = true;      // Anzeige am Lüfter-Pico
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => sim,
        });
        await hub.FanTickAsync();

        var list = await hub.PicosAsync(refresh: true);
        var pico = Assert.Single(list);
        Assert.Equal("fans", pico.Slot);
        Assert.True(pico.Connected);
        Assert.Equal("usb", pico.Connection);
        Assert.Equal(PicoFanDevice.FirmwareVersion, pico.Version);
        Assert.False(pico.Outdated);
        Assert.Equal(3600, pico.Uptime);
        Assert.Equal("bwr75", pico.Panel);

        await hub.PicoBlinkAsync("fans", "Test");
        Assert.Equal(1, sim.Blinks);
        await hub.PicoRestartAsync("fans", "Test");
        Assert.Equal(1, sim.Resets);
        Assert.False((await hub.PicosAsync()).Single().Connected);   // verbindet sich erst nach einigen Sekunden neu

        await Assert.ThrowsAsync<LocalizedException>(() => hub.PicoBlinkAsync("extra-ff", "Test"));
        // Koppeln und Schlüssel nur im WLAN
        await Assert.ThrowsAsync<LocalizedException>(() => hub.PicoPairStartAsync("fans", "Test"));
    }
}
