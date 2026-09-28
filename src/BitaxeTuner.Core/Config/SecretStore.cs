using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Passwörter für Integrationen (NAS, MQTT) – getrennt von config.json, damit sie nie in Datenarchive, Sicherungen
/// oder Übertragungen gelangen. Windows: DPAPI (Rechner-gebunden); Linux: nur für den Dienstbenutzer lesbar (600).
/// </summary>
public sealed class SecretStore(string dataDirectory)
{
    public const string SmbPassword = "backup.smb.password";
    public const string MqttPassword = "mqtt.password";
    private static readonly byte[] Entropy = "BitaxeTuner.Secrets.v1"u8.ToArray();
    private readonly object _lock = new();

    public string FilePath { get; } = Path.Combine(dataDirectory, "secrets.json");

    public bool Has(string key) => Get(key) is { Length: > 0 };

    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!Load().TryGetValue(key, out var stored)) return null;
            try { return Unprotect(stored); }
            catch { return null; } // z. B. Datenordner auf anderen Rechner kopiert → neu eingeben
        }
    }

    /// <summary>Setzen; null oder leer löscht.</summary>
    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            var all = Load();
            if (string.IsNullOrEmpty(value)) all.Remove(key);
            else all[key] = Protect(value);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return OperatingSystem.IsWindows()
            ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine))
            : "plain:" + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored)
    {
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException(L.T("DPAPI nur unter Windows."));
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.LocalMachine));
        }
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        throw new FormatException("Unbekanntes Format.");
    }
}
