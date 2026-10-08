using System.Net.Sockets;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Core.Host;

/// <summary>Ein Miner bei „Pool umschalten“: geplante Änderung oder Grund, warum er übersprungen wird.</summary>
public sealed record PoolSwitchItem(HubDevice Device, PoolSwitchPlan? Plan, string? Skip);

/// <summary>
/// 0.9.11: Pool-Umschaltung. Manuell je Miner oder Gruppe (Vorschau alt → neu, Bestätigung) und als freigegebene Automatik:
/// Ersatz-Pool nach Zeitplan, zurück zum Haupt-Pool, sobald er wieder erreichbar ist, und Wechsel bei schlechten Shares.
/// Geschrieben wird nur die Pool-Auswahl; danach startet der Miner neu. Jede Umschaltung wird protokolliert.
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Zwischen zwei automatischen Pool-Umschaltungen eines Miners liegen mindestens so viele Minuten.</summary>
    public static readonly TimeSpan PoolMinGap = TimeSpan.FromMinutes(30);

    private sealed class PoolAutoState
    {
        public PoolLayout? Layout;
        public DateTime LayoutAt;
        public DateTime? OffHomeSince;
        public DateTime? BadSince;
        public DateTime LastSwitch = DateTime.MinValue;
        public bool ScheduledSwitch;
        public bool Busy;
    }

    private readonly Dictionary<string, PoolAutoState> _poolAuto = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ist ein Pool erreichbar? Standard: TCP-Verbindung zu url:port innerhalb von 5 s (in Tests ersetzbar).</summary>
    internal Func<string, int, Task<bool>> PoolReachable { get; set; } = ProbeTcpAsync;

    /// <summary>Meldet die Pool-Überwachung gerade schlechte Shares oder langsame Antworten? (in Tests ersetzbar)</summary>
    internal Func<HubDevice, bool>? PoolBadOverride { get; set; }

    private static async Task<bool> ProbeTcpAsync(string host, int port)
    {
        try
        {
            var url = host.Contains("://", StringComparison.Ordinal) ? new Uri(host).Host : host;
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(url, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Pool-Stand live vom Miner.</summary>
    public async Task<PoolLayout> PoolLayoutAsync(HubDevice device) =>
        PoolLayout.Parse(await device.Connection.GetRawInfoAsync());

    /// <summary>Vorschau für einen Miner; <paramref name="targetKey"/> ist <see cref="PoolSlot.Key"/>, „home“ oder „backup“.</summary>
    public async Task<PoolSwitchItem> PoolSwitchPreviewAsync(HubDevice device, string targetKey)
    {
        try
        {
            if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));
            var layout = await PoolLayoutAsync(device);
            var target = Target(device, layout, targetKey);
            if (target is null) return new PoolSwitchItem(device, null, L.T("kein passender Pool eingetragen"));
            if (target == layout.Active) return new PoolSwitchItem(device, null, L.T("{0} ist bereits aktiv.", target.Text));
            return new PoolSwitchItem(device, PoolSwitchPlan.Create(layout, target), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException or System.Text.Json.JsonException or HttpRequestException)
        {
            return new PoolSwitchItem(device, null, ex.Message);
        }
    }

    private static PoolSlot? Target(HubDevice device, PoolLayout layout, string key) => key switch
    {
        "home" => layout.Home(device.Config.HomePool),
        "backup" => layout.Backup(device.Config.HomePool),
        _ => layout.Find(key) ?? throw new InvalidOperationException(L.T("Der gewählte Pool ist auf dem Miner nicht (mehr) eingetragen – bitte neu laden.")),
    };

    /// <summary>
    /// Bestätigte Umschaltung: Plan neu berechnen (Stand kann sich geändert haben), vorher sichern, Pool-Auswahl schreiben,
    /// neu starten, protokollieren. <paramref name="makeHome"/>: Ziel künftig als Haupt-Pool merken.
    /// </summary>
    public async Task<PoolSwitchItem> SwitchPoolAsync(HubDevice device, string targetKey, string reason, bool makeHome = false, bool automatic = false, DateTime? at = null)
    {
        var item = await PoolSwitchPreviewAsync(device, targetKey);
        if (item.Plan is not { } plan)
        {
            // Bereits auf dem Ziel: nur „als Haupt-Pool merken“
            if (makeHome && targetKey is not ("home" or "backup") && PoolLayoutCached(device)?.Find(targetKey) is { } slot)
                RememberHome(device, slot);
            return item;
        }
        try
        {
            device.Applying = true;
            if (!automatic) await TryAutoBackupAsync(device, L.T("vor Pool-Umschaltung"));
            if (string.IsNullOrWhiteSpace(device.Config.HomePool) && (await PoolLayoutAsync(device)).Home(null) is { } oldHome)
                device.Config.HomePool = oldHome.Address;
            if (makeHome) device.Config.HomePool = plan.To.Address;
            Config.Save();
            if (plan.Patch.Count > 0) await device.Connection.PatchSettingsAsync(plan.Patch);
            await device.Connection.RestartAsync();

            var st = PoolState(device.Host);
            st.LastSwitch = at ?? DateTime.Now;
            st.OffHomeSince = plan.To.Address == device.Config.HomePool ? null : st.LastSwitch; // Wartezeit „zurück“ ab Umschaltung
            st.Layout = null;
            var text = plan.Text + " – " + reason;
            device.AddLog((automatic ? L.T("Pool-Automatik: {0}", text) : L.T("Pool umgeschaltet: {0}", text)) +
                          (makeHome ? L.T(" (als Haupt-Pool gemerkt)") : ""), automatic ? EventCategories.Automation : EventCategories.Settings);
            if (Config.Notifications.Wants(NotifyCategory.Pool))
                SendAlert(new Alert($"pool-switch:{device.Host}:{plan.To.Address}", L.T("{0}: Pool umgeschaltet", device.Title), text,
                    NotifyPriority.Normal, TimeSpan.FromMinutes(1), NotifyCategory.Pool, device.Host));
            return item;
        }
        catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException or HttpRequestException)
        {
            device.AddLog(L.T("Pool-Umschaltung fehlgeschlagen: {0}", ex.Message), automatic ? EventCategories.Automation : EventCategories.Settings);
            return item with { Plan = null, Skip = ex.Message };
        }
        finally
        {
            device.Applying = false;
        }
    }

    private void RememberHome(HubDevice device, PoolSlot slot)
    {
        if (device.Config.HomePool == slot.Address) return;
        device.Config.HomePool = slot.Address;
        Config.Save();
        device.AddLog(L.T("Haupt-Pool gemerkt: {0}", slot.Text), EventCategories.Settings);
    }

    private PoolLayout? PoolLayoutCached(HubDevice device) => PoolState(device.Host).Layout;

    private PoolAutoState PoolState(string host)
    {
        lock (_poolAuto)
        {
            if (!_poolAuto.TryGetValue(host, out var st)) _poolAuto[host] = st = new PoolAutoState();
            return st;
        }
    }

    // ---------- Gruppe ----------

    /// <summary>„Gruppe auf Ersatz-Pool“ bzw. „Gruppe zurück zum Haupt-Pool“: Vorschau je Miner.</summary>
    public async Task<List<PoolSwitchItem>> GroupPoolPreviewAsync(string group, string target)
    {
        if (target is not ("home" or "backup")) throw new InvalidOperationException(L.T("Ziel muss „home“ oder „backup“ sein."));
        var members = GroupMembers(group);
        if (members.Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", group);
        var list = new List<PoolSwitchItem>();
        foreach (var d in members) list.Add(await PoolSwitchPreviewAsync(d, target));
        return list;
    }

    public async Task<List<PoolSwitchItem>> SwitchGroupPoolAsync(string group, string target)
    {
        var members = GroupMembers(group);
        if (members.Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", group);
        var reason = target == "home" ? L.T("Gruppe „{0}“ zurück zum Haupt-Pool", group) : L.T("Gruppe „{0}“ auf Ersatz-Pool", group);
        var list = new List<PoolSwitchItem>();
        foreach (var d in members) list.Add(await SwitchPoolAsync(d, target, reason));
        LogEvent(null, EventCategories.Settings, L.T("{0}: {1} von {2} Minern umgeschaltet.", reason, list.Count(i => i.Plan is not null), list.Count));
        return list;
    }

    /// <summary>Text der Vorschau (Desktop und Browser gleich).</summary>
    public static string PoolPreviewText(IEnumerable<PoolSwitchItem> items) =>
        string.Join("\n", items.Select(i => i.Plan is { } p
            ? $"• {i.Device.Title}: {p.Text}" + (p.Warning is { } w ? $"\n  ⚠ {w}" : "")
            : L.T("• {0}: übersprungen – {1}", i.Device.Title, i.Skip ?? "")));

    // ---------- Automatik ----------

    public GroupPoolRule? GroupPoolRuleFor(string group) => Config.GroupPoolRules.FirstOrDefault(r => SameGroup(r.Group, group));

    private static string GroupPoolKey(string group, IEnumerable<HubDevice> members) =>
        "pool-" + GroupScheduleRule.ApprovalKey(group, members.Select(d => d.Host));

    public bool IsGroupPoolRuleApproved(GroupPoolRule rule) => rule.Rule.IsApproved(GroupPoolKey(rule.Group, GroupMembers(rule.Group)));

    /// <summary>Wirksame Regel: eigene des Miners (freigegeben), sonst die erste freigegebene seiner Gruppen.</summary>
    public (PoolSwitchRule Rule, string? Group)? EffectivePoolRule(HubDevice device)
    {
        if (device.Config.PoolAuto.Enabled)
            return device.Config.PoolAuto.IsApproved(device.Host) ? (device.Config.PoolAuto, null) : null;
        foreach (var r in Config.GroupPoolRules)
            if (r.Rule.Enabled && device.Config.Groups.Any(g => SameGroup(g, r.Group)) && IsGroupPoolRuleApproved(r))
                return (r.Rule, r.Group);
        return null;
    }

    private static void NormalizePoolRule(PoolSwitchRule rule)
    {
        rule.Entries ??= [];
        foreach (var e in rule.Entries)
        {
            e.FromHour = Math.Clamp(e.FromHour, 0, 23);
            e.ToHour = Math.Clamp(e.ToHour, 1, 24);
            e.Days &= 127;
            e.Preset = "";
        }
        rule.ReturnAfterMinutes = Math.Clamp(rule.ReturnAfterMinutes, 5, 24 * 60);
        rule.BadMinutes = Math.Clamp(rule.BadMinutes, 5, 24 * 60);
    }

    /// <summary>Regel eines Miners speichern; die Freigabe bleibt nur gültig, wenn sich der Inhalt nicht geändert hat.</summary>
    public void SavePoolRule(HubDevice device, PoolSwitchRule rule)
    {
        NormalizePoolRule(rule);
        rule.ApprovedSignature = device.Config.PoolAuto.ApprovedSignature;
        rule.ApprovedAt = device.Config.PoolAuto.ApprovedAt;
        device.Config.PoolAuto = rule;
        Config.Save();
        device.AddLog(L.T("Pool-Automatik gespeichert."), EventCategories.Automation);
    }

    public void ApprovePoolRule(HubDevice device)
    {
        if (!device.Config.PoolAuto.Enabled) throw new LocalizedException("Pool-Automatik ist nicht eingeschaltet.");
        device.Config.PoolAuto.Approve(device.Host);
        Config.Save();
        device.AddLog(L.T("Pool-Automatik freigegeben."), EventCategories.Automation);
    }

    public GroupPoolRule SaveGroupPoolRule(string group, PoolSwitchRule rule)
    {
        if (GroupMembers(group).Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", group);
        NormalizePoolRule(rule);
        var existing = GroupPoolRuleFor(group);
        if (existing is null)
        {
            existing = new GroupPoolRule { Group = group.Trim() };
            Config.GroupPoolRules.Add(existing);
        }
        rule.ApprovedSignature = existing.Rule.ApprovedSignature;
        rule.ApprovedAt = existing.Rule.ApprovedAt;
        existing.Rule = rule;
        Config.Save();
        LogEvent(null, EventCategories.Automation, L.T("Pool-Automatik der Gruppe „{0}“ gespeichert.", existing.Group));
        return existing;
    }

    public void ApproveGroupPoolRule(string group)
    {
        var rule = GroupPoolRuleFor(group) ?? throw new LocalizedException("Für „{0}“ ist noch keine Regel gespeichert.", group);
        if (!rule.Rule.Enabled) throw new LocalizedException("Pool-Automatik der Gruppe „{0}“ ist nicht eingeschaltet.", rule.Group);
        rule.Rule.Approve(GroupPoolKey(rule.Group, GroupMembers(rule.Group)));
        Config.Save();
        LogEvent(null, EventCategories.Automation, L.T("Pool-Automatik der Gruppe „{0}“ freigegeben ({1} Miner).", rule.Group, GroupMembers(rule.Group).Count));
    }

    /// <summary>Freigabetext: was die Regel tut (Desktop und Browser gleich).</summary>
    public static string PoolRuleText(PoolSwitchRule rule, string who)
    {
        var lines = new List<string>();
        foreach (var e in rule.Entries) lines.Add(L.T("• {0} {1:00}–{2:00} Uhr → Ersatz-Pool, danach zurück zum Haupt-Pool", e.DaysText, e.FromHour, e.ToHour));
        if (rule.OnBadShares) lines.Add(L.T("• Meldet die Pool-Überwachung {0} min lang zu viele abgelehnte Shares oder zu lange Antwortzeiten → Ersatz-Pool", rule.BadMinutes));
        if (rule.ReturnHome) lines.Add(L.T("• Läuft der Miner auf dem Ersatz-Pool und ist der Haupt-Pool wieder erreichbar → nach {0} min zurück zum Haupt-Pool", rule.ReturnAfterMinutes));
        if (lines.Count == 0) lines.Add(L.T("• (keine Bedingung gewählt – die Regel tut nichts)"));
        return L.T("Pool-Automatik für {0} freigeben?\n\n", who) + string.Join("\n", lines) + "\n\n" +
               L.T("Jede Umschaltung schreibt nur die Pool-Auswahl und startet den Miner neu (Frequenz und Spannung bleiben). ") +
               L.T("Zwischen zwei automatischen Umschaltungen liegen mindestens {0:0} min. Jede Umschaltung wird protokolliert.", PoolMinGap.TotalMinutes);
    }

    /// <summary>Nach jeder Abfragerunde: freigegebene Pool-Regeln auswerten (höchstens eine Umschaltung je Miner gleichzeitig).</summary>
    private void TickPoolAutomation(DateTime now)
    {
        foreach (var device in Devices)
        {
            if (EffectivePoolRule(device) is not { } eff) continue;
            var st = PoolState(device.Host);
            if (st.Busy || !device.State.Online || device.IsBenchmarkRunning || device.Applying || device.Connection.InMaintenance) continue;
            st.Busy = true;
            _ = RunPoolRuleAsync(device, eff.Rule, eff.Group, st, now); // wie ExecuteAutomationAsync: bleibt im Hub-Thread
        }
    }

    private async Task RunPoolRuleAsync(HubDevice device, PoolSwitchRule rule, string? group, PoolAutoState st, DateTime now)
    {
        try { await EvaluatePoolRuleAsync(device, rule, group, st, now); }
        catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or HttpRequestException or System.Text.Json.JsonException)
        { /* nächste Runde erneut */ }
        finally { st.Busy = false; }
    }

    internal async Task EvaluatePoolRuleAsync(HubDevice device, PoolSwitchRule rule, string? group, DateTime now)
    {
        var st = PoolState(device.Host);
        await EvaluatePoolRuleAsync(device, rule, group, st, now);
    }

    private async Task EvaluatePoolRuleAsync(HubDevice device, PoolSwitchRule rule, string? group, PoolAutoState st, DateTime now)
    {
        // Pool-Stand höchstens einmal pro Minute live lesen
        if (st.Layout is null || now - st.LayoutAt > TimeSpan.FromMinutes(1))
        {
            st.Layout = await PoolLayoutAsync(device);
            st.LayoutAt = now;
        }
        var layout = st.Layout;
        var home = layout.Home(device.Config.HomePool);
        var active = layout.Active;
        if (home is null || active is null || layout.Backup(device.Config.HomePool) is null) return;
        var onHome = active == home;
        st.OffHomeSince = onHome ? null : st.OffHomeSince ?? now;

        var bad = onHome && rule.OnBadShares && (PoolBadOverride?.Invoke(device) ?? PoolIsBad(device.Host));
        st.BadSince = bad ? st.BadSince ?? now : null;
        if (now - st.LastSwitch < PoolMinGap) return;

        var who = group is null ? "" : L.T(" (Gruppe „{0}“)", group);
        if (rule.BackupScheduled(now))
        {
            if (onHome && (await SwitchPoolAsync(device, "backup", L.T("Zeitplan") + who, automatic: true, at: now)).Plan is not null)
                st.ScheduledSwitch = true;
            return;
        }
        if (bad && st.BadSince is { } since && now - since >= TimeSpan.FromMinutes(rule.BadMinutes))
        {
            await SwitchPoolAsync(device, "backup", L.T("schlechte Shares/Antwortzeit seit {0} min", (int)(now - since).TotalMinutes) + who, automatic: true, at: now);
            st.BadSince = null;
            return;
        }
        // Zurück: nach einem Zeitplan-Fenster sofort, sonst erst nach der Wartezeit – jeweils nur, wenn der Haupt-Pool antwortet
        if (onHome) { st.ScheduledSwitch = false; return; }
        if (st.ScheduledSwitch && await PoolReachable(home.Url, home.Port))
        {
            await SwitchPoolAsync(device, "home", L.T("Zeitplan beendet") + who, automatic: true, at: now);
            st.ScheduledSwitch = false;
        }
        else if (rule.ReturnHome && st.OffHomeSince is { } off && now - off >= TimeSpan.FromMinutes(rule.ReturnAfterMinutes)
                 && await PoolReachable(home.Url, home.Port))
            await SwitchPoolAsync(device, "home", L.T("Haupt-Pool wieder erreichbar") + who, automatic: true, at: now);
    }

    private bool PoolIsBad(string host)
    {
        var s = Config.PoolWatch;
        return (PoolWatch.RejectRate(host, s) is { } r && r > s.RejectPercent) ||
               (s.ResponseMs > 0 && PoolWatch.AverageResponse(host) is { } ms && ms > s.ResponseMs);
    }
}
