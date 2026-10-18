using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.24 „Rückgängig“ nach dem Speichern (Desktop): Kopie der Einstellungen vor dem Speichern. Zurückholen kopiert alle
/// gespeicherten Eigenschaften in dasselbe Objekt zurück – andere Teile der App halten Verweise darauf. Laufzeitfelder
/// ohne Speicherung (Dateipfad, nicht auflösbare Token-Verweise) bleiben unberührt.
/// </summary>
public sealed class ConfigSnapshot
{
    private readonly string _json;

    /// <summary>Zeitpunkt der Kopie (für Protokoll und Hinweis).</summary>
    public DateTime Taken { get; }

    private ConfigSnapshot(string json, DateTime taken) => (_json, Taken) = (json, taken);

    public static ConfigSnapshot Take(AppConfig config, DateTime? now = null) => new(JsonSerializer.Serialize(config), now ?? DateTime.Now);

    public void RestoreInto(AppConfig target)
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(_json)!;
        foreach (var p in typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.SetMethod is not { IsPublic: true } || p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            p.SetValue(target, p.GetValue(copy));
        }
    }
}
