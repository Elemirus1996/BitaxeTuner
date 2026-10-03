using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Fans;

/// <summary>
/// Inhalt von btcfg.json auf dem Pico (WLAN-Betrieb): Rolle, WLAN-Zugang, gemeinsamer Schlüssel, Gerätename.
/// Das WLAN-Passwort steht nur auf dem Pico, der Server speichert lediglich den Schlüssel (secrets.json).
/// </summary>
public sealed record PicoNetworkConfig(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("ssid")] string Ssid,
    [property: JsonPropertyName("psk")] string Password,
    [property: JsonPropertyName("key")] string KeyHex,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port = NetworkLineTransport.DefaultPort)
{
    /// <summary>Neue Einrichtung mit frischem Zufallsschlüssel; prüft die Eingaben.</summary>
    public static PicoNetworkConfig Create(string role, string? ssid, string? password, string? host)
    {
        if (role is not (PicoFanDevice.RoleFans or PicoFanDevice.RoleDisplay)) throw new LocalizedException("Unbekannte Rolle.");
        var s = (ssid ?? "").Trim();
        if (s.Length is 0 or > 32) throw new LocalizedException("WLAN-Name fehlt oder ist zu lang (höchstens 32 Zeichen).");
        var pw = password ?? "";
        if (pw.Length is > 0 and < 8 or > 63) throw new LocalizedException("WLAN-Passwort: 8 bis 63 Zeichen (leer nur bei offenem WLAN).");
        if (s.Any(c => c < 32 || c > 126) || pw.Any(c => c < 32 || c > 126))
            throw new LocalizedException("WLAN-Name und Passwort bitte ohne Umlaute und Sonderzeichen außerhalb von ASCII.");
        var h = string.IsNullOrWhiteSpace(host) ? (role == PicoFanDevice.RoleDisplay ? "bitaxetuner-display" : "bitaxetuner-fans") : host.Trim().ToLowerInvariant();
        if (h.Length > 32 || !h.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') || h.StartsWith('-'))
            throw new LocalizedException("Gerätename: nur a–z, 0–9 und Bindestrich, höchstens 32 Zeichen.");
        return new PicoNetworkConfig(role, s, pw, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), h);
    }

    public byte[] Key => Convert.FromHexString(KeyHex);

    public string ToJson() => JsonSerializer.Serialize(this);
}
