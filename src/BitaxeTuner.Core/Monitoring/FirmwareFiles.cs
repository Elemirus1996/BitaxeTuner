using System.Security.Cryptography;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>0.9.25: Welche Dateien eines Releases für einen Miner aufgespielt werden – Weboberfläche (optional) und Firmware.</summary>
public sealed record FirmwareFileSet(FirmwareAsset Firmware, FirmwareAsset? Www);

/// <summary>
/// 0.9.25 Firmware-Update: passende Release-Dateien auswählen und heruntergeladene Dateien prüfen.
/// ESP-Miner (AxeOS): eine „esp-miner.bin“ für alle Boards (das Board steht im Miner), bis 2.14 dazu „www.bin“ – ab 2.15
/// steckt die Weboberfläche in der Firmware. NerdQAxe: je Board eine „esp-miner-&lt;Modell&gt;.bin“ (Modell wie in
/// „deviceModel“, z. B. NerdQAxe++) und „www.bin“. Werkseitige Komplett-Abbilder („…-factory-…“) werden nie genommen.
/// </summary>
public static class FirmwareFiles
{
    /// <summary>Kleinste plausible Größe einer Firmware-Datei (Schutz vor leeren/abgeschnittenen Downloads).</summary>
    public const long MinFirmwareBytes = 512 * 1024;

    /// <summary>Höchstgröße (OTA-Partition der Miner: 4 MB; Factory-Abbilder sind ~15 MB und gehören nie dazu).</summary>
    public const long MaxFirmwareBytes = 4 * 1024 * 1024;

    /// <summary>Passende Dateien oder null mit Grund in <paramref name="error"/>.</summary>
    public static FirmwareFileSet? Select(FirmwareKind kind, string? deviceModel, FirmwareRelease release, out string? error)
    {
        error = null;
        var assets = release.Assets.Where(a => !a.Name.Contains("factory", StringComparison.OrdinalIgnoreCase)).ToList();
        FirmwareAsset? Find(string name) => assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        var www = Find("www.bin");
        switch (kind)
        {
            case FirmwareKind.AxeOS:
                if (Find("esp-miner.bin") is { } fw) return new FirmwareFileSet(fw, www);
                error = L.T("Im Release {0} fehlt „esp-miner.bin“.", release.Tag);
                return null;
            case FirmwareKind.NerdQAxe:
                if (string.IsNullOrWhiteSpace(deviceModel))
                {
                    error = L.T("Der Miner meldet kein Modell – die passende Datei lässt sich nicht sicher bestimmen.");
                    return null;
                }
                var name = $"esp-miner-{deviceModel.Trim()}.bin";
                if (Find(name) is { } nerd) return new FirmwareFileSet(nerd, www);
                error = L.T("Im Release {0} gibt es keine Datei für {1} (gesucht: {2}).", release.Tag, deviceModel.Trim(), name);
                return null;
            default:
                error = L.T("Firmware-Update gibt es nur für ESP-Miner (AxeOS) und NerdQAxe.");
                return null;
        }
    }

    /// <summary>
    /// Heruntergeladene Datei prüfen: Größe wie im Release, SHA-256 wie von GitHub angegeben (falls vorhanden), Firmware
    /// beginnt mit der ESP32-Kennung 0xE9 und passt in die OTA-Partition. Null = in Ordnung, sonst Grund.
    /// </summary>
    public static string? Verify(FirmwareAsset asset, byte[] data, bool isFirmware)
    {
        if (asset.Size > 0 && data.Length != asset.Size)
            return L.T("{0}: Größe {1} Byte statt {2} Byte – Download unvollständig.", asset.Name, data.Length, asset.Size);
        if (asset.Sha256 is { Length: > 0 } expected)
        {
            var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (actual != expected) return L.T("{0}: Prüfsumme stimmt nicht mit GitHub überein.", asset.Name);
        }
        if (isFirmware)
        {
            if (data.Length < MinFirmwareBytes || data.Length > MaxFirmwareBytes)
                return L.T("{0}: ungewöhnliche Größe ({1} Byte) – nicht aufgespielt.", asset.Name, data.Length);
            if (data[0] != 0xE9) return L.T("{0}: keine ESP32-Firmware.", asset.Name);
        }
        else if (data.Length == 0 || data.Length > MaxFirmwareBytes)
            return L.T("{0}: ungewöhnliche Größe ({1} Byte) – nicht aufgespielt.", asset.Name, data.Length);
        return null;
    }
}
