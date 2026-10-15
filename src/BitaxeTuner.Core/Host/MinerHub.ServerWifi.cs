using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>WLAN des Servers (nur für die Pico-Einrichtung; das Passwort wird nie angezeigt oder gespeichert).</summary>
public sealed record ServerWifi(string Ssid, string Password);

/// <summary>
/// 0.9.17: „Pico für WLAN einrichten“ kann WLAN-Name und -Passwort vom Server übernehmen. Auf dem Raspberry Pi/Linux legt
/// der Dienst (ohne Root-Rechte) die Anforderung &lt;Datenordner&gt;/wifi-request an; die root-eigene Pfad-Unit
/// „bitaxetuner-wifi.path“ startet daraufhin /usr/local/lib/bitaxetuner/wifi-read, das nur das aktive WLAN liest und
/// wifi-answer.json (nur für den Dienst lesbar) schreibt. Die Antwort wird sofort gelesen und gelöscht.
/// </summary>
public sealed partial class MinerHub
{
    private const string ServerWifiUnit = "/etc/systemd/system/bitaxetuner-wifi.path";

    /// <summary>Für Tests: WLAN des Servers vorgeben (null = wie im Betrieb).</summary>
    internal Func<ServerWifi?>? ServerWifiOverride { get; set; }

    /// <summary>Kann dieser Server sein WLAN für die Pico-Einrichtung lesen? (Linux mit eingerichteter Pfad-Unit)</summary>
    public bool ServerWifiSupported => ServerWifiOverride is not null || (OperatingSystem.IsLinux() && File.Exists(ServerWifiUnit));

    /// <summary>WLAN des Servers lesen; wirft eine verständliche Meldung, wenn es nicht geht.</summary>
    public async Task<ServerWifi> ReadServerWifiAsync(CancellationToken ct = default)
    {
        if (ServerWifiOverride is { } fake)
            return fake() ?? throw new LocalizedException("Der Server ist nicht per WLAN verbunden – bitte WLAN-Name und Passwort eintragen.");
        if (!ServerWifiSupported)
            throw new LocalizedException("WLAN übernehmen geht nur auf dem Raspberry Pi/Linux mit aktuellen Systemdateien (Desktop-App → Server → „SSH ▾“ → „Systemdateien per SSH erneuern …“).");
        var dir = Options.DataDirectory;
        var request = Path.Combine(dir, "wifi-request");
        var answer = Path.Combine(dir, "wifi-answer.json");
        try { File.Delete(answer); } catch (IOException) { /* gleich überschrieben */ }
        await File.WriteAllTextAsync(request, DateTime.Now.ToString("O"), ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(answer))
        {
            if (DateTime.UtcNow > deadline)
            {
                try { File.Delete(request); } catch (IOException) { /* egal */ }
                throw new LocalizedException("Der Server hat sein WLAN nicht gemeldet – Systemdateien erneuern oder WLAN von Hand eintragen.");
            }
            await Task.Delay(200, ct);
        }
        string json;
        try { json = await File.ReadAllTextAsync(answer, ct); }
        finally { try { File.Delete(answer); } catch (IOException) { /* nur für den Dienst lesbar */ } }
        using var doc = JsonDocument.Parse(json);
        string Get(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var error = Get("error");
        if (error == "nowifi") throw new LocalizedException("Der Server ist nicht per WLAN verbunden – bitte WLAN-Name und Passwort eintragen.");
        if (error == "nopsk") throw new LocalizedException("Das WLAN-Passwort des Servers ist nicht lesbar (offenes Netz oder Firmen-WLAN?) – bitte von Hand eintragen.");
        if (error.Length > 0) throw new LocalizedException("WLAN des Servers nicht übernehmbar: {0}", error);
        return new ServerWifi(Get("ssid"), Get("psk"));
    }
}
