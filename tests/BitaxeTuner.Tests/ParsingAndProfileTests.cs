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
    public void AxeOS_with_two_temperature_sensors_reports_both_chips()
    {
        var i = Parse("""{"ASICModel":"BM1370","asicCount":2,"temp":60.5,"temp2":64.2,"vrTemp":70}""");
        Assert.Equal([60.5, 64.2], i.ChipTempsC!);
        Assert.Equal(64.2, i.MaxChipTempC);
        Assert.Null(Parse(AxeOsGamma).ChipTempsC);                                 // ein Chip: keine Liste
    }

    [Fact]
    public void Parses_NerdQAxe_info_and_uses_hottest_asic()
    {
        var i = Parse(NerdQAxe);
        Assert.Equal(FirmwareKind.NerdQAxe, i.Firmware);
        Assert.Equal(4, i.AsicCount);
        Assert.Equal(58.9, i.MaxChipTempC);
        Assert.Equal([55.1, 58.9, 56.2, 57.0], i.ChipTempsC!);                 // 0.9.11: jeder Chip einzeln
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
    [InlineData("""{"deviceModel":"Q1370","ASICModel":"BM1370","asicCount":4}""", "q1370")]
    [InlineData("""{"deviceModel":"Q1373","ASICModel":"BM1373","asicCount":4}""", "q1373")]
    [InlineData("""{"deviceModel":"NerdQAxe+","asicCount":4,"jobInterval":1}""", "nerdqaxe-plus")]
    [InlineData("""{"deviceModel":"NerdAxeGamma","asicCount":1,"jobInterval":1}""", "nerdaxe-gamma")]
    [InlineData("""{"deviceModel":"NerdAxe","asicCount":1,"jobInterval":1}""", "nerdaxe")]
    [InlineData("""{"deviceModel":"NerdOCTAXE-γ","asicCount":8,"jobInterval":1}""", "nerdoctaxe")]
    [InlineData("""{"ASICModel":"BM1370","asicCount":4,"asicTemps":[1]}""", "nerdqaxe-plusplus")]
    // Neu aus der Firmware-Recherche (ESP-Miner device_config.h, NerdQAxePlus boards/*.cpp)
    [InlineData("""{"ASICModel":"BM1370","boardVersion":"650","asicCount":2}""", "bitaxe-duo")]
    [InlineData("""{"ASICModel":"BM1370","boardVersion":"1300","asicCount":6}""", "bitaxe-gammahex")]
    [InlineData("""{"ASICModel":"BM1373","boardVersion":"1201","asicCount":2}""", "bitaxe-najaduo")]
    [InlineData("""{"ASICModel":"BM1366","boardVersion":"0.11"}""", "bitaxe-ultra")]
    [InlineData("""{"ASICModel":"BM1397","boardVersion":"102"}""", "bitaxe-max")]
    [InlineData("""{"deviceModel":"NerdAxeGaia","asicCount":1,"jobInterval":1}""", "nerdaxe-gaia")]
    [InlineData("""{"deviceModel":"NerdOCTAXE+","asicCount":8,"jobInterval":1}""", "nerdoctaxe-plus")]
    [InlineData("""{"deviceModel":"NerdHaxe-γ","asicCount":6,"jobInterval":1}""", "nerdhaxe-gamma")]
    [InlineData("""{"deviceModel":"NerdEKO","asicCount":12,"jobInterval":1}""", "nerdeko")]
    [InlineData("""{"deviceModel":"NerdQX","asicCount":4,"jobInterval":1}""", "nerdqx")]
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
        // Unbekannter ASIC: nur wenig über den Standardwerten (Audit H1)
        Assert.True(p.IsFallback);
        Assert.Equal(750, p.MaxFrequencyMhz);
        Assert.True(p.MaxVoltageMv <= 1200);
    }

    [Fact]
    public void Gamma_duo_uses_the_limits_of_its_bm1370xp_variant()
    {
        // ESP-Miner device_config.h: Board 650 = BM1370XP, Auswahlliste 350–410 MHz, Standard 400 MHz (geprüft 03.10.2026)
        var duo = new ProfileRegistry(ProfileRegistry.LoadBuiltIn()).Match(Parse("""{"ASICModel":"BM1370","boardVersion":"650","asicCount":2}"""));
        Assert.Equal("bitaxe-duo", duo.Id);
        Assert.Equal((350, 410, 400), (duo.MinFrequencyMhz, duo.MaxFrequencyMhz, duo.DefaultFrequencyMhz));
        Assert.Equal((1000, 1250), (duo.MinVoltageMv, duo.MaxVoltageMv));
    }

    [Fact]
    public void Unchanged_copy_of_older_built_in_profiles_does_not_hide_corrected_limits()
    {
        // profiles.json aus „Profile bearbeiten“ mit dem GammaDuo-Stand bis 0.9.2 (400–800 MHz) → korrigierte Werte gelten
        using var dir = new TempDir();
        var old = """
            [{"Id":"bitaxe-duo","Name":"Bitaxe Gamma Duo (2× BM1370)","Family":"Bitaxe","AsicModel":"BM1370","AsicCount":2,"SmallCoresPerAsic":2040,
            "DefaultFrequencyMhz":525,"DefaultVoltageMv":1150,"MinFrequencyMhz":400,"MaxFrequencyMhz":800,"MinVoltageMv":1000,"MaxVoltageMv":1250,
            "MaxChipTempC":66,"MaxVrTempC":86,"MaxPowerW":40,"DeviceModelMatches":["GammaDuo","Gamma Duo"],"BoardVersions":["650"],
            "Notes":"ESP-Miner: Board 650, ASIC-Variante BM1370XP, Firmware-Leistungsbudget 40 W für beide Chips zusammen."}]
            """;
        File.WriteAllText(dir.File("profiles.json"), old);
        Assert.Equal(410, ProfileRegistry.Load(dir.Path).Profiles.First(p => p.Id == "bitaxe-duo").MaxFrequencyMhz);
        // selbst geändert (z. B. Chipgrenze 60 °C) → bleibt
        File.WriteAllText(dir.File("profiles.json"), old.Replace("\"MaxChipTempC\":66", "\"MaxChipTempC\":60"));
        Assert.Equal(800, ProfileRegistry.Load(dir.Path).Profiles.First(p => p.Id == "bitaxe-duo").MaxFrequencyMhz);
    }

    [Fact]
    public void Known_asic_without_exact_profile_gets_the_tightest_limits_of_its_family()
    {
        // BM1373 mit einer ASIC-Anzahl, für die es kein Profil gibt: nie die allgemeinen 1300 mV / 800 MHz (Audit H1)
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        var p = registry.Match(Parse("""{"ASICModel":"BM1373","asicCount":3}"""));
        var family = registry.Profiles.Where(x => x.AsicModel == "BM1373").ToList();
        Assert.True(p.IsFallback);
        Assert.Equal(family.Min(x => x.MaxVoltageMv), p.MaxVoltageMv);
        Assert.Equal(family.Min(x => x.MaxFrequencyMhz), p.MaxFrequencyMhz);
        Assert.Equal(family.Min(x => x.MaxChipTempC), p.MaxChipTempC);
        Assert.True(p.MinVoltageMv <= p.MaxVoltageMv && p.MinFrequencyMhz <= p.MaxFrequencyMhz);
        Assert.InRange(p.DefaultVoltageMv, p.MinVoltageMv, p.MaxVoltageMv);
        Assert.False(registry.Match(Parse("""{"ASICModel":"BM1370","boardVersion":"601"}""")).IsFallback);
    }

    [Theory]
    [InlineData("GammaDuo", "bitaxe-duo")]       // enthält auch "Gamma" – längster Treffer muss gewinnen
    [InlineData("GammaHex", "bitaxe-gammahex")]  // enthält "Gamma" und "Hex"
    [InlineData("NajaDuo", "bitaxe-najaduo")]
    [InlineData("GammaTurbo", "bitaxe-gt")]
    [InlineData("Gamma", "bitaxe-gamma")]
    public void Asic_endpoint_device_models_resolve_to_the_right_family(string deviceModel, string expected)
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        Assert.Equal(expected, registry.Match(Parse("""{"ASICModel":"BM1370"}"""), new AsicInfo { DeviceModel = deviceModel }).Id);
    }

    [Fact]
    public void Device_reported_core_and_chip_count_win_over_table()
    {
        var registry = new ProfileRegistry(ProfileRegistry.LoadBuiltIn());
        var p = registry.Match(Parse("""{"deviceModel":"NerdQX","asicCount":6,"smallCoreCount":2040,"jobInterval":1}"""));
        Assert.Equal(("nerdqx", 6, 2040), (p.Id, p.AsicCount, p.SmallCoresPerAsic));
        var gaia = registry.Match(Parse("""{"deviceModel":"NerdAxeGaia","asicCount":1,"smallCoreCount":6860,"jobInterval":1}"""));
        Assert.Equal(6860, gaia.SmallCoresPerAsic);
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

public class ProfileOverrideTests
{
    [Fact]
    public void Unchanged_legacy_copy_does_not_override_but_real_edits_do()
    {
        using var dir = new TempDir();
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BitaxeTuner.Core", "Profiles", "DeviceProfiles.v0.1.0.json")))!.AsArray();
        // Gamma vom Nutzer geändert, Rest unveränderte Kopie der v0.1.0-Profile
        var gamma = legacy.First(p => (string)p!["Id"]! == "bitaxe-gamma")!;
        gamma["MaxChipTempC"] = 60;
        File.WriteAllText(dir.File("profiles.json"), legacy.ToJsonString());

        var registry = ProfileRegistry.Load(dir.Path);

        Assert.Equal(40, registry.Profiles.First(p => p.Id == "bitaxe-duo").MaxPowerW);          // neue eingebaute Werte gelten
        Assert.Equal(["2.2", "102"], registry.Profiles.First(p => p.Id == "bitaxe-max").BoardVersions);
        Assert.Equal(60, registry.Profiles.First(p => p.Id == "bitaxe-gamma").MaxChipTempC);      // echte Änderung bleibt
        Assert.Contains(registry.Profiles, p => p.Id == "nerdaxe-gaia");
    }
}
