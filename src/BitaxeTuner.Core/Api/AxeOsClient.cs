using System.Text;
using System.Text.Json;

namespace BitaxeTuner.Core.Api;

/// <summary>
/// HTTP-Client für die REST-API von AxeOS (bitaxeorg/ESP-Miner) und der NerdQAxe-Firmware
/// (shufps/ESP-Miner-NerdQAxePlus). Beide nutzen <c>/api/system/info</c> und <c>PATCH /api/system</c>,
/// unterscheiden sich aber in einigen Feldnamen.
/// </summary>
public sealed class AxeOsClient : IMinerClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private HashSet<string> _lastKeys = new(StringComparer.Ordinal);

    public AxeOsClient(string address, HttpClient? http = null)
    {
        Address = address.Trim();
        var baseUri = Address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Address : $"http://{Address}";
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = RequestTimeout };
        _http.BaseAddress ??= new Uri(baseUri.TrimEnd('/') + "/");
    }

    public string Address { get; }

    public async Task<MinerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/system/info", ct).ConfigureAwait(false);
        var info = Parse(doc.RootElement);
        _lastKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return info;
    }

    public async Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync("api/system/asic", ct).ConfigureAwait(false);
            var r = doc.RootElement;
            return new AsicInfo
            {
                AsicModel = Str(r, "ASICModel"),
                DeviceModel = Str(r, "deviceModel"),
                AsicCount = Int(r, "asicCount"),
                DefaultFrequencyMhz = Int(r, "defaultFrequency"),
                DefaultVoltageMv = Int(r, "defaultVoltage"),
                FrequencyOptions = IntArray(r, "frequencyOptions"),
                VoltageOptions = IntArray(r, "voltageOptions"),
            };
        }
        catch (MinerApiException)
        {
            // Ältere AxeOS-Versionen und die NerdQAxe-Firmware kennen diesen Endpunkt nicht.
            return null;
        }
    }

    public Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["frequency"] = frequencyMhz,
            ["coreVoltage"] = coreVoltageMv,
        };
        // Neuere AxeOS-Versionen erlauben Werte außerhalb der Auswahlliste nur mit aktiviertem Overclocking.
        if (_lastKeys.Contains("overclockEnabled"))
            body["overclockEnabled"] = 1;
        return PatchAsync(body, ct);
    }

    public Task SetFanAsync(int autoFanMode, int manualPercent, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["autofanspeed"] = autoFanMode };
        if (autoFanMode == 0)
        {
            // Ältere AxeOS-Versionen nutzen "fanspeed", neuere "manualFanSpeed".
            if (_lastKeys.Contains("manualFanSpeed") || _lastKeys.Count == 0)
                body["manualFanSpeed"] = manualPercent;
            else
                body["fanspeed"] = manualPercent;
        }
        return PatchAsync(body, ct);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsync("api/system/restart", null, ct).ConfigureAwait(false);
            // Manche Firmware-Versionen trennen die Verbindung sofort – das ist kein Fehler.
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task PatchAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Patch, "api/system") { Content = content };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new MinerApiException($"{Address}: PATCH /api/system fehlgeschlagen ({(int)resp.StatusCode}) {text}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MinerApiException($"{Address}: Einstellungen konnten nicht gesendet werden – {ex.Message}", ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new MinerApiException($"{Address}: GET /{path} lieferte {(int)resp.StatusCode}");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MinerApiException($"{Address}: nicht erreichbar – {ex.Message}", ex);
        }
    }

    /// <summary>Wandelt die JSON-Antwort von <c>/api/system/info</c> in ein <see cref="MinerInfo"/> um.</summary>
    public static MinerInfo Parse(JsonElement r)
    {
        var firmware = DetectFirmware(r);

        double? chipTemp = Dbl(r, "temp");
        double? chipTemp2 = Dbl(r, "temp2");
        if (r.TryGetProperty("asicTemps", out var asicTemps) && asicTemps.ValueKind == JsonValueKind.Array)
        {
            var temps = asicTemps.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetDouble()).Where(t => t > 0).ToList();
            if (temps.Count > 0)
                chipTemp2 = temps.Max();
        }
        if (chipTemp2 is <= 0) chipTemp2 = null;

        var inputVoltage = Dbl(r, "voltage");
        if (inputVoltage is > 0 and < 100) inputVoltage *= 1000; // Volt → mV

        return new MinerInfo
        {
            Firmware = firmware,
            Hostname = Str(r, "hostname"),
            DeviceModel = Str(r, "deviceModel"),
            AsicModel = Str(r, "ASICModel") ?? Str(r, "asicModel"),
            BoardVersion = Str(r, "boardVersion"),
            FirmwareVersion = Str(r, "version") ?? Str(r, "axeOSVersion"),
            AsicCount = Int(r, "asicCount") is > 0 and var n ? n : 1,
            SmallCoreCount = Int(r, "smallCoreCount"),
            HashRateGh = Dbl(r, "hashRate") ?? 0,
            HashRate1mGh = Dbl(r, "hashRate_1m"),
            ExpectedHashRateGh = Dbl(r, "expectedHashrate") is > 0 and var e ? e : null,
            ErrorPercent = Dbl(r, "errorPercentage"),
            PowerW = Dbl(r, "power") ?? 0,
            InputVoltageMv = inputVoltage,
            CurrentMa = Dbl(r, "current"),
            ChipTempC = chipTemp is > 0 ? chipTemp : null,
            ChipTemp2C = chipTemp2,
            VrTempC = Dbl(r, "vrTemp") is > 0 and var vr ? vr : null,
            FrequencyMhz = (int)Math.Round(Dbl(r, "frequency") ?? 0),
            ActualFrequencyMhz = Dbl(r, "actualFrequency"),
            CoreVoltageMv = Int(r, "coreVoltage") ?? 0,
            CoreVoltageActualMv = Dbl(r, "coreVoltageActual"),
            DefaultFrequencyMhz = Int(r, "defaultFrequency"),
            DefaultCoreVoltageMv = Int(r, "defaultCoreVoltage"),
            AutoFanMode = Int(r, "autofanspeed"),
            FanPercent = Int(r, "fanspeed") ?? Int(r, "manualFanSpeed"),
            FanRpm = Int(r, "fanrpm"),
            SharesAccepted = (long)(Dbl(r, "sharesAccepted") ?? 0),
            SharesRejected = (long)(Dbl(r, "sharesRejected") ?? 0),
            UptimeSeconds = (long)(Dbl(r, "uptimeSeconds") ?? 0),
            OverheatMode = (Int(r, "overheat_mode") ?? 0) != 0,
            PowerFault = Str(r, "power_fault"),
            HardwareFault = Str(r, "hardware_fault"),
            OverclockEnabled = Int(r, "overclockEnabled") is { } oc ? oc != 0 : null,
        };
    }

    public static FirmwareKind DetectFirmware(JsonElement r)
    {
        if (r.TryGetProperty("asicTemps", out _) || r.TryGetProperty("jobInterval", out _) || r.TryGetProperty("pidTargetTemp", out _))
            return FirmwareKind.NerdQAxe;
        if (r.TryGetProperty("axeOSVersion", out _) || r.TryGetProperty("ASICModel", out _) || r.TryGetProperty("overclockEnabled", out _))
            return FirmwareKind.AxeOS;
        return FirmwareKind.Unknown;
    }

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static double? Dbl(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static int? Int(JsonElement r, string name) => Dbl(r, name) is { } d ? (int)Math.Round(d) : null;

    private static IReadOnlyList<int> IntArray(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => (int)Math.Round(e.GetDouble())).ToList()
            : [];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
