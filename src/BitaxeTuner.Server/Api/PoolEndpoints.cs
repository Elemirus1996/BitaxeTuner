using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// 0.9.11 Pool-Umschaltung (nur Admin): je Miner und je Gruppe mit Vorschau alt → neu, dazu die Pool-Automatik mit Freigabe.
/// </summary>
public static class PoolEndpoints
{
    public sealed record PoolRequest(string? Target, bool MakeHome, PoolSwitchRule? Rule, string? Group);

    /// <summary>0.9.14: Vorlage aus dem Browser. Password null = unverändert, ClearPassword = löschen.</summary>
    public sealed record PoolPresetDto(string? Id, string? Name, string? Url, int Port, string? User, string? Worker, int Tls, string? Notes,
        string? Password, bool ClearPassword, bool? DecodeCoinbase = null);

    public sealed record PoolPresetsRequest(List<PoolPresetDto>? Presets);

    public sealed record PoolAssignDto(string Id, string? Primary, string? Fallback);

    public sealed record PoolAssignRequest(List<PoolAssignDto>? Items);

    private static object PresetJson(PoolPreset p) => new
    {
        p.Id, p.Name, p.Url, p.Port, p.User, p.Worker, p.Tls, p.Notes, p.DecodeCoinbase, hasPassword = p.Password.Length > 0,
    };

    private static List<PoolAssignment> Assignments(MinerHub h, PoolAssignRequest req) =>
        (req.Items ?? []).Select(i => new PoolAssignment(Device(h, i.Id).Host, i.Primary, i.Fallback)).ToList();

    private static object AssignItems(IEnumerable<PoolAssignItem> items) => items.Select(i => new
    {
        id = Dto.DeviceId(i.Device.Host), name = i.Device.Title, skip = i.Skip,
        changes = i.Changes.Select(c => new { label = c.Label, old = c.Old, @new = c.New }).ToList(),
    }).ToList();

    private static HubDevice Device(MinerHub h, string id) =>
        Dto.Find(h, id) ?? throw new KeyNotFoundException(L.N("Gerät nicht gefunden."));

