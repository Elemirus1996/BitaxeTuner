using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Tests;

/// <summary>Voreinstellungen für Raspberry Pi OS (cloud-init) auf der Boot-Partition.</summary>
public class PiOsSetupTests
{
    [Fact]
    public void Sha512_crypt_matches_glibc()
    {
        Assert.Equal("$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1",
            PiOsSetup.Sha512Crypt("Hello world!", "saltstring"));
        Assert.Equal("$6$Ab.9/xyz$MCgnW.vGhw13d1U0ZcUX7TaJVLMwPFI3pJ5Eq7RqQGJ84hfoWdcu32oBer50QFM3q5JJsdjYUXoFqzmNB2XoP/",
            PiOsSetup.Sha512Crypt("Grüße-€", "Ab.9/xyz"));
        var random = PiOsSetup.Sha512Crypt("geheim123");
        Assert.Matches(@"^\$6\$[./0-9A-Za-z]{16}\$[./0-9A-Za-z]{86}$", random);
        Assert.NotEqual(random, PiOsSetup.Sha512Crypt("geheim123"));
    }

    [Fact]
    public void Wpa_psk_matches_ieee_test_vector()
    {
        Assert.Equal("f42c6fc52df0ebef9ebb4b90b38a5f902e83fe1b135a70e23aed762e9710a12e", PiOsSetup.WpaPsk("IEEE", "password"));
        Assert.Equal("dea6585dd68764ce1beb11f5dbf5c6630f714b7df3c380188a6a87ae5ec78b06", PiOsSetup.WpaPsk("Fritz!Box ä", "Pässwort 123"));
    }

    private static TempDir Boot()
    {
        var d = new TempDir();
        File.WriteAllText(d.File("cmdline.txt"), "console=tty1 root=PARTUUID=x rootwait");
        File.WriteAllText(d.File("meta-data"), "dsmode: local\ninstance_id: rpios-image\n");
        File.WriteAllText(d.File("user-data"), "#cloud-config\n# Vorlage\n");
        File.WriteAllText(d.File("network-config"), "# Vorlage\n");
        return d;
    }

