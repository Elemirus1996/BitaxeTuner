using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Plugs;

/// <summary>
/// 0.9.14: Steckdosen mit Tasmota-Firmware, nur lesend (es wird nie geschaltet): <c>cm?cmnd=Status 8</c> liefert
/// <c>StatusSNS.ENERGY</c> mit Power (W, bei mehreren Kanälen eine Liste), Total (kWh), Voltage und Current.
/// Mit Web-Passwort gehen Benutzer und Passwort als Parameter mit (so verlangt es Tasmota – im Heimnetz unverschlüsselt).
/// </summary>
public sealed class TasmotaClient : IPlugClient
{
    private readonly HttpClient _http;
    private readonly string _auth;

    public TasmotaClient(string host, string? user = null, string? password = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = ShellyClient.BaseUri(host);
        _http.Timeout = TimeSpan.FromSeconds(5);
        _auth = string.IsNullOrEmpty(password) ? ""
            : $"&user={Uri.EscapeDataString(string.IsNullOrWhiteSpace(user) ? "admin" : user.Trim())}&password={Uri.EscapeDataString(password)}";
    }

    public async Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        using var doc = await CommandAsync("Status 0", ct);
        var status = doc.RootElement.TryGetProperty("Status", out var s) ? s : default;
        var name = Str(status, "DeviceName") ?? (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("FriendlyName", out var f)
            && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() > 0 ? f[0].GetString() : null) ?? "Tasmota";
        var fw = doc.RootElement.TryGetProperty("StatusFWR", out var fwr) ? Str(fwr, "Version") : null;
        return new PlugIdentity(0, fw is null ? $"Tasmota · {name}" : $"Tasmota {fw.Split('(')[0]} · {name}", _auth.Length > 0);
    }

    public async Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default)
    {
        using var doc = await CommandAsync("Status 8", ct);
        return Parse(doc.RootElement, channel);
    }

    /// <summary>StatusSNS.ENERGY: Power als Zahl oder Liste je Kanal, Total in kWh (→ Wh).</summary>
    public static PlugReading Parse(JsonElement root, int channel)
    {
        if (!root.TryGetProperty("StatusSNS", out var sns) || !sns.TryGetProperty("ENERGY", out var e))
            throw new LocalizedException("Tasmota liefert keine Energiewerte (kein Leistungsmesser oder falsche Vorlage).");
        double? Pick(string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.Array when channel >= 0 && channel < v.GetArrayLength() && v[channel].ValueKind == JsonValueKind.Number => v[channel].GetDouble(),
            _ => null,
        } : null;
        var power = Pick("Power") ?? throw new LocalizedException("Kanal {0} liefert keine Messwerte.", channel);
        return new PlugReading(power, Pick("Total") is { } kwh ? kwh * 1000 : null, Pick("Voltage"), Pick("Current"));
    }

    private async Task<JsonDocument> CommandAsync(string command, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"cm?cmnd={Uri.EscapeDataString(command)}{_auth}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new LocalizedException("Anmeldung am Plug fehlgeschlagen – Benutzer/Passwort prüfen.");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        // Falsches oder fehlendes Passwort: {"WARNING":"Need user=<username>&password=<password>"}
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("WARNING", out _))
        {
            doc.Dispose();
            throw new LocalizedException("Anmeldung am Plug fehlgeschlagen – Benutzer/Passwort prüfen.");
        }
        return doc;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
