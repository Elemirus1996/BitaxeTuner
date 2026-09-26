using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitaxeTuner.Core.Api;

/// <summary>
/// Antwort von GET http://&lt;ip&gt;/api/system/info (AxeOS / ESP-Miner).
/// Nicht jedes Feld existiert in jeder Firmware-Version -> alles nullable/default-tolerant.
/// </summary>
public sealed class SystemInfo
{
    public string? hostname { get; set; }

    // Leistung
    public double hashRate { get; set; }              // GH/s
    public double expectedHashrate { get; set; }      // GH/s
    public double power { get; set; }                 // W
    public double voltage { get; set; }               // mV (Input)
    public double current { get; set; }               // mA
    public double coreVoltage { get; set; }           // mV (Soll)
    public double coreVoltageActual { get; set; }     // mV (Ist)
    public double frequency { get; set; }             // MHz

    // Temperaturen
    public double temp { get; set; }                  // ASIC °C
    public double vrTemp { get; set; }                // Spannungsregler °C
    public int overheat_mode { get; set; }

    // Lüfter
    public double fanspeed { get; set; }              // %
    public int fanrpm { get; set; }
    public int autofanspeed { get; set; }
    public int temptarget { get; set; }

    // Shares / Difficulty
    public double sharesAccepted { get; set; }
    public double sharesRejected { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? bestDiff { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? bestSessionDiff { get; set; }

    // Pool
    public string? stratumURL { get; set; }
    public int stratumPort { get; set; }
    public string? stratumUser { get; set; }
    public int isUsingFallbackStratum { get; set; }
    public double responseTime { get; set; }          // ms, mittlere Pool-Antwortzeit (AxeOS ≥ 2.x)
    public string? fallbackStratumURL { get; set; }
    public int fallbackStratumPort { get; set; }

    // System
    public int blockFound { get; set; }
    public long uptimeSeconds { get; set; }
    public int wifiRSSI { get; set; }
    public string? wifiStatus { get; set; }
    public long freeHeap { get; set; }

    [JsonPropertyName("ASICModel")]
    public string? AsicModel { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? boardVersion { get; set; }

    public string? version { get; set; }

    /// <summary>Ersatz, wenn kein Roh-JSON vorliegt (z. B. Simulation): Kernwerte aus der normalisierten Sicht.</summary>
    public static SystemInfo FromMinerInfo(MinerInfo m) => new()
    {
        hostname = m.Hostname,
        hashRate = m.HashRateGh,
        expectedHashrate = m.ExpectedHashRateGh ?? 0,
        power = m.PowerW,
        voltage = m.InputVoltageMv ?? 0,
        current = m.CurrentMa ?? 0,
        coreVoltage = m.CoreVoltageMv,
        coreVoltageActual = m.CoreVoltageActualMv ?? m.CoreVoltageMv,
        frequency = m.FrequencyMhz,
        temp = m.MaxChipTempC ?? 0,
        vrTemp = m.VrTempC ?? 0,
        overheat_mode = m.OverheatMode ? 1 : 0,
        fanspeed = m.FanPercent ?? 0,
        fanrpm = m.FanRpm ?? 0,
        autofanspeed = m.AutoFanMode ?? 0,
        sharesAccepted = m.SharesAccepted,
        sharesRejected = m.SharesRejected,
        uptimeSeconds = m.UptimeSeconds,
        AsicModel = m.AsicModel,
        boardVersion = m.BoardVersion,
        version = m.FirmwareVersion,
    };
}

/// <summary>
/// bestDiff/boardVersion kommen je nach Firmware als String ODER als Zahl.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString()
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
