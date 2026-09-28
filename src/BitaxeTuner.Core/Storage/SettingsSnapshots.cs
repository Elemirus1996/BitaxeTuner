using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Storage;

/// <summary>Gesicherte Einstellungen eines Miners (vollständige /api/system/info-Antwort).</summary>
public sealed class SettingsSnapshot
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime TakenAt { get; set; }
    /// <summary>Anlass, z. B. "manuell", "vor Benchmark", "vor manueller Änderung".</summary>
    public string Reason { get; set; } = "";
    public string? FirmwareVersion { get; set; }
    public JsonObject Info { get; set; } = new();

    public string? FilePath { get; set; }

    public string DisplayText =>
        $"{TakenAt.ToString("g", L.Culture)} · {(Reason == "manuell" ? L.T("manuell") : L.T(Reason))} · {Value("frequency")} MHz / {Value("coreVoltage")} mV";

    public string Value(string field) => SettingsSnapshots.Format(Info[field]);
}

public enum SettingGroup
{
    Tuning,
    Fan,
    Pool,
}

/// <summary>Ein Feld, das sich zwischen Sicherung und aktuellem Stand unterscheidet.</summary>
public sealed record SettingChange(SettingGroup Group, string Field, string Label, string Current, string Saved, JsonNode? Value)
{
    public string GroupText => Group switch
    {
        SettingGroup.Tuning => "Tuning",
        SettingGroup.Fan => L.T("Lüfter/Temperatur"),
        _ => "Pool",
    };
}

/// <summary>
/// Speichert und vergleicht Einstellungs-Sicherungen unter &lt;Datenordner&gt;\snapshots\.
/// Wiederhergestellt werden nur Felder, die laut ESP-Miner v2.15.3 per PATCH /api/system änderbar sind
/// UND im aktuellen Gerät vorkommen (andere Firmware, z. B. NerdQAxe, hat teils andere Felder).
/// Pool-Passwörter liefert AxeOS nicht aus – sie werden weder gesichert noch überschrieben.
/// </summary>
public sealed class SettingsSnapshots(string directory)
{
    private const int MaxAutoPerHost = 30;

    /// <summary>Feld, Anzeigename, Gruppe. Frequenz/Spannung werden gesondert (protokolliert) gesetzt.</summary>
    public static (string Field, string Label, SettingGroup Group)[] Restorable =>
    [
        ("frequency", L.T("Frequenz (MHz)"), SettingGroup.Tuning),
        ("coreVoltage", L.T("Kernspannung (mV)"), SettingGroup.Tuning),
        ("overclockEnabled", L.T("Overclocking-Modus"), SettingGroup.Tuning),
        ("autofanspeed", L.T("Lüfter automatisch"), SettingGroup.Fan),
        ("manualFanSpeed", L.T("Lüfter manuell (%)"), SettingGroup.Fan),
        ("temptarget", L.T("Zieltemperatur (°C)"), SettingGroup.Fan),
        ("stratumURL", L.T("Pool-URL"), SettingGroup.Pool),
        ("stratumPort", L.T("Pool-Port"), SettingGroup.Pool),
        ("stratumUser", L.T("Pool-Benutzer (Wallet.Worker)"), SettingGroup.Pool),
        ("fallbackStratumURL", L.T("Fallback-Pool-URL"), SettingGroup.Pool),
        ("fallbackStratumPort", L.T("Fallback-Pool-Port"), SettingGroup.Pool),
        ("fallbackStratumUser", L.T("Fallback-Pool-Benutzer"), SettingGroup.Pool),
    ];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Directory => directory;

