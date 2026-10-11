using System.Text.Json.Nodes;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Core.Host;

/// <summary>Was ein Miner bekommen soll: Vorlage für Haupt- und Ersatz-Pool (null = unverändert lassen).</summary>
public sealed record PoolAssignment(string Host, string? PrimaryId, string? FallbackId);

/// <summary>Eine Änderung in der Vorschau, z. B. „Haupt-Pool: a:3333 → b:4444“.</summary>
public sealed record PoolAssignChange(string Label, string Old, string New);

/// <summary>Vorschau bzw. Ergebnis je Miner: Änderungen oder Grund, warum er übersprungen wird.</summary>
public sealed record PoolAssignItem(HubDevice Device, IReadOnlyList<PoolAssignChange> Changes, string? Skip)
{
    public string Text => Skip is { } s ? L.T("übersprungen – {0}", s)
        : string.Join(" · ", Changes.Select(c => $"{c.Label} {c.Old} → {c.New}"));
}

/// <summary>
/// 0.9.14 Pool-Steuerung: Pool-Vorlagen anlegen, bearbeiten und als Haupt- bzw. Ersatz-Pool auf Miner übertragen.
/// Vorschau alt → neu, vor dem Schreiben eine Sicherung, danach Neustart und Protokoll. Frequenz/Spannung bleiben unberührt.
/// ESP-Miner ab 2.15 bekommt die Pool-Liste (<see cref="PoolWrite"/>), ältere Firmware die Felder stratum*/fallbackStratum*.
/// </summary>
public sealed partial class MinerHub
{
    public PoolPreset? PoolPresetById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Config.PoolPresets.FirstOrDefault(p => p.Id == id);

