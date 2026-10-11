using System.Text.Json.Nodes;

namespace BitaxeTuner.Core.Pools;

/// <summary>
/// ESP-Miner ab 2.15 (z. B. Gamma): <c>stratumURL</c>/<c>fallbackStratum*</c> sind nur noch Anzeige – ein PATCH damit wird
/// ohne Fehler ignoriert. Pools ändert man dort über die Liste <c>pools</c> (Eintrag je <c>id</c>). Diese Klasse übersetzt
/// die alten Felder in den passenden Listeneintrag: Haupt-Pool → <c>primaryPoolIndex</c>, Ersatz-Pool → <c>secondaryPoolIndex</c>.
/// Alle übrigen Felder des Eintrags (TLS, Protokoll, Difficulty …) bleiben, das Passwort bleibt über „*****“ erhalten.
/// </summary>
public static class PoolWrite
{
    // Adresse, Port, Benutzer, Passwort, TLS – im alten Format je Haupt- und Ersatz-Pool
    private static readonly string[] Primary = ["stratumURL", "stratumPort", "stratumUser", "stratumPassword", "stratumTLS"];
    private static readonly string[] Fallback = ["fallbackStratumURL", "fallbackStratumPort", "fallbackStratumUser", "fallbackStratumPassword", "fallbackStratumTLS"];

    /// <summary>Enthält der PATCH Pool-Felder im alten Format?</summary>
    public static bool HasLegacyPoolFields(IReadOnlyDictionary<string, object> patch) =>
        patch.Keys.Any(k => Primary.Contains(k) || Fallback.Contains(k));

    /// <summary>PATCH für die Firmware des Miners passend machen (alte Firmware: unverändert).</summary>
    public static Dictionary<string, object> Adapt(string currentRawInfo, IReadOnlyDictionary<string, object> patch)
    {
        var result = patch.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!HasLegacyPoolFields(patch)) return result;
        var root = JsonNode.Parse(currentRawInfo) as JsonObject;
        if (root?["pools"] is not JsonArray list || root["primaryPoolIndex"] is null)
        {
            // alte Firmware/NerdQAxe: Felder bleiben; TLS nur, wenn die Firmware es kennt
            foreach (var k in new[] { Primary[4], Fallback[4] })
                if (root is null || !root.ContainsKey(k)) result.Remove(k);
            return result;
        }

        var entries = list.OfType<JsonObject>().ToList();
        var used = entries.Where(e => Str(e["stratumURL"]).Length > 0).Select(e => Int(e["id"])).ToHashSet();
        var primaryIndex = Int(root["primaryPoolIndex"]);
        var secondaryIndex = root["secondaryPoolIndex"] is null ? -1 : Int(root["secondaryPoolIndex"]);
        var pools = new JsonArray();

        foreach (var (keys, isPrimary) in new[] { (Primary, true), (Fallback, false) })
        {
            if (!keys.Any(result.ContainsKey)) continue;
            var index = isPrimary ? primaryIndex : secondaryIndex;
            if (!isPrimary && (index < 0 || index == primaryIndex))
            {
                // Noch kein eigener Ersatz-Pool: freien Platz nehmen und als Ersatz-Pool eintragen
                index = Enumerable.Range(0, 8).First(i => i != primaryIndex && !used.Contains(i));
                result["secondaryPoolIndex"] = index;
            }
            var entry = entries.FirstOrDefault(e => Int(e["id"]) == index)?.DeepClone() as JsonObject ?? new JsonObject();
            entry["id"] = index;
            if (result.Remove(keys[0], out var url)) entry["stratumURL"] = url.ToString();
            if (result.Remove(keys[1], out var port)) entry["stratumPort"] = Convert.ToInt32(port, System.Globalization.CultureInfo.InvariantCulture);
            if (result.Remove(keys[2], out var user)) entry["stratumUser"] = user.ToString();
            // ohne neues Passwort „*****“: die Firmware behält das gespeicherte
            entry["stratumPassword"] = result.Remove(keys[3], out var pw) && pw.ToString() is { Length: > 0 } p ? p : "*****";
            if (result.Remove(keys[4], out var tls)) entry["stratumTLS"] = Convert.ToInt32(tls, System.Globalization.CultureInfo.InvariantCulture);
            if (Str(entry["stratumURL"]).Length == 0 || Str(entry["stratumUser"]).Length == 0)
                throw new InvalidOperationException(I18n.L.T("Pool-Adresse und Benutzer dürfen nicht leer sein."));
            if (entry["stratumPort"] is null) entry["stratumPort"] = 3333;
            pools.Add(entry);
        }
        result["pools"] = pools;
        return result;
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static int Int(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : 0
        : 0;
}
