namespace BitaxeTuner.Core.Api;

public enum FirmwareKind
{
    Unknown,
    AxeOS,
    NerdQAxe,
    Simulated,
}

/// <summary>
/// Normalisierte Momentaufnahme eines Miners – unabhängig davon, ob AxeOS (Bitaxe)
/// oder die NerdQAxe-Firmware antwortet. Nicht gelieferte Werte sind null.
/// </summary>
public sealed record MinerInfo
{
    public FirmwareKind Firmware { get; init; }
    public string? Hostname { get; init; }
    public string? DeviceModel { get; init; }
    public string? AsicModel { get; init; }
    public string? BoardVersion { get; init; }
    public string? FirmwareVersion { get; init; }

    public int AsicCount { get; init; } = 1;
    public int? SmallCoreCount { get; init; }

    /// <summary>Aktuelle Hashrate in GH/s.</summary>
    public double HashRateGh { get; init; }
    public double? HashRate1mGh { get; init; }
    /// <summary>Von der Firmware erwartete Hashrate in GH/s (nur neuere AxeOS).</summary>
    public double? ExpectedHashRateGh { get; init; }
    public double? ErrorPercent { get; init; }

    public double PowerW { get; init; }
    /// <summary>Eingangsspannung in mV.</summary>
    public double? InputVoltageMv { get; init; }
    public double? CurrentMa { get; init; }

    public double? ChipTempC { get; init; }
    public double? ChipTemp2C { get; init; }
    public double? VrTempC { get; init; }

    public int FrequencyMhz { get; init; }
    public double? ActualFrequencyMhz { get; init; }
    public int CoreVoltageMv { get; init; }
    public double? CoreVoltageActualMv { get; init; }
    public int? DefaultFrequencyMhz { get; init; }
    public int? DefaultCoreVoltageMv { get; init; }

    /// <summary>Roher Lüftermodus der Firmware (0 = manuell, &gt;0 = automatisch/PID).</summary>
    public int? AutoFanMode { get; init; }
    public bool? AutoFan => AutoFanMode is { } m ? m > 0 : null;
    public int? FanPercent { get; init; }
    /// <summary>Zieltemperatur der Lüfterautomatik (AxeOS „temptarget“, NerdQAxe „pidTargetTemp“), falls gemeldet.</summary>
    public int? FanTargetTempC { get; init; }
    /// <summary>Mindestdrehzahl der Lüfter-Automatik in % (AxeOS „minFanSpeed“); null = Firmware kennt sie nicht.</summary>
    public int? FanMinPercent { get; init; }
    public int? FanRpm { get; init; }

    public long SharesAccepted { get; init; }
    public long SharesRejected { get; init; }
    /// <summary>Share-Difficulty, die der Pool gerade vorgibt (AxeOS „poolDifficulty“); null, wenn die Firmware sie nicht liefert.</summary>
    public double? PoolDifficulty { get; init; }
    public string PoolDifficultyText => PoolDifficulty is { } p ? BitaxeTuner.Core.Monitoring.Difficulty.Format(p) : "–";
    public long UptimeSeconds { get; init; }

    public bool OverheatMode { get; init; }
    public string? PowerFault { get; init; }
    public string? HardwareFault { get; init; }
    public bool? OverclockEnabled { get; init; }

    /// <summary>Dieselbe Antwort als Roh-DTO (Pool, Wallet, Best Diff, WLAN …) für die Überwachungsansicht.</summary>
    public SystemInfo? Details { get; init; }

    /// <summary>Höchste Chiptemperatur (bei Mehrchip-Geräten).</summary>
    public double? MaxChipTempC => ChipTemp2C is { } t2 && (ChipTempC is null || t2 > ChipTempC) ? t2 : ChipTempC;

    /// <summary>Effizienz in J/TH.</summary>
    public double? EfficiencyJth => HashRateGh > 0 && PowerW > 0 ? PowerW / (HashRateGh / 1000.0) : null;

    public bool HasFault => OverheatMode || !string.IsNullOrWhiteSpace(PowerFault) || !string.IsNullOrWhiteSpace(HardwareFault);

    public string DisplayName => string.IsNullOrWhiteSpace(Hostname) ? DeviceModel ?? AsicModel ?? "Miner" : Hostname!;
}

/// <summary>Antwort von <c>GET /api/system/asic</c> (nur neuere AxeOS-Versionen).</summary>
public sealed record AsicInfo
{
    public string? AsicModel { get; init; }
    public string? DeviceModel { get; init; }
    public int? AsicCount { get; init; }
    public int? DefaultFrequencyMhz { get; init; }
    public int? DefaultVoltageMv { get; init; }
    public IReadOnlyList<int> FrequencyOptions { get; init; } = [];
    public IReadOnlyList<int> VoltageOptions { get; init; } = [];
}
