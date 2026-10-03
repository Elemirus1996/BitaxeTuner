using BitaxeTuner.Core.I18n;
namespace BitaxeTuner.Core.Api;

/// <summary>Herkunft einer Frequenz-/Spannungsänderung (wird in history.db protokolliert).</summary>
public enum TuningSource
{
    Manual,
    Benchmark,
    Restore,
    /// <summary>Freigegebene Automatik-Regel (Temperaturschutz, Zeitplan, Strompreis).</summary>
    Automatic,
}

public interface IMinerClient
{
    /// <summary>IP-Adresse oder Hostname des Geräts.</summary>
    string Address { get; }

    Task<MinerInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Liefert die erlaubten Frequenzen/Spannungen, falls die Firmware das unterstützt, sonst null.</summary>
    Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default);

    Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, TuningSource source = TuningSource.Manual, CancellationToken ct = default);

    /// <param name="autoFanMode">0 = manuell, 1 = automatisch (Firmware-Rohwert, z. B. 2 = PID bei NerdQAxe).</param>
    Task SetFanAsync(int autoFanMode, int manualPercent, CancellationToken ct = default);

    /// <summary>Zieltemperatur der Lüfterautomatik (AxeOS „temptarget“, NerdQAxe „pidTargetTemp“); ohne Unterstützung nichts.</summary>
    Task SetFanTargetAsync(int targetTempC, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Mindestdrehzahl der Lüfter-Automatik in % (nur Firmware mit „minFanSpeed“).</summary>
    Task SetFanMinAsync(int percent, CancellationToken ct = default) => Task.CompletedTask;

    Task RestartAsync(CancellationToken ct = default);

    /// <summary>
    /// true, wenn für diese Werte in AxeOS der Overclocking-Modus (<c>overclockEnabled</c>) eingeschaltet werden müsste –
    /// für den Bestätigungsdialog.
    /// </summary>
    Task<bool> WillEnableOverclockAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Bisheriger Log-Puffer des Miners (AxeOS: GET /api/system/logs, Text seit dem letzten Neustart).</summary>
    Task<string> GetLogBufferAsync(CancellationToken ct = default) =>
        throw new MinerApiException(L.T("{0}: Log-Puffer wird von diesem Gerät nicht unterstützt", Address));

    /// <summary>Unveränderte JSON-Antwort von /api/system/info.</summary>
    Task<string> GetRawInfoAsync(CancellationToken ct = default) =>
        throw new MinerApiException(L.T("{0}: Rohdaten werden von diesem Gerät nicht unterstützt", Address));

    /// <summary>PATCH /api/system mit beliebigen Feldern.</summary>
    Task PatchSettingsAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default) =>
        throw new MinerApiException(L.T("{0}: Einstellungen werden von diesem Gerät nicht unterstützt", Address));
}

public sealed class MinerApiException(string message, Exception? inner = null) : Exception(message, inner);
