using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Plugs;

/// <summary>Ein Messwert. EnergyWh = Zählerstand des Geräts (kann bei Neustart des Plugs zurückspringen).</summary>
public sealed record PlugReading(double PowerW, double? EnergyWh, double? Voltage, double? Current);

/// <summary>Gerät hinter der Adresse: Generation, Modell, ob ein Passwort nötig ist.</summary>
public sealed record PlugIdentity(int Generation, string Model, bool AuthRequired)
{
    /// <summary>Audit I4: Gen1 meldet sich per Basic-Auth an – das Passwort geht unverschlüsselt durchs Heimnetz.</summary>
    public bool InsecureAuth => Generation < 2 && AuthRequired;

    public static string InsecureAuthHint => L.T("Hinweis: Shelly Gen1 überträgt das Passwort unverschlüsselt (Basic-Auth). Für geschützte Plugs Shelly Plus/Gen2+ empfohlen.");
}

public interface IPlugClient : IDisposable
{
    Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default);
    Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default);
}

/// <summary>
/// Shelly-Geräte mit Leistungsmessung, nur lesend (es wird nie geschaltet):
/// Gen1 über <c>/status</c> (meters/emeters), Gen2+/Gen3/Gen4 über RPC <c>Shelly.GetStatus</c> (switch, pm1, em1).
/// Geschützte Geräte: Gen1 Basic-, Gen2+ Digest-Anmeldung – beides erledigt der HttpClient mit denselben Zugangsdaten.
/// </summary>
public sealed partial class ShellyClient : IPlugClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _http;
    private int? _generation;

    public ShellyClient(string host, string? user = null, string? password = null, HttpMessageHandler? handler = null)
    {
        var baseUri = BaseUri(host);
        handler ??= new SocketsHttpHandler
        {
            Credentials = string.IsNullOrEmpty(password) ? null : new NetworkCredential(string.IsNullOrWhiteSpace(user) ? "admin" : user.Trim(), password),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = Timeout };
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?(:\d{1,5})?$|^\[[0-9A-Fa-f:.]+\](:\d{1,5})?$")]
    private static partial Regex HostPattern();

    /// <summary>Nur Adresse (optional mit Port) – kein Schema, kein Pfad.</summary>
    public static Uri BaseUri(string host)
    {
        var h = (host ?? "").Trim();
        if (!HostPattern().IsMatch(h)) throw new LocalizedException("Ungültige Plug-Adresse: {0} (nur IP oder Hostname, z. B. 192.168.1.60)", h);
        return new Uri($"http://{h}/");
    }

    public async Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        // /shelly ist bei allen Generationen ohne Anmeldung erreichbar
        using var doc = await GetJsonAsync("shelly", ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("gen", out var gen) && gen.TryGetInt32(out var g) && g >= 2)
        {
            _generation = g;
            return new PlugIdentity(g, Str(root, "model") ?? Str(root, "app") ?? "Shelly", root.TryGetProperty("auth_en", out var a) && a.ValueKind == JsonValueKind.True);
        }
        if (root.TryGetProperty("type", out var type))
        {
            _generation = 1;
            return new PlugIdentity(1, type.GetString() ?? "Shelly", root.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.True);
        }
        throw new LocalizedException("Kein Shelly-Gerät (unbekannte Antwort auf /shelly).");
    }

    public async Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default)
    {
        _generation ??= (await IdentifyAsync(ct)).Generation;
        using var doc = await GetJsonAsync(_generation >= 2 ? "rpc/Shelly.GetStatus" : "status", ct);
        return _generation >= 2 ? ParseGen2(doc.RootElement, channel) : ParseGen1(doc.RootElement, channel);
    }

    /// <summary>Gen1: meters[n] (power in W, total in Wattminuten) oder emeters[n] (Shelly EM, total in Wh).</summary>
    public static PlugReading ParseGen1(JsonElement root, int channel)
    {
        if (Item(root, "emeters", channel) is { } em)
            return new PlugReading(Num(em, "power") ?? 0, Num(em, "total"), Num(em, "voltage"), Num(em, "current"));
        if (Item(root, "meters", channel) is { } m)
            return new PlugReading(Num(m, "power") ?? 0, Num(m, "total") is { } wmin ? wmin / 60.0 : null, null, null);
        throw new LocalizedException("Kanal {0} liefert keine Messwerte.", channel);
    }

    /// <summary>Gen2+: switch:n / pm1:n (apower, aenergy.total in Wh) oder em1:n + em1data:n.</summary>
    public static PlugReading ParseGen2(JsonElement root, int channel)
    {
        foreach (var key in new[] { $"switch:{channel}", $"pm1:{channel}" })
            if (root.TryGetProperty(key, out var s) && Num(s, "apower") is { } p)
                return new PlugReading(p, s.TryGetProperty("aenergy", out var e) ? Num(e, "total") : null, Num(s, "voltage"), Num(s, "current"));
        if (root.TryGetProperty($"em1:{channel}", out var em) && Num(em, "act_power") is { } ap)
            return new PlugReading(ap, root.TryGetProperty($"em1data:{channel}", out var d) ? Num(d, "total_act_energy") : null,
                Num(em, "voltage"), Num(em, "current"));
        throw new LocalizedException("Kanal {0} liefert keine Messwerte.", channel);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new LocalizedException("Anmeldung am Plug fehlgeschlagen – Benutzer/Passwort prüfen.");
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new LocalizedException("Kein Shelly-Gerät (unbekannte Antwort auf /shelly).");
        }
        return doc;
    }

    private static JsonElement? Item(JsonElement root, string name, int index) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && index >= 0 && index < arr.GetArrayLength()
            ? arr[index] : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}

/// <summary>Simulierter Plug (Adresse "sim"): Summe der Miner-Leistung + Netzteilverlust + etwas Nebenverbrauch.</summary>
public sealed class SimulatedPlugClient(Func<double> minerPowerW) : IPlugClient
{
    private double _energyWh;
    private DateTime _last = DateTime.Now;

    public static bool IsSimAddress(string host) => string.Equals(host?.Trim(), "sim", StringComparison.OrdinalIgnoreCase);

    public Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default) => Task.FromResult(new PlugIdentity(2, L.T("Simulation"), false));

    public Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default)
    {
        // ~88 % Netzteil-Wirkungsgrad plus 2,5 W Eigenverbrauch
        var power = Math.Round(minerPowerW() / 0.88 + 2.5, 1);
        var now = DateTime.Now;
        _energyWh += power * (now - _last).TotalHours;
        _last = now;
        return Task.FromResult(new PlugReading(power, Math.Round(_energyWh, 2), 230.1, Math.Round(power / 230.1, 3)));
    }

    public void Dispose() { }
}