    /// <summary>Vorlagen prüfen und speichern (ersetzt die Liste). Ungültige Einträge brechen mit einer Meldung ab.</summary>
    public void SavePoolPresets(IReadOnlyList<PoolPreset> presets, string by)
    {
        var list = new List<PoolPreset>();
        foreach (var src in presets)
        {
            var p = src.Clone();
            var url = (p.Url ?? "").Trim();
            // „stratum+tcp://host:port“ aus der Pool-Anleitung direkt übernehmen
            if (url.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase) && p.Tls == 0) p.Tls = 1;
            if (url.Contains("://", StringComparison.Ordinal)) url = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            url = url.TrimEnd('/');
            if (url.LastIndexOf(':') is var colon and > 0 && int.TryParse(url[(colon + 1)..], out var port)) { p.Port = port; url = url[..colon]; }
            p.Url = url;
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? url : p.Name.Trim();
            p.User = (p.User ?? "").Trim();
            p.Password = (p.Password ?? "").Trim();
            p.Notes = (p.Notes ?? "").Trim();
            if (!PoolPreset.WorkerModes.Contains(p.Worker)) p.Worker = "keep";
            p.Tls = Math.Clamp(p.Tls, 0, 2);
            if (string.IsNullOrWhiteSpace(p.Id) || list.Any(x => x.Id == p.Id)) p.Id = Guid.NewGuid().ToString("N")[..10];
            if (p.Url.Length == 0 || p.Url.Any(char.IsWhiteSpace)) throw new LocalizedException("Vorlage „{0}“: Bitte eine gültige Pool-Adresse eintragen.", p.Name);
            if (p.Port is < 1 or > 65535) throw new LocalizedException("Vorlage „{0}“: Port muss zwischen 1 und 65535 liegen.", p.Name);
            if (p.User.Length == 0) throw new LocalizedException("Vorlage „{0}“: Benutzer (Wallet-Adresse oder Konto) fehlt.", p.Name);
            list.Add(p);
        }
        Config.PoolPresets = list;
        Config.Save();
        LogEvent(null, EventCategories.Settings, L.T("Pool-Vorlagen gespeichert ({0}) – {1}", list.Count, by));
    }

    /// <summary>Vorschläge aus den Pools eines Miners (ohne Passwort – das liefert die Firmware nicht).</summary>
    public async Task<List<PoolPreset>> PoolPresetsFromDeviceAsync(HubDevice device)
    {
        var layout = await PoolLayoutAsync(device);
        return layout.Pools
            .OrderBy(p => p == layout.Primary ? 0 : p == layout.Secondary ? 1 : 2)
            .Select(p => new PoolPreset { Name = p.Url, Url = p.Url, Port = p.Port, User = p.User, Worker = "keep" })
            .ToList();
    }

    /// <summary>Vorschau für einen Miner.</summary>
    public async Task<PoolAssignItem> PoolAssignPreviewAsync(HubDevice device, PoolAssignment assignment) =>
        (await PlanAssignAsync(device, assignment)).Item;

    private async Task<(PoolAssignItem Item, Dictionary<string, object> Patch, string Raw)> PlanAssignAsync(HubDevice device, PoolAssignment a)
    {
        var none = new Dictionary<string, object>();
        try
        {
            if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));
            var primary = PoolPresetById(a.PrimaryId);
            var fallback = PoolPresetById(a.FallbackId);
            if (a.PrimaryId is { Length: > 0 } && primary is null || a.FallbackId is { Length: > 0 } && fallback is null)
                throw new InvalidOperationException(L.T("Die gewählte Vorlage gibt es nicht mehr – bitte neu laden."));
            if (primary is null && fallback is null) return (new PoolAssignItem(device, [], L.T("keine Vorlage gewählt")), none, "");
            if (primary is not null && primary == fallback)
                throw new InvalidOperationException(L.T("Haupt- und Ersatz-Pool dürfen nicht dieselbe Vorlage sein."));

            var raw = await device.Connection.GetRawInfoAsync();
            var layout = PoolLayout.Parse(raw);
            var root = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            var patch = new Dictionary<string, object>();
            var changes = new List<PoolAssignChange>();
            Plan(primary, layout.Primary, L.T("Haupt-Pool"), "stratum", isPrimary: true);
            Plan(fallback, layout.Secondary, L.T("Ersatz-Pool"), "fallbackStratum", isPrimary: false);
            return changes.Count == 0
                ? (new PoolAssignItem(device, [], L.T("bereits so eingestellt")), none, raw)
                : (new PoolAssignItem(device, changes, null), patch, raw);

            void Plan(PoolPreset? preset, PoolSlot? current, string label, string prefix, bool isPrimary)
            {
                if (preset is null) return;
                var user = preset.UserFor(device.Title, current?.User ?? "");
                var tls = CurrentTls(root, layout, current, prefix);
                var changed = false;
                if (current is null || !string.Equals(current.Url, preset.Url, StringComparison.OrdinalIgnoreCase) || current.Port != preset.Port)
                {
                    changes.Add(new PoolAssignChange(label, current?.Text ?? "–", preset.Text));
                    changed = true;
                }
                if (current?.User != user)
                {
                    changes.Add(new PoolAssignChange(L.T("{0}-Benutzer", label), Short(current?.User ?? "–"), Short(user)));
                    changed = true;
                }
                if (tls is { } t && t != preset.Tls)
                {
                    changes.Add(new PoolAssignChange(L.T("{0} TLS", label), TlsText(t), TlsText(preset.Tls)));
                    changed = true;
                }
                if (preset.Password.Length > 0 && changed) changes.Add(new PoolAssignChange(L.T("{0}-Passwort", label), "•••", L.T("aus der Vorlage")));
                if (!changed) return;
                patch[prefix + "URL"] = preset.Url;
                patch[prefix + "Port"] = preset.Port;
                patch[prefix + "User"] = user;
                if (preset.Password.Length > 0) patch[prefix + "Password"] = preset.Password;
                if (tls is not null) patch[prefix + "TLS"] = preset.Tls;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException or System.Text.Json.JsonException or HttpRequestException)
        {
            return (new PoolAssignItem(device, [], ex.Message), none, "");
        }
    }

    /// <summary>TLS-Stand des Pools (null = Firmware kennt kein TLS).</summary>
    private static int? CurrentTls(JsonObject root, PoolLayout layout, PoolSlot? slot, string prefix)
    {
        JsonNode? node = layout.Indexed
            ? (root["pools"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => slot is not null && (int?)e["id"] == slot.Index)?["stratumTLS"]
              ?? JsonValue.Create(0)
            : root[prefix + "TLS"];
        return node is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<bool>(out var b) ? (b ? 1 : 0) : 0 : null;
    }

    private static string TlsText(int tls) => tls switch { 0 => L.T("aus"), 1 => "TLS", _ => L.T("TLS (eigenes Zertifikat)") };

    private static string Short(string user) => user.Length > 28 ? user[..10] + "…" + user[^12..] : user;

    /// <summary>
    /// Bestätigte Übertragung: je Miner Vorschau neu rechnen, sichern, schreiben, Haupt-Pool merken, neu starten, protokollieren.
    /// </summary>
    public async Task<List<PoolAssignItem>> ApplyPoolAssignmentsAsync(IReadOnlyList<PoolAssignment> assignments, string by)
    {
        var results = new List<PoolAssignItem>();
        foreach (var a in assignments)
        {
            if (Device(a.Host) is not { } device) continue;
            var (item, patch, raw) = await PlanAssignAsync(device, a);
            if (item.Skip is not null || patch.Count == 0) { results.Add(item); continue; }
            try
            {
                device.Applying = true;
                await TryAutoBackupAsync(device, L.T("vor Pool-Übertragung"));
                await device.Connection.PatchSettingsAsync(PoolWrite.Adapt(raw, patch));
                if (PoolPresetById(a.PrimaryId) is { } primary) device.Config.HomePool = $"{primary.Url.Trim().ToLowerInvariant()}:{primary.Port}";
                Config.Save();
                await device.Connection.RestartAsync();
                PoolState(device.Host).Layout = null;
                device.AddLog(L.T("Pools übertragen ({0}): {1}", by, item.Text), EventCategories.Settings);
                results.Add(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException or HttpRequestException)
            {
                device.AddLog(L.T("Pool-Übertragung fehlgeschlagen: {0}", ex.Message), EventCategories.Settings);
                results.Add(item with { Changes = [], Skip = ex.Message });
            }
            finally
            {
                device.Applying = false;
            }
        }
        LogEvent(null, EventCategories.Settings, L.T("Pool-Steuerung: {0} von {1} Minern übertragen ({2}).",
            results.Count(r => r.Skip is null), results.Count, by));
        return results;
    }
}
