using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>Gruppen-Automatik (nur Admin): Regel je Miner-Gruppe, Freigabe, „jetzt umschalten“ mit Vorschau alt → neu.</summary>
public static class GroupEndpoints
{
    public sealed record GroupRequest(string? Group, PresetScheduleRule? Schedule, string? Preset);

    private static string GroupOf(GroupRequest req, MinerHub h)
    {
        var g = (req.Group ?? "").Trim();
        if (g.Length == 0 || h.GroupMembers(g).Count == 0) throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", g);
        return g;
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/group-automation", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            groups = MinerGroups.All(h.Config.Devices).Select(name =>
            {
                var rule = h.GroupRule(name);
                return new
                {
                    name,
                    schedule = rule?.Schedule ?? new PresetScheduleRule(),
                    approved = rule is not null && h.IsGroupScheduleApproved(rule),
                    approvedAt = rule?.Schedule.ApprovedAt,
                    presetNames = h.GroupPresetNames(name),
                    members = h.GroupMembers(name).Select(d => new
                    {
                        id = Dto.DeviceId(d.Host), name = d.Title, ownSchedule = d.Config.Schedule.Enabled, d.AutomationStatus,
                        presets = d.Config.Presets.Select(p => p.Name).ToList(),
                    }).ToList(),
                };
            }).ToList(),
        })));

        g.MapPut("/group-automation", async (GroupRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var rule = h.SaveGroupSchedule(GroupOf(req, h), req.Schedule ?? throw new LocalizedException("Regel fehlt."));
            return new { ok = true, approved = h.IsGroupScheduleApproved(rule) };
        })));

        g.MapPost("/group-automation/approval-text", async (GroupRequest req, HubService hub) =>
            Results.Json(await hub.RunAsync(h => new { text = h.GroupScheduleApprovalText(GroupOf(req, h)) })));

        g.MapPost("/group-automation/approve", async (GroupRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.ApproveGroupSchedule(GroupOf(req, h));
            return new { ok = true };
        })));

        // Jetzt umschalten: erst Vorschau (alt → neu je Miner), dann bestätigt ausführen
        g.MapPost("/group-automation/preview", async (GroupRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var items = await h.PreviewGroupPresetAsync(GroupOf(req, h), req.Preset ?? "");
            return new
            {
                items = items.Select(i => new
                {
                    id = Dto.DeviceId(i.Device.Host), name = i.Device.Title, i.FrequencyMhz, i.CoreVoltageMv,
                    targetFrequencyMhz = i.Target?.FrequencyMhz, targetCoreVoltageMv = i.Target?.CoreVoltageMv, i.Skip,
                }).ToList(),
            };
        })));

        g.MapPost("/group-automation/apply", async (GroupRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var results = await h.ApplyGroupPresetAsync(GroupOf(req, h), req.Preset ?? "");
            return new { results = results.Select(r => new { name = r.Device.Title, result = r.Result }).ToList() };
        })));
    }
}
