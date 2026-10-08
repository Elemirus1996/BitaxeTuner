using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Ein Miner bei „Gruppe jetzt umschalten“: aktuelle Werte, Ziel oder Grund, warum er übersprungen wird.</summary>
public sealed record GroupPresetItem(HubDevice Device, int? FrequencyMhz, int? CoreVoltageMv, TuningPreset? Target, string? Skip);

/// <summary>
/// Gruppen-Automatik (0.9.7): eine Zeitplan- oder Strompreis-Regel für alle Miner einer Gruppe; jeder Miner nutzt seine
/// eigene Voreinstellung gleichen Namens. Dazu „jetzt umschalten“ mit Bestätigung alt → neu je Miner.
/// </summary>
public sealed partial class MinerHub
{
    private static bool SameGroup(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Miner einer Gruppe (Reihenfolge wie in der Geräteliste).</summary>
    public List<HubDevice> GroupMembers(string group) =>
        Devices.Where(d => d.Config.Groups.Any(g => SameGroup(g, group))).ToList();

    /// <summary>Gespeicherte Regel der Gruppe oder null.</summary>
    public GroupScheduleRule? GroupRule(string group) => Config.GroupSchedules.FirstOrDefault(r => SameGroup(r.Group, group));

    /// <summary>
    /// Freigabe-Schlüssel: Gruppe, Mitglieder und – Audit N-S4 – je Mitglied die Werte der verwendeten Voreinstellungen
    /// und die Profilgrenzen. Ändert sich eine Voreinstellung (z. B. höhere Spannung) oder werden Grenzen angehoben, ist
    /// eine neue Freigabe nötig; vorher übersprungene Voreinstellungen werden so nicht stillschweigend gesetzt.
    /// </summary>
    private string GroupApprovalKey(GroupScheduleRule rule)
    {
        var names = PresetNames(rule.Schedule);
        var members = GroupMembers(rule.Group);
        var detail = string.Join(";", members.OrderBy(d => d.Host.Trim().ToLowerInvariant(), StringComparer.Ordinal).Select(d =>
            d.Host.Trim().ToLowerInvariant() + "=" + string.Join(",", names.Select(n =>
                d.Config.Presets.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)) is { } p
                    ? $"{n.ToLowerInvariant()}:{p.FrequencyMhz}/{p.CoreVoltageMv}" : $"{n.ToLowerInvariant()}:-"))
            + $"@{d.Profile.MaxFrequencyMhz}/{d.Profile.MaxVoltageMv}"));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(detail)))[..16];
        return GroupScheduleRule.ApprovalKey(rule.Group, members.Select(d => d.Host)) + "|v2:" + hash;
    }

    /// <summary>Namen der Voreinstellungen, die eine Regel verwendet.</summary>
    private static string[] PresetNames(PresetScheduleRule s) =>
        (s.Mode == "price" ? new[] { s.CheapPreset, s.ExpensivePreset } : s.Entries.Select(e => e.Preset).Append(s.DefaultPreset).ToArray())
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Freigegeben und seit der Freigabe weder Regel, Mitglieder, Voreinstellungen noch Grenzen geändert?</summary>
    public bool IsGroupScheduleApproved(GroupScheduleRule rule) => rule.Schedule.IsApproved(GroupApprovalKey(rule));

    /// <summary>Für die Automatik: erste freigegebene Gruppenregel eines Miners ohne eigene Regel.</summary>
    private (PresetScheduleRule Rule, string Group)? GroupScheduleFor(HubDevice device)
    {
        if (device.Config.Schedule.Enabled) return null;
        foreach (var rule in Config.GroupSchedules)
        {
            if (!rule.Schedule.Enabled || !device.Config.Groups.Any(g => SameGroup(g, rule.Group))) continue;
            if (IsGroupScheduleApproved(rule)) return (rule.Schedule, rule.Group);
        }
        return null;
    }

    /// <summary>Namen aller Voreinstellungen der Mitglieder (für die Auswahl), nach Häufigkeit.</summary>
    public List<string> GroupPresetNames(string group) =>
        GroupMembers(group).SelectMany(d => d.Config.Presets.Select(p => p.Name.Trim()))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.First()).ToList();

    /// <summary>Regel speichern; die Freigabe-Felder bleiben serverseitig (geänderte Regel = neue Freigabe nötig).</summary>
    public GroupScheduleRule SaveGroupSchedule(string group, PresetScheduleRule schedule)
    {
        if (GroupMembers(group).Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", group);
        if (schedule.Mode is not ("time" or "price")) schedule.Mode = "time";
        schedule.Entries ??= [];
        foreach (var e in schedule.Entries)
        {
            e.FromHour = Math.Clamp(e.FromHour, 0, 23);
            e.ToHour = Math.Clamp(e.ToHour, 1, 24);
            e.Days &= 127;
            e.Preset = (e.Preset ?? "").Trim();
        }
        schedule.DefaultPreset = (schedule.DefaultPreset ?? "").Trim();
        schedule.CheapPreset = (schedule.CheapPreset ?? "").Trim();
        schedule.ExpensivePreset = (schedule.ExpensivePreset ?? "").Trim();
        var rule = GroupRule(group);
        if (rule is null)
        {
            rule = new GroupScheduleRule { Group = group.Trim() };
            Config.GroupSchedules.Add(rule);
        }
        schedule.ApprovedSignature = rule.Schedule.ApprovedSignature;
        schedule.ApprovedAt = rule.Schedule.ApprovedAt;
        rule.Schedule = schedule;
        Config.Save();
        LogEvent(null, EventCategories.Automation, L.T("Gruppen-Automatik „{0}“ gespeichert.", rule.Group));
        return rule;
    }

    /// <summary>Freigabetext: Regel und für jeden Miner, was er setzen würde – oder warum er übersprungen wird.</summary>
    public string GroupScheduleApprovalText(string group)
    {
        var rule = GroupRule(group) ?? throw new LocalizedException("Für „{0}“ ist noch keine Regel gespeichert.", group);
        var s = rule.Schedule;
        var names = PresetNames(s);
        var body = s.Mode == "price"
            ? L.T("Strompreis ({0}) ≤ {1:0.##} ct/kWh → „{2}“, sonst → „{3}“", Prices.SourceName, s.ThresholdCt, s.CheapPreset, s.ExpensivePreset).Cents(Config)
            : string.Join("\n", s.Entries.Select(e => L.T("{0} {1:00}–{2:00} Uhr → „{3}“", e.DaysText, e.FromHour, e.ToHour, e.Preset))) +
              L.T("\nsonst → {0}", s.DefaultPreset.Length == 0 ? L.T("keine Änderung") : $"„{s.DefaultPreset}“");
        var lines = GroupMembers(group).Select(d =>
        {
            if (d.Config.Schedule.Enabled) return L.T("• {0}: eigene Regel hat Vorrang – Gruppenregel gilt hier nicht", d.Title);
            var parts = names.Select(n =>
            {
                var p = d.Config.Presets.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (p is null) return L.T("„{0}“ fehlt → übersprungen", n);
                return CheckPreset(d, p) is null ? $"{p.Name} = {p.FrequencyMhz} MHz / {p.CoreVoltageMv} mV" : L.T("„{0}“ außerhalb der Grenzen → übersprungen", n);
            });
            return $"• {d.Title}: " + string.Join(" · ", parts);
        });
        return L.T("Gruppen-Automatik für „{0}“ freigeben?\n\n{1}\n\n", rule.Group, body) +
               L.T("Jeder Miner nutzt seine eigene Voreinstellung gleichen Namens:\n") + string.Join("\n", lines) + "\n\n" +
               L.T("Zwischen zwei automatischen Änderungen liegen mindestens {0:0} min. ", AutomationEngine.MinGap.TotalMinutes) +
               GuardText(GroupMembers(group)) +
               L.T("Kommt ein Miner in die Gruppe oder fällt einer weg, ändert sich eine Voreinstellung oder eine Profilgrenze, ist eine neue Freigabe nötig. Jede Änderung wird protokolliert.");
    }

    /// <summary>Audit N-S4: „Temperaturschutz bleibt aktiv“ nur, wenn er bei allen Mitgliedern eingeschaltet und freigegeben ist.</summary>
    private static string GuardText(IReadOnlyList<HubDevice> members)
    {
        var without = members.Where(d => !(d.Config.ThermalGuard.Enabled && d.Config.ThermalGuard.IsApproved(d.Host))).Select(d => d.Title).ToList();
        return without.Count == 0
            ? L.T("Der Temperaturschutz je Miner bleibt aktiv. ")
            : L.T("Ohne freigegebenen Temperaturschutz: {0}. ", string.Join(", ", without));
    }

    public void ApproveGroupSchedule(string group)
    {
        var rule = GroupRule(group) ?? throw new LocalizedException("Für „{0}“ ist noch keine Regel gespeichert.", group);
        if (!rule.Schedule.Enabled) throw new LocalizedException("Gruppen-Automatik „{0}“ ist nicht eingeschaltet.", rule.Group);
        rule.Schedule.Approve(GroupApprovalKey(rule));
        Config.Save();
        LogEvent(null, EventCategories.Automation, L.T("Gruppen-Automatik „{0}“ freigegeben ({1} Miner).", rule.Group, GroupMembers(rule.Group).Count));
        foreach (var d in GroupMembers(rule.Group)) d.AddLog(L.T("Gruppen-Automatik „{0}“ freigegeben.", rule.Group), EventCategories.Automation);
    }

    // ---------- Jetzt umschalten ----------

    /// <summary>Was „Gruppe jetzt auf Voreinstellung X“ je Miner bedeuten würde (aktuelle Werte live vom Miner).</summary>
    public async Task<List<GroupPresetItem>> PreviewGroupPresetAsync(string group, string presetName)
    {
        var name = (presetName ?? "").Trim();
        if (name.Length == 0) throw new LocalizedException("Bitte eine Voreinstellung wählen.");
        var members = GroupMembers(group);
        if (members.Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", group);
        var items = new List<GroupPresetItem>();
        foreach (var d in members)
        {
            var preset = d.Config.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            Api.MinerInfo? info = null;
            try { info = await d.Connection.GetInfoAsync(); } catch (Api.MinerApiException) { }
            string? skip = preset is null ? L.T("keine Voreinstellung „{0}“", name)
                : CheckPreset(d, preset) is not null ? L.T("außerhalb der Grenzen für {0}", d.Profile.Name)
                : d.IsBenchmarkRunning ? L.T("Benchmark läuft")
                : info is null ? L.T("nicht erreichbar")
                : info.FrequencyMhz == preset.FrequencyMhz && info.CoreVoltageMv == preset.CoreVoltageMv ? L.T("bereits eingestellt")
                : null;
            items.Add(new GroupPresetItem(d, info?.FrequencyMhz, info?.CoreVoltageMv, preset, skip));
        }
        return items;
    }

    /// <summary>Bestätigte Umschaltung: je Miner wie eine manuelle Änderung (Sicherung, setzen, protokollieren).</summary>
    public async Task<List<(HubDevice Device, string Result)>> ApplyGroupPresetAsync(string group, string presetName)
    {
        var results = new List<(HubDevice, string)>();
        foreach (var item in await PreviewGroupPresetAsync(group, presetName))
        {
            if (item.Skip is { } skip || item.Target is not { } p)
            {
                results.Add((item.Device, L.T("übersprungen: {0}", item.Skip ?? "?")));
                continue;
            }
            try
            {
                await ApplyChangeAsync(item.Device, p.FrequencyMhz, p.CoreVoltageMv);
                item.Device.AddLog(L.T("Gruppe „{0}“: Voreinstellung „{1}“ gesetzt ({2}→{3} MHz / {4}→{5} mV).", group, p.Name,
                    item.FrequencyMhz?.ToString() ?? "?", p.FrequencyMhz, item.CoreVoltageMv?.ToString() ?? "?", p.CoreVoltageMv), EventCategories.Automation);
                results.Add((item.Device, L.T("gesetzt: {0} MHz / {1} mV", p.FrequencyMhz, p.CoreVoltageMv)));
            }
            catch (Exception ex)
            {
                results.Add((item.Device, L.T("fehlgeschlagen: {0}", ex.Message)));
            }
        }
        LogEvent(null, EventCategories.Automation, L.T("Gruppe „{0}“ auf „{1}“ umgeschaltet: {2} von {3} Minern.", group, presetName.Trim(),
            results.Count(r => !r.Item2.StartsWith(L.T("übersprungen"), StringComparison.Ordinal) && !r.Item2.StartsWith(L.T("fehlgeschlagen"), StringComparison.Ordinal)), results.Count));
        return results;
    }
}
