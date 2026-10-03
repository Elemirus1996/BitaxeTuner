using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Api;

/// <summary>
/// HTTP-Client für die REST-API von AxeOS (bitaxeorg/ESP-Miner) und der NerdQAxe-Firmware
/// (shufps/ESP-Miner-NerdQAxePlus). Beide nutzen <c>/api/system/info</c> und <c>PATCH /api/system</c>,
/// unterscheiden sich aber in einigen Feldnamen.
/// </summary>
public sealed class AxeOsClient : IMinerClient, IDisposable
{
    // Wie bisher im BitaxeMonitor: 5 s je Abfrage, damit ein hängender Miner das Polling nicht aufhält.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Optionen für das Roh-DTO <see cref="SystemInfo"/> (wie im BitaxeMonitor).</summary>
    private static readonly JsonSerializerOptions SystemInfoOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private HashSet<string> _lastKeys = new(StringComparer.Ordinal);
    private bool? _lastOverclockEnabled;
    private AsicInfo? _asic;
    private bool _asicLoaded;

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
        _lastOverclockEnabled = info.OverclockEnabled;
        return info;
    }

    public async Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync("api/system/asic", ct).ConfigureAwait(false);
            var r = doc.RootElement;
            _asic = new AsicInfo
            {
                AsicModel = Str(r, "ASICModel"),
                DeviceModel = Str(r, "deviceModel"),
                AsicCount = Int(r, "asicCount"),
                DefaultFrequencyMhz = Int(r, "defaultFrequency"),
                DefaultVoltageMv = Int(r, "defaultVoltage"),
                FrequencyOptions = IntArray(r, "frequencyOptions"),
                VoltageOptions = IntArray(r, "voltageOptions"),
            };
            _asicLoaded = true;
            return _asic;
        }
        catch (MinerApiException)
        {
            // Ältere AxeOS-Versionen und die NerdQAxe-Firmware kennen diesen Endpunkt nicht.
            _asicLoaded = true;
            return null;
        }
    }

    public async Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, TuningSource source = TuningSource.Manual, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["frequency"] = frequencyMhz,
            ["coreVoltage"] = coreVoltageMv,
        };
        // AxeOS (geprüft gegen v2.15.3): Werte außerhalb von frequencyOptions/voltageOptions sind für
        // "custom voltage/frequency" gedacht. overclockEnabled wird nur dann gesetzt – und nie ungefragt,
        // der Bestätigungsdialog fragt vorher WillEnableOverclockAsync ab.
        if (await WillEnableOverclockAsync(frequencyMhz, coreVoltageMv, ct).ConfigureAwait(false))
            body["overclockEnabled"] = 1;
        await PatchAsync(body, ct).ConfigureAwait(false);
    }

    public async Task<string> GetRawInfoAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/system/info", ct).ConfigureAwait(false);
        return doc.RootElement.GetRawText();
    }

    public Task PatchSettingsAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default) =>
        PatchAsync(values.ToDictionary(kv => kv.Key, kv => kv.Value), ct);

    public async Task<string> GetLogBufferAsync(CancellationToken ct = default)
    {
        // Eigener Client mit längerem Timeout: der Puffer ist mehrere 100 KB groß (bei .182 ca. 550 KB)
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var resp = await http.GetAsync("api/system/logs", ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new MinerApiException(L.T("{0}: Diese Firmware bietet keinen Log-Puffer (/api/system/logs)", Address));
            if (!resp.IsSuccessStatusCode)
                throw new MinerApiException($"{Address}: GET /api/system/logs lieferte {(int)resp.StatusCode}");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MinerApiException(L.T("{0}: Log-Puffer nicht abrufbar – {1}", Address, ex.Message), ex);
        }
    }

    public async Task<bool> WillEnableOverclockAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default)
    {
        if (!_lastKeys.Contains("overclockEnabled") || _lastOverclockEnabled == true) return false;
        if (!_asicLoaded) await GetAsicInfoAsync(ct).ConfigureAwait(false);
        if (_asic is null || _asic.FrequencyOptions.Count == 0 || _asic.VoltageOptions.Count == 0) return false;
        return !_asic.FrequencyOptions.Contains(frequencyMhz) || !_asic.VoltageOptions.Contains(coreVoltageMv);
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

    /// <summary>Mindestdrehzahl der Automatik (AxeOS „minFanSpeed“, 0–99 %); Firmware ohne das Feld: nichts senden.</summary>
    public Task SetFanMinAsync(int percent, CancellationToken ct = default) =>
        _lastKeys.Contains("minFanSpeed") ? PatchAsync(new Dictionary<string, object> { ["minFanSpeed"] = Math.Clamp(percent, 0, 99) }, ct) : Task.CompletedTask;

    public Task SetFanTargetAsync(int targetTempC, CancellationToken ct = default)
    {
        // NerdQAxe regelt per PID auf „pidTargetTemp“, AxeOS (≥ 2.x) auf „temptarget“; ältere Firmware: nichts senden
        var key = _lastKeys.Contains("pidTargetTemp") ? "pidTargetTemp" : _lastKeys.Contains("temptarget") ? "temptarget" : null;
        return key is null ? Task.CompletedTask : PatchAsync(new Dictionary<string, object> { [key] = targetTempC }, ct);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        try
        {
            // AxeOS v2.15.3 antwortet "System will restart shortly." und startet nach 1 s neu.
            using var resp = await _http.PostAsync("api/system/restart", null, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new MinerApiException(L.T("{0}: Neustart abgelehnt ({1})", Address, (int)resp.StatusCode));
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            // Keine Verbindung aufgebaut → der Neustart wurde nicht ausgelöst.
            throw new MinerApiException(L.T("{0}: Neustart nicht möglich – keine Verbindung", Address), ex);
        }
        catch (HttpRequestException)
        {
            // Verbindung während der Antwort abgebrochen: manche Firmware startet sofort neu – kein Fehler.
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MinerApiException(L.T("{0}: Neustart – Zeitüberschreitung", Address), ex);
        }
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
                throw new MinerApiException(L.T("{0}: PATCH /api/system fehlgeschlagen ({1}) {2}", Address, (int)resp.StatusCode, text));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MinerApiException(L.T("{0}: Einstellungen konnten nicht gesendet werden – {1}", Address, ex.Message), ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new MinerApiException(L.T("{0}: GET /{1} lieferte {2}", Address, path, (int)resp.StatusCode));
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MinerApiException(L.T("{0}: nicht erreichbar – {1}", Address, ex.Message), ex);
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
            FanTargetTempC = (Int(r, "pidTargetTemp") ?? Int(r, "temptarget")) is > 0 and var ft ? ft : null,
            FanMinPercent = Int(r, "minFanSpeed"),
            FanPercent = Int(r, "fanspeed") ?? Int(r, "manualFanSpeed"),
            FanRpm = Int(r, "fanrpm"),
            SharesAccepted = (long)(Dbl(r, "sharesAccepted") ?? 0),
            SharesRejected = (long)(Dbl(r, "sharesRejected") ?? 0),
            PoolDifficulty = (Dbl(r, "poolDifficulty") ?? Dbl(r, "poolDiff")) is > 0 and var pd ? pd : null,
            UptimeSeconds = (long)(Dbl(r, "uptimeSeconds") ?? 0),
            OverheatMode = (Int(r, "overheat_mode") ?? 0) != 0,
            PowerFault = Str(r, "power_fault"),
            HardwareFault = Str(r, "hardware_fault"),
            OverclockEnabled = Int(r, "overclockEnabled") is { } oc ? oc != 0 : null,
            Details = ParseDetails(r),
        };
    }

    /// <summary>Dieselbe Antwort als Roh-DTO für die Überwachungsansicht (aus BitaxeMonitor).</summary>
    private static SystemInfo? ParseDetails(JsonElement r)
    {
        try { return r.Deserialize<SystemInfo>(SystemInfoOptions); }
        catch (JsonException) { return null; }
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