    public SettingsSnapshot Save(string host, string name, string rawInfo, string reason, DateTime? now = null)
    {
        var info = JsonNode.Parse(rawInfo) as JsonObject ?? throw new JsonException(L.T("Antwort ist kein JSON-Objekt"));
        var snapshot = new SettingsSnapshot
        {
            Host = host,
            Name = name,
            TakenAt = now ?? DateTime.Now,
            Reason = reason,
            FirmwareVersion = Format(info["version"]),
            Info = info,
        };

        System.IO.Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{Safe(host)}_{snapshot.TakenAt:yyyyMMdd-HHmmss}.json");
        for (var i = 2; File.Exists(file); i++)
            file = Path.Combine(directory, $"{Safe(host)}_{snapshot.TakenAt:yyyyMMdd-HHmmss}-{i}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, Options));
        snapshot.FilePath = file;

        if (reason != "manuell") PruneAuto(host);
        return snapshot;
    }

    public List<SettingsSnapshot> List(string host)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        var list = new List<SettingsSnapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, Safe(host) + "_*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SettingsSnapshot>(File.ReadAllText(file));
                if (s is null || !string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                s.FilePath = file;
                list.Add(s);
            }
            catch (JsonException) { /* defekte Datei überspringen */ }
        }
        return list.OrderByDescending(s => s.TakenAt).ToList();
    }

    /// <summary>Unterschiede zwischen aktuellem Stand und Sicherung (nur wiederherstellbare, im Gerät vorhandene Felder).</summary>
    public static List<SettingChange> Diff(SettingsSnapshot snapshot, string currentRawInfo)
    {
        var current = JsonNode.Parse(currentRawInfo) as JsonObject ?? new JsonObject();
        var changes = new List<SettingChange>();
        foreach (var (field, label, group) in Restorable)
        {
            if (!snapshot.Info.TryGetPropertyValue(field, out var saved) || saved is null) continue;
            if (!current.TryGetPropertyValue(field, out var now)) continue; // Feld gibt es auf dem Gerät nicht (mehr)
            var savedText = Format(saved);
            var nowText = Format(now);
            if (savedText == nowText) continue;
            changes.Add(new SettingChange(group, field, label, nowText, savedText, saved.DeepClone()));
        }
        return changes;
    }

    /// <summary>
    /// Einstellungen eines Miners auf einen anderen übertragen: nur Pool und Lüfter (nie Frequenz/Spannung), nur Felder,
    /// die beide Geräte kennen. Beim Pool-Benutzer wird nur das Wallet übernommen, der Worker-Name des Ziels bleibt.
    /// </summary>
    public static List<SettingChange> CopyDiff(string sourceRawInfo, string targetRawInfo, IReadOnlySet<SettingGroup> groups)
    {
        var source = JsonNode.Parse(sourceRawInfo) as JsonObject ?? new JsonObject();
        var target = JsonNode.Parse(targetRawInfo) as JsonObject ?? new JsonObject();
        var changes = new List<SettingChange>();
        foreach (var (field, label, group) in Restorable)
        {
            if (group == SettingGroup.Tuning || !groups.Contains(group)) continue;
            if (!source.TryGetPropertyValue(field, out var value) || value is null) continue;
            if (!target.TryGetPropertyValue(field, out var now)) continue;
            if (field is "stratumUser" or "fallbackStratumUser")
                value = JsonValue.Create(MergeWorker(Format(value), Format(now)));
            var newText = Format(value);
            var nowText = Format(now);
            if (newText == nowText) continue;
            changes.Add(new SettingChange(group, field, label, nowText, newText, value!.DeepClone()));
        }
        return changes;
    }

    /// <summary>„bc1q….quelle“ + „bc1x….ziel“ → „bc1q….ziel“ (Wallet der Quelle, Worker des Ziels).</summary>
    public static string MergeWorker(string sourceUser, string targetUser)
    {
        var wallet = sourceUser.Split('.', 2)[0];
        var worker = targetUser.Split('.', 2) is [_, var w] ? w : null;
        return worker is { Length: > 0 } ? $"{wallet}.{worker}" : wallet;
    }

    /// <summary>JSON-Wert in den PATCH-Typ (Zahl → int, sonst Text).</summary>
    public static object ToPatchValue(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => (int)Math.Round(d),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value?.ToJsonString() ?? "",
    };

    public static string Format(JsonNode? value) => value switch
    {
        null => "–",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.##", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    private void PruneAuto(string host)
    {
        var auto = List(host).Where(s => s.Reason != "manuell").Skip(MaxAutoPerHost);
        foreach (var s in auto)
        {
            try { if (s.FilePath is not null) File.Delete(s.FilePath); } catch { /* nicht kritisch */ }
        }
    }

    private static string Safe(string host) =>
        new(host.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c).ToArray());
}
