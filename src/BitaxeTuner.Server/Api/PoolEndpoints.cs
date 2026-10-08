using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// 0.9.11 Pool-Umschaltung (nur Admin): je Miner und je Gruppe mit Vorschau alt → neu, dazu die Pool-Automatik mit Freigabe.
/// </summary>
public static class PoolEndpoints
{
    public sealed record PoolRequest(string? Target, bool MakeHome, PoolSwitchRule? Rule, string? Group);

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