    private static string GroupOf(PoolRequest req, MinerHub h)
    {
        var g = (req.Group ?? "").Trim();
        if (g.Length == 0 || h.GroupMembers(g).Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", g);
        return g;
    }

    private static object Items(IEnumerable<PoolSwitchItem> items) => items.Select(i => new
    {
        id = Dto.DeviceId(i.Device.Host), name = i.Device.Title, text = i.Plan?.Text, warning = i.Plan?.Warning, skip = i.Skip,
    }).ToList();

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/devices/{id}/pools", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var layout = await h.PoolLayoutAsync(d);
            var home = layout.Home(d.Config.HomePool);
            var eff = h.EffectivePoolRule(d);
            return new
            {
                indexed = layout.Indexed,
                usingFallback = layout.UsingFallback,
                pools = layout.Pools.Select(p => new
                {
                    key = p.Key, text = p.Text, user = p.User,
                    active = p == layout.Active, primary = p == layout.Primary, secondary = p == layout.Secondary, home = p == home,
                }).ToList(),
                rule = d.Config.PoolAuto,
                approved = d.Config.PoolAuto.IsApproved(d.Host),
                activeGroupRule = eff is { Group: { } grp } ? grp : null,
            };
        })));

        g.MapPost("/devices/{id}/pools/preview", async (string id, PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var item = await h.PoolSwitchPreviewAsync(Device(h, id), req.Target ?? "");
            return new { text = item.Plan?.Text, warning = item.Plan?.Warning, skip = item.Skip };
        })));

        g.MapPost("/devices/{id}/pools/switch", async (string id, PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var item = await h.SwitchPoolAsync(Device(h, id), req.Target ?? "", L.T("Browser"), req.MakeHome);
            if (item.Plan is null && !req.MakeHome) throw new InvalidOperationException(item.Skip ?? L.T("Nicht umgeschaltet."));
            return new { ok = true, text = item.Plan?.Text };
        })));

        g.MapPut("/devices/{id}/pools/rule", async (string id, PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            h.SavePoolRule(d, req.Rule ?? throw new LocalizedException("Regel fehlt."));
            return new { ok = true, approved = d.Config.PoolAuto.IsApproved(d.Host) };
        })));

        g.MapPost("/devices/{id}/pools/rule/approval-text", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            return new { text = MinerHub.PoolRuleText(d.Config.PoolAuto, d.Title) };
        })));

        g.MapPost("/devices/{id}/pools/rule/approve", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.ApprovePoolRule(Device(h, id));
            return new { ok = true };
        })));

        // ---------- 0.9.15 Pool-Vergleich ----------

        g.MapGet("/pool-comparison", async (string? range, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = h.PoolComparisonFor(range, DateTime.Now);
            return new
            {
                range = r.Range, currency = r.Currency, incomeShared = r.IncomeShared,
                rows = r.Rows.Select(x => new
                {
                    pool = x.Pool, miners = x.Miners, hours = x.Hours, rejectPercent = x.RejectPercent, responseMs = x.ResponseMs,
                    outageHours = x.OutageHours, availabilityPercent = x.AvailabilityPercent, fallbackHours = x.FallbackHours,
                    avgHashrateGh = x.AvgHashrateGh, incomePerThDay = x.IncomePerThDay,
                }).ToList(),
            };
        })));

        // ---------- 0.9.15 Mining-Art-Vergleich ----------

        g.MapGet("/mining-modes", async (double? pps, double? pplns, double? solo, double? btcFees, double? bchFees, HubService hub) =>
        {
            var d = new MiningModeAssumptions();
            var a = new MiningModeAssumptions(Math.Clamp(pps ?? d.PpsFeePercent, 0, 50), Math.Clamp(pplns ?? d.PplnsFeePercent, 0, 50),
                Math.Clamp(solo ?? d.SoloFeePercent, 0, 50), Math.Clamp(btcFees ?? d.BtcFeesPerBlock, 0, 10), Math.Clamp(bchFees ?? d.BchFeesPerBlock, 0, 10));
            return Results.Json(await hub.RunAsync(async h =>
            {
                var r = await h.MiningModesAsync(a, DateTime.Now);
                return new
                {
                    hashrateGh = r.HashrateGh, miners = r.Miners, currency = r.Currency, costPerDay = r.CostPerDay, powerW = r.PowerW,
                    btcDifficulty = r.BtcDifficulty, bchDifficulty = r.BchDifficulty, btcPrice = r.BtcPrice, bchPrice = r.BchPrice,
                    assumptions = new { pps = a.PpsFeePercent, pplns = a.PplnsFeePercent, solo = a.SoloFeePercent, btcFees = a.BtcFeesPerBlock, bchFees = a.BchFeesPerBlock },
                    rows = r.Rows.Select(x => new
                    {
                        key = x.Key, coin = x.Coin, coinPerDay = x.CoinPerDay, incomePerDay = x.IncomePerDay, costPerDay = x.CostPerDay,
                        netPerDay = x.NetPerDay, chancePerMonth = x.ChancePerMonth, expectedYears = x.ExpectedYears,
                    }).ToList(),
                };
            }));
        });

        // ---------- 0.9.14 Pool-Steuerung: Vorlagen ----------

        g.MapGet("/pool-presets", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            presets = h.Config.PoolPresets.Select(PresetJson).ToList(),
            devices = h.Devices.Select(d => new
            {
                id = Dto.DeviceId(d.Host), name = d.Title, online = d.State.Online, homePool = d.Config.HomePool,
                // 0.9.16: gemerkte Vorlagen und Abweichung davon
                assigned = new { primary = d.Config.PoolPresetPrimary, fallback = d.Config.PoolPresetFallback },
                deviation = h.PoolDeviationOf(d)?.Differences,
            }).ToList(),
        })));

        // 0.9.16: Abweichung ist gewollt – Vorlagen dieses Miners nicht mehr überwachen
        g.MapPost("/pool-presets/forget/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = h.Devices.FirstOrDefault(x => Dto.DeviceId(x.Host) == id) ?? throw new KeyNotFoundException(L.T("Miner nicht gefunden."));
            h.ForgetPoolPresets(d, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPut("/pool-presets", async (PoolPresetsRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var list = (req.Presets ?? []).Select(p =>
            {
                var old = h.PoolPresetById(p.Id);
                return new PoolPreset
                {
                    Id = p.Id ?? "", Name = p.Name ?? "", Url = p.Url ?? "", Port = p.Port, User = p.User ?? "",
                    Worker = p.Worker ?? "keep", Tls = p.Tls, Notes = p.Notes ?? "", DecodeCoinbase = p.DecodeCoinbase,
                    Password = p.ClearPassword ? "" : p.Password ?? old?.Password ?? "",
                };
            }).ToList();
            h.SavePoolPresets(list, L.T("Browser"));
            return new { ok = true, presets = h.Config.PoolPresets.Select(PresetJson).ToList() };
        })));

        g.MapPost("/pool-presets/from-device/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            new { presets = (await h.PoolPresetsFromDeviceAsync(Device(h, id))).Select(PresetJson).ToList() })));

        g.MapPost("/pool-presets/preview", async (PoolAssignRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var list = new List<PoolAssignItem>();
            foreach (var a in Assignments(h, req)) list.Add(await h.PoolAssignPreviewAsync(h.Device(a.Host)!, a));
            return new { items = AssignItems(list) };
        })));

        g.MapPost("/pool-presets/apply", async (PoolAssignRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            new { items = AssignItems(await h.ApplyPoolAssignmentsAsync(Assignments(h, req), L.T("Browser"))) })));

        // ---------- Gruppen ----------

        g.MapGet("/group-pools", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            groups = MinerGroups.All(h.Config.Devices).Select(name =>
            {
                var rule = h.GroupPoolRuleFor(name);
                return new
                {
                    name,
                    rule = rule?.Rule ?? new PoolSwitchRule(),
                    approved = rule is not null && h.IsGroupPoolRuleApproved(rule),
                    members = h.GroupMembers(name).Select(d => new { id = Dto.DeviceId(d.Host), name = d.Title, ownRule = d.Config.PoolAuto.Enabled }).ToList(),
                };
            }).ToList(),
        })));

        g.MapPut("/group-pools", async (PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var rule = h.SaveGroupPoolRule(GroupOf(req, h), req.Rule ?? throw new LocalizedException("Regel fehlt."));
            return new { ok = true, approved = h.IsGroupPoolRuleApproved(rule) };
        })));

        g.MapPost("/group-pools/approval-text", async (PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var group = GroupOf(req, h);
            var rule = h.GroupPoolRuleFor(group) ?? throw new LocalizedException("Für „{0}“ ist noch keine Regel gespeichert.", group);
            return new { text = MinerHub.PoolRuleText(rule.Rule, L.T("Gruppe „{0}“", group)) };
        })));

        g.MapPost("/group-pools/approve", async (PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.ApproveGroupPoolRule(GroupOf(req, h));
            return new { ok = true };
        })));

        g.MapPost("/group-pools/preview", async (PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            new { items = Items(await h.GroupPoolPreviewAsync(GroupOf(req, h), req.Target ?? "")) })));

        g.MapPost("/group-pools/switch", async (PoolRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            new { items = Items(await h.SwitchGroupPoolAsync(GroupOf(req, h), req.Target ?? "")) })));
    }
}
