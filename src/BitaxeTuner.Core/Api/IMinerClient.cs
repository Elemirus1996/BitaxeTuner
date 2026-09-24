namespace BitaxeTuner.Core.Api;

public interface IMinerClient
{
    /// <summary>IP-Adresse oder Hostname des Geräts.</summary>
    string Address { get; }

    Task<MinerInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Liefert die erlaubten Frequenzen/Spannungen, falls die Firmware das unterstützt, sonst null.</summary>
    Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default);

    Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default);

    /// <param name="autoFanMode">0 = manuell, 1 = automatisch (Firmware-Rohwert, z. B. 2 = PID bei NerdQAxe).</param>
    Task SetFanAsync(int autoFanMode, int manualPercent, CancellationToken ct = default);

    Task RestartAsync(CancellationToken ct = default);
}

public sealed class MinerApiException(string message, Exception? inner = null) : Exception(message, inner);
