using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Tests;

public class ParsingAndProfileTests
{
    private const string AxeOsGamma = """
    {
      "power": 17.2, "voltage": 5087.5, "current": 3390, "temp": 58.3, "temp2": -1, "vrTemp": 61,
      "hashRate": 1180.4, "hashRate_1m": 1175.1, "expectedHashrate": 1224, "errorPercentage": 0.41,
      "bestDiff": 1234, "coreVoltage": 1150, "coreVoltageActual": 1144, "frequency": 600, "actualFrequency": 599.8,
      "ASICModel": "BM1370", "asicCount": 1, "smallCoreCount": 2040, "boardVersion": "602",
      "hostname": "bitaxe-gamma", "version": "v2.9.0", "axeOSVersion": "v2.9.0",
      "autofanspeed": 1, "fanspeed": 72, "manualFanSpeed": 100, "fanrpm": 4800,
      "sharesAccepted": 120, "sharesRejected": 1, "uptimeSeconds": 3600,
      "overheat_mode": 0, "overclockEnabled": 1
    }
    """;

    private const string NerdQAxe = """
    {
      "deviceModel": "NerdQAxe++", "asicCount": 4, "smallCoreCount": 2040, "power": 72.5, "voltage": 12050,
      "temp": 57, "vrTemp": 63, "asicTemps": [55.1, 58.9, 56.2, 57.0], "hashRate": 4800.2,
      "coreVoltage": 1150, "frequency": 600, "defaultFrequency": 600, "defaultCoreVoltage": 1150,
      "jobInterval": 1200, "autofanspeed": 2, "fanspeed": 60, "hostname": "nerdqaxe", "version": "v1.0.30"
    }
    """;

    private static MinerInfo Parse(string json) => AxeOsClient.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_AxeOS_info()
    {
        var i = Parse(AxeOsGamma);
        Assert.Equal(FirmwareKind.AxeOS, i.Firmware);
        Assert.Equal("BM1370", i.AsicModel);
        Assert.Equal(600, i.FrequencyMhz);
        Assert.Equal(1150, i.CoreVoltageMv);
        Assert.Equal(1224, i.ExpectedHashRateGh);
        Assert.Null(i.ChipTemp2C); // -1 = nicht vorhanden
        Assert.Equal(58.3, i.MaxChipTempC);
        Assert.Equal(1, i.AutoFanMode);
        Assert.True(i.OverclockEnabled);
        Assert.False(i.HasFault);
        Assert.InRange(i.EfficiencyJth!.Value, 14.5, 14.6);
    }

    [Fact]
    public void Parses_NerdQAxe_info_and_uses_hottest_asic()
    {
        var i = Parse(NerdQAxe);
        Assert.Equal(FirmwareKind.NerdQAxe, i.Firmware);
        Assert.Equal(4, i.AsicCount);
        Assert.Equal(58.9, i.MaxChipTempC);
        Assert.Equal(12050, i.InputVoltageMv);
        Assert.Equal(2, i.AutoFanMode);
    }

    [Theory]
    [InlineData("""{"ASICModel":"BM1370","boardVersion":"602","asicCount":1}""", "bitaxe-gamma")]
    [InlineData("""{"ASICModel":"BM1368","boardVersion":"401","asicCount":1}""", "bitaxe-supra")]
    [InlineData("""{"ASICModel":"BM1366","boardVersion":"204","asicCount":1}""", "bitaxe-ultra")]
    [InlineData("""{"ASICModel":"BM1370","boardVersion":"801","asicCount":2}""", "bitaxe-gt")]
    [InlineData("""{"ASICModel":"BM1368","boardVersion":"702","asicCount":6}""", "bitaxe-suprahex")]
    [InlineData("""{"deviceModel":"NerdQAxe++","asicCount":4,"jobInterval":1}""", "nerdqaxe-plusplus")]
    [InlineData("""{"deviceModel":"NerdQAxe+","asicCount":4,"jobInterval":1}""", "nerdqaxe-plus")]
    [InlineData("""{"deviceModel":"NerdAxeGamma","asicCount":1,"jobInterval":1}""", "nerdaxe-gamma")]
    [InlineData("""{"deviceModel":"NerdAxe","asicCount":1,"jobInterval":1}""", "nerdaxe")]
    [InlineData("""{"deviceModel":"NerdOCTAXE-γ","asicCount":8,"jobInterval":1}""", "nerdoctaxe")]
    [InlineData("""{"ASICModel":"BM1370","asicCount":4,"asicTemps":[1]}""", "nerdqaxe-plusplus")]
    public void Matches_profiles(string json, string expectedId)
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        Assert.Equal(expectedId, registry.Match(Parse(json)).Id);
    }

    [Fact]
    public void Unknown_device_gets_generic_profile_with_device_values()
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        var p = registry.Match(Parse("""{"ASICModel":"BM9999","asicCount":3,"smallCoreCount":1500,"defaultFrequency":700}"""));
        Assert.Equal("generic", p.Id);
        Assert.Equal(1500, p.SmallCoresPerAsic);
        Assert.Equal(3, p.AsicCount);
        Assert.Equal(700, p.DefaultFrequencyMhz);
    }

    [Fact]
    public void Asic_endpoint_device_model_wins()
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        var p = registry.Match(Parse("""{"ASICModel":"BM1370","boardVersion":"999"}"""), new AsicInfo { DeviceModel = "Gamma", AsicCount = 1 });
        Assert.Equal("bitaxe-gamma", p.Id);
    }

    [Fact]
    public void All_builtin_profiles_are_sane()
    {
        foreach (var p in ProfileRegistry.LoadBuiltIn())
        {
            Assert.InRange(p.DefaultFrequencyMhz, p.MinFrequencyMhz, p.MaxFrequencyMhz);
            Assert.InRange(p.DefaultVoltageMv, p.MinVoltageMv, p.MaxVoltageMv);
            Assert.True(p.MaxPowerW > 0, p.Id);
        }
    }
}