    [Fact]
    public void Writes_user_wifi_and_ssh_without_plain_passwords()
    {
        using var boot = Boot();
        PiOsSetup.Write(boot.Path, new PiOsOptions("pi", "pi-passwort-1", "Mein \"WLAN\" 2,4", "wlan-geheim-99"));

        var user = File.ReadAllText(boot.File("user-data"));
        var net = File.ReadAllText(boot.File("network-config"));
        Assert.StartsWith("#cloud-config\n", user);
        Assert.DoesNotContain("\r", user + net);
        Assert.Contains("hostname: \"bitaxetuner\"", user);
        Assert.Contains("- name: \"pi\"", user);
        Assert.Contains("passwd: \"$6$", user);
        Assert.Contains("ssh_pwauth: true", user);
        Assert.Contains("do_wifi_country, \"DE\"", user);
        Assert.Contains("\"Mein \\\"WLAN\\\" 2,4\":", net);
        Assert.Contains($"password: \"{PiOsSetup.WpaPsk("Mein \"WLAN\" 2,4", "wlan-geheim-99")}\"", net);
        Assert.Contains("regulatory-domain: \"DE\"", net);
        Assert.DoesNotContain("pi-passwort-1", user + net);
        Assert.DoesNotContain("wlan-geheim-99", user + net);
        Assert.True(File.Exists(boot.File("ssh")));
        // Vorlagen einmalig gesichert
        Assert.Equal("#cloud-config\n# Vorlage\n", File.ReadAllText(boot.File("user-data.orig")));
        PiOsSetup.Write(boot.Path, new PiOsOptions("pi", "pi-passwort-1"));
        Assert.Equal("#cloud-config\n# Vorlage\n", File.ReadAllText(boot.File("user-data.orig")));
        Assert.DoesNotContain("wifis", File.ReadAllText(boot.File("network-config")));   // nur LAN
    }

    [Theory]
    [InlineData("Pi", "pi-passwort-1", null, null)]          // Großbuchstaben
    [InlineData("root", "pi-passwort-1", null, null)]
    [InlineData("pi", "kurz", null, null)]
    [InlineData("pi", "pi-passwort-1", "WLAN", "1234567")]    // WLAN-Passwort zu kurz
    public void Rejects_invalid_input(string user, string pw, string? ssid, string? wifiPw)
    {
        using var boot = Boot();
        Assert.Throws<InvalidOperationException>(() => PiOsSetup.Write(boot.Path, new PiOsOptions(user, pw, ssid, wifiPw)));
        Assert.Equal("#cloud-config\n# Vorlage\n", File.ReadAllText(boot.File("user-data")));
    }

    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOPtestKeyForBitaxeTunerOnlyXXXXXXXXXXXXXXXXXXX bitaxetuner@PC";

    [Fact]
    public void Writes_ssh_key_and_region_from_the_dialog()
    {
        using var boot = Boot();
        PiOsSetup.Write(boot.Path, new PiOsOptions("admin", "pi-passwort-1", "Home", "wlan-geheim-99",
            Country: "US", Timezone: "America/New_York", Keyboard: "us", SshKeys: [Key]));
        var user = File.ReadAllText(boot.File("user-data"));
        Assert.Contains("  ssh_authorized_keys:\n  - \"" + Key + "\"\n", user);
        Assert.Contains("timezone: \"America/New_York\"", user);
        Assert.Contains("layout: \"us\"", user);
        Assert.Contains("do_wifi_country, \"US\"", user);
        Assert.Contains("regulatory-domain: \"US\"", File.ReadAllText(boot.File("network-config")));
    }

    [Theory]
    [InlineData("Europe/Berlin; rm -rf /", "de", Key)]     // Zeitzone mit Befehl
    [InlineData("Europe/Berlin", "de\nx", Key)]             // Tastatur
    [InlineData("Europe/Berlin", "de", "ssh-ed25519 AAAA\"\nruncmd: [evil]")] // Schlüssel mit Zeilenumbruch
    [InlineData("Europe/Berlin", "de", "kein schlüssel")]
    public void Rejects_unsafe_region_or_key(string tz, string kb, string key)
    {
        using var boot = Boot();
        Assert.Throws<InvalidOperationException>(() => PiOsSetup.Write(boot.Path,
            new PiOsOptions("pi", "pi-passwort-1", Timezone: tz, Keyboard: kb, SshKeys: [key])));
    }

    [Theory]
    [InlineData("de-DE", "DE", "de")]
    [InlineData("de-CH", "CH", "ch")]
    [InlineData("en-US", "US", "us")]
    [InlineData("en-GB", "GB", "gb")]
    [InlineData("pt-BR", "BR", "br")]
    [InlineData("nl-NL", "NL", "us")]
    [InlineData("fr-BE", "BE", "be")]
    public void Keyboard_layout_follows_language_and_country(string culture, string country, string expected) =>
        Assert.Equal(expected, PiRegion.KeyboardFor(System.Globalization.CultureInfo.GetCultureInfo(culture), country));

    [Fact]
    public void Windows_timezone_is_converted_to_iana()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
        Assert.Equal("Europe/Berlin", PiRegion.IanaTimezone(berlin, "DE"));
        var region = PiRegion.FromSystem();
        Assert.Matches("^[A-Z]{2}$", region.Country);
        // Kontinent/Stadt oder „UTC“ (GitHub-Runner laufen in UTC) – beides nimmt der Pi an
        Assert.Matches(@"^([A-Za-z_]+/[A-Za-z0-9_+\-/]+|UTC)$", region.Timezone);
        Assert.Equal("UTC", PiRegion.IanaTimezone(TimeZoneInfo.Utc));
        PiOsSetup.Validate(new PiOsOptions("pi", "pi-passwort-1", Timezone: "UTC"));
    }

    [Fact]
    public void Refuses_boot_partition_without_cloud_init()
    {
        using var boot = Boot();
        File.Delete(boot.File("meta-data"));
        Assert.False(PiOsSetup.Supports(boot.Path));
        Assert.Throws<InvalidOperationException>(() => PiOsSetup.Write(boot.Path, new PiOsOptions("pi", "pi-passwort-1")));
    }
}
