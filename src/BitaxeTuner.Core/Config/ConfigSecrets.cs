using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Tokens und Schlüssel aus config.json heraushalten (Audit P2): Beim Speichern steht in config.json statt des Werts nur ein
/// Verweis „secret:…“, der Wert selbst liegt geschützt in secrets.json (<see cref="SecretStore"/>). Beim Laden werden die
/// Verweise wieder aufgelöst – im Speicher und in allen Oberflächen ändert sich also nichts.
/// Damit landen die Werte nicht mehr in Sicherungen (USB/NAS); Übertragungen (Desktop ↔ Server, Pi-Einrichtung) nehmen sie
/// über <see cref="ResolveFile"/> weiterhin mit. Nicht auflösbare Verweise (z. B. Sicherung auf anderem Rechner) bleiben
/// beim nächsten Speichern erhalten, statt still zu verschwinden.
/// </summary>
public static class ConfigSecrets
{
    public const string RefPrefix = "secret:";
    private const string KeyPrefix = "cfg.";
    private const string HmacKey = "cfg-key";

    /// <summary>Eigenschaften, deren Wert ein Geheimnis ist (Name, optional Name des übergeordneten Objekts).</summary>
    private static readonly HashSet<string> Names =
    [
        "BlockchairApiKey", "CoinGeckoApiKey", "NtfyTopic", "TelegramBotToken", "DiscordWebhookUrl",
        "PushoverUserKey", "PushoverAppToken", "WebhookUrl", "TibberToken",
    ];
    // Außerdem: Server.Token (Desktop-App) und WebView.PinHash (Ansicht-PIN, Audit S6)

    private static bool IsSecret(string name, string? parent) =>
        Names.Contains(name) || (name == "Token" && parent == "Server") || (name == "PinHash" && parent == "WebView");

    /// <summary>
    /// Geheimnisse in <paramref name="root"/> durch Verweise ersetzen und in secrets.json ablegen. Erst wird secrets.json
    /// geschrieben, dann darf der Aufrufer config.json schreiben – so geht nie ein Wert verloren.
    /// </summary>
    /// <param name="keepRefs">Beim Laden nicht auflösbare Verweise (Pfad → Verweis), die erhalten bleiben sollen.</param>
    public static void Externalize(JsonNode root, SecretStore store, IReadOnlyDictionary<string, string>? keepRefs = null)
    {
        var values = new Dictionary<string, string>();
        var key = HmacKeyOf(store);
        Walk(root, null, "", (obj, name, path) =>
        {
            var value = obj[name]?.GetValue<string>() ?? "";
            if (value.StartsWith(RefPrefix, StringComparison.Ordinal)) return;   // bereits Verweis
            if (value.Length == 0)
            {
                if (keepRefs is not null && keepRefs.TryGetValue(path, out var kept)) obj[name] = kept;
                return;
            }
            var id = KeyPrefix + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
            values[id] = value;
            obj[name] = RefPrefix + id;
        });
        var keep = new HashSet<string>(values.Keys);
        if (keepRefs is not null) keep.UnionWith(keepRefs.Values.Select(r => r[RefPrefix.Length..]));
        store.ReplaceGroup(KeyPrefix, values, keep);
    }

    /// <summary>Verweise durch die Werte ersetzen. Ergebnis: nicht auflösbare Verweise (Pfad → Verweis), Wert dort leer.</summary>
    public static Dictionary<string, string> Resolve(JsonNode root, SecretStore? store)
    {
        var missing = new Dictionary<string, string>();
        Walk(root, null, "", (obj, name, path) =>
        {
            var value = obj[name]?.GetValue<string>() ?? "";
            if (!value.StartsWith(RefPrefix, StringComparison.Ordinal)) return;
            var resolved = store?.Get(value[RefPrefix.Length..]);
            if (resolved is null) missing[path] = value;
            obj[name] = resolved ?? "";
        });
        return missing;
    }

    /// <summary>config.json mit aufgelösten Werten (für Übertragungen); <paramref name="target"/> darf eine andere Datei sein.</summary>
    public static void ResolveFile(string configFile, string target)
    {
        var root = JsonNode.Parse(File.ReadAllText(configFile));
        if (root is not null) Resolve(root, new SecretStore(Path.GetDirectoryName(Path.GetFullPath(configFile))!));
        File.WriteAllText(target, root?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "{}");
    }

    private static byte[] HmacKeyOf(SecretStore store)
    {
        if (store.Get(HmacKey) is { Length: > 0 } k) return Convert.FromBase64String(k);
        var fresh = RandomNumberGenerator.GetBytes(32);
        store.Set(HmacKey, Convert.ToBase64String(fresh));
        return fresh;
    }

    private static void Walk(JsonNode? node, string? parent, string path, Action<JsonObject, string, string> onSecret)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    var p = path.Length == 0 ? name : path + "." + name;
                    if (child is JsonValue v && v.TryGetValue<string>(out _) && IsSecret(name, parent)) onSecret(obj, name, p);
                    else Walk(child, name, p, onSecret);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) Walk(arr[i], parent, $"{path}[{i}]", onSecret);
                break;
        }
    }
}
