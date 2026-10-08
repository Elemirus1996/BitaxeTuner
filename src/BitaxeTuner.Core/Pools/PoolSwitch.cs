using System.Globalization;
using System.Text.Json.Nodes;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Pools;

/// <summary>Ein Pool-Eintrag des Miners (Passwörter liest die Firmware nie aus).</summary>
public sealed record PoolSlot(int Index, string Url, int Port, string User)
{
    /// <summary>„url:port“ in Kleinbuchstaben – so merkt sich BitaxeTuner den Haupt-Pool (<see cref="Config.DeviceConfig.HomePool"/>).</summary>
    public string Address => $"{Url.Trim().ToLowerInvariant()}:{Port}";
    public string Text => $"{Url}:{Port}";
    /// <summary>Eindeutige Auswahl für Browser/Desktop: Index plus Adresse (schützt vor zwischenzeitlich geänderter Liste).</summary>
    public string Key => $"{Index}|{Address}";
}

/// <summary>
/// Pool-Stand eines Miners aus der Roh-Antwort von /api/system/info. Zwei Arten:
/// <list type="bullet">
/// <item>ESP-Miner ab 2.15: Liste <c>pools</c> mit <c>primaryPoolIndex</c>/<c>secondaryPoolIndex</c> – Umschalten tauscht nur die
/// Indizes, die gespeicherten Passwörter bleiben an ihren Einträgen.</item>
/// <item>Ältere Firmware/NerdQAxe: Felder <c>stratum*</c> und <c>fallbackStratum*</c> – Umschalten tauscht URL, Port und Benutzer;
/// die Passwörter kann die Firmware nicht auslesen, sie bleiben am Platz.</item>
/// </list>
/// </summary>
public sealed class PoolLayout
{
    public bool Indexed { get; init; }
    public List<PoolSlot> Pools { get; init; } = [];
    public int PrimaryIndex { get; init; }
    public int SecondaryIndex { get; init; } = -1;
    /// <summary>Firmware meldet: Haupt-Pool nicht erreichbar, läuft auf dem Ersatz-Pool.</summary>
    public bool UsingFallback { get; init; }

    public PoolSlot? Primary => Pools.FirstOrDefault(p => p.Index == PrimaryIndex);
    public PoolSlot? Secondary => Pools.FirstOrDefault(p => p.Index == SecondaryIndex && p.Index != PrimaryIndex);
    /// <summary>Pool, auf dem der Miner gerade arbeitet.</summary>
    public PoolSlot? Active => UsingFallback && Secondary is { } s ? s : Primary;

    public PoolSlot? Find(string key) => Pools.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Haupt-Pool im Sinne von BitaxeTuner: gemerkter Heimat-Pool, sonst der aktuelle Haupt-Pool des Miners.</summary>
    public PoolSlot? Home(string? homePool) =>
        string.IsNullOrWhiteSpace(homePool) ? Primary
            : Pools.FirstOrDefault(p => p.Address == homePool.Trim().ToLowerInvariant()) ?? Primary;

    /// <summary>Anderer Pool als der Heimat-Pool: bevorzugt der eingestellte Ersatz-Pool.</summary>
    public PoolSlot? Backup(string? homePool)
    {
        var home = Home(homePool);
        if (Secondary is { } s && s != home) return s;
        if (Primary is { } p && p != home) return p;
        return Pools.FirstOrDefault(x => x != home);
    }

    public static PoolLayout Parse(string rawInfo)
    {
        var root = JsonNode.Parse(rawInfo) as JsonObject ?? throw new InvalidOperationException(L.T("Antwort des Miners ist kein JSON-Objekt."));
        var usingFallback = Int(root["isUsingFallbackStratum"]) != 0;
        if (root["pools"] is JsonArray arr && root["primaryPoolIndex"] is not null)
        {
            var pools = new List<PoolSlot>();
            foreach (var node in arr.OfType<JsonObject>())
            {
                var url = Str(node["stratumURL"]);
                if (url.Length == 0) continue;
                pools.Add(new PoolSlot(Int(node["id"]), url, Int(node["stratumPort"]), Str(node["stratumUser"])));
            }
            return new PoolLayout
            {
                Indexed = true, Pools = pools, UsingFallback = usingFallback,
                PrimaryIndex = Int(root["primaryPoolIndex"]), SecondaryIndex = Int(root["secondaryPoolIndex"]),
            };
        }

        var legacy = new List<PoolSlot>();
        if (Str(root["stratumURL"]) is { Length: > 0 } u) legacy.Add(new PoolSlot(0, u, Int(root["stratumPort"]), Str(root["stratumUser"])));
        if (Str(root["fallbackStratumURL"]) is { Length: > 0 } f) legacy.Add(new PoolSlot(1, f, Int(root["fallbackStratumPort"]), Str(root["fallbackStratumUser"])));
        return new PoolLayout { Indexed = false, Pools = legacy, PrimaryIndex = 0, SecondaryIndex = legacy.Count > 1 ? 1 : -1, UsingFallback = usingFallback };
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static int Int(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }
}

/// <summary>Was eine Umschaltung schreibt. <see cref="Patch"/> leer = nur Neustart (Miner kehrt selbst zum Haupt-Pool zurück).</summary>
public sealed record PoolSwitchPlan(PoolSlot From, PoolSlot To, IReadOnlyDictionary<string, object> Patch, string? Warning)
{
    /// <summary>Bestätigungstext alt → neu (Desktop und Browser gleich).</summary>
    public string Text => L.T("Pool {0} → {1}", From.Text, To.Text) +
        (From.User != To.User ? L.T(" · Benutzer {0} → {1}", Short(From.User), Short(To.User)) : "") +
        (Patch.Count == 0 ? L.T(" (nur Neustart: der Miner verbindet sich wieder mit seinem Haupt-Pool)") : "");

    private static string Short(string user) => user.Length > 24 ? user[..10] + "…" + user[^10..] : user;

    public static PoolSwitchPlan Create(PoolLayout layout, PoolSlot target)
    {
        var active = layout.Active ?? throw new InvalidOperationException(L.T("Der Miner meldet keinen Pool."));
        if (target == active) throw new InvalidOperationException(L.T("{0} ist bereits aktiv.", target.Text));
        var primary = layout.Primary!;

        // Läuft auf dem Ersatz-Pool und soll zurück zum eingestellten Haupt-Pool: ein Neustart genügt
        if (target == primary && layout.UsingFallback) return new PoolSwitchPlan(active, target, new Dictionary<string, object>(), null);

        if (layout.Indexed)
        {
            // Ziel wird Haupt-Pool, der bisherige Haupt-Pool wird Ersatz-Pool
            var patch = new Dictionary<string, object> { ["primaryPoolIndex"] = target.Index, ["secondaryPoolIndex"] = primary.Index };
            return new PoolSwitchPlan(active, target, patch, null);
        }

        if (target.Index != 1 || layout.Pools.Count < 2)
            throw new InvalidOperationException(L.T("Diese Firmware kennt nur Haupt- und Ersatz-Pool."));
        var legacy = new Dictionary<string, object>
        {
            ["stratumURL"] = target.Url, ["stratumPort"] = target.Port, ["stratumUser"] = target.User,
            ["fallbackStratumURL"] = primary.Url, ["fallbackStratumPort"] = primary.Port, ["fallbackStratumUser"] = primary.User,
        };
        return new PoolSwitchPlan(active, target, legacy,
            L.T("Diese Firmware tauscht Adresse und Benutzer; die Pool-Passwörter bleiben an ihrem Platz (meist „x“ – bei Pools mit eigenem Passwort vorher prüfen)."));
    }
}
