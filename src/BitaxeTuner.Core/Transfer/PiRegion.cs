using System.Globalization;

namespace BitaxeTuner.Core.Transfer;

/// <summary>
/// Land (WLAN-Regulierung), Zeitzone und Tastaturbelegung für „Raspberry Pi vorbereiten“ – aus den Einstellungen
/// dieses PCs abgeleitet statt fest Deutschland. Im Dialog sichtbar und änderbar.
/// </summary>
public sealed record PiRegion(string Country, string Timezone, string Keyboard)
{
    public static readonly PiRegion Germany = new("DE", "Europe/Berlin", "de");

    /// <param name="inputLanguage">Eingabesprache (Windows-Tastatur); ohne Angabe die Anzeigesprache.</param>
    public static PiRegion FromSystem(CultureInfo? inputLanguage = null)
    {
        string country;
        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant();
            country = region.Length == 2 && region != "ZZ" ? region : Germany.Country;
        }
        catch (ArgumentException) { country = Germany.Country; }

        return new PiRegion(country, IanaTimezone(TimeZoneInfo.Local, country) ?? Germany.Timezone,
            KeyboardFor(inputLanguage ?? CultureInfo.CurrentCulture, country));
    }

    /// <summary>Windows-Zeitzonen-ID (z. B. „W. Europe Standard Time“) in die IANA-ID für Linux umrechnen.</summary>
    public static string? IanaTimezone(TimeZoneInfo zone, string? country = null)
    {
        if (zone.HasIanaId) return zone.Id;
        if (country is not null && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, country, out var byRegion)) return byRegion;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }

    /// <summary>XKB-Tastaturbelegung (Linux) zu einer Sprache; Varianten nach Land (de-CH → ch, pt-BR → br, en-GB → gb).</summary>
    public static string KeyboardFor(CultureInfo culture, string? country = null)
    {
        var region = (country ?? SafeRegion(culture) ?? "").ToUpperInvariant();
        return (culture.TwoLetterISOLanguageName, region) switch
        {
            ("de", "CH") or ("fr", "CH") or ("it", "CH") => "ch",
            ("fr", "BE") or ("nl", "BE") => "be",
            ("fr", "CA") => "ca",
            ("en", "GB") or ("en", "IE") => "gb",
            ("pt", "BR") => "br",
            ("de", _) => "de",
            ("fr", _) => "fr",
            ("es", _) => "es",
            ("it", _) => "it",
            ("pt", _) => "pt",
            ("pl", _) => "pl",
            ("nl", _) => "us",   // in den Niederlanden üblich: US-International
            ("sv", _) => "se",
            ("da", _) => "dk",
            ("nb", _) or ("nn", _) or ("no", _) => "no",
            ("fi", _) => "fi",
            ("cs", _) => "cz",
            ("sk", _) => "sk",
            ("hu", _) => "hu",
            ("tr", _) => "tr",
            ("ru", _) => "ru",
            ("uk", _) => "ua",
            ("ja", _) => "jp",
            _ => "us",
        };
    }

    private static string? SafeRegion(CultureInfo culture)
    {
        try { return culture.IsNeutralCulture ? null : new RegionInfo(culture.Name).TwoLetterISORegionName; }
        catch (ArgumentException) { return null; }
    }
}
