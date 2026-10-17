using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Server.Api;

/// <summary>0.9.11 Übersicht-Designer: Aufbau der Übersicht (für alle Browser gleich) und Reihenfolge der Miner.</summary>
public static class OverviewEndpoints
{
    public sealed record LayoutRequest(List<OverviewPanel>? Panels, bool Reset, string? Template = null, bool Restore = false);
    public sealed record OrderRequest(List<string>? Ids);

    public static void MapViewer(RouteGroupBuilder g)
    {
        g.MapGet("/overview-layout", async (HttpContext http, HubService hub) =>
        {
            var lang = Endpoints.LangOf(http);
            return Results.Json(await hub.RunAsync(h => new
            {
                layout = h.Config.OverviewLayout ?? OverviewLayouts.Default(),
                custom = h.Config.OverviewLayout is not null,
                panelTypes = OverviewLayouts.PanelTypes,
                // 0.9.23 Vorlagen und gesicherter eigener Aufbau
                templates = OverviewLayouts.Templates.Select(x => new { id = x.Id, name = lang.T(x.Name), hint = lang.T(x.Hint) }).ToList(),
                hasBackup = h.Config.OverviewLayoutBackup is not null,
            }));
        });
    }

    public static void MapAdmin(RouteGroupBuilder g)
    {
        g.MapPut("/overview-layout", async (LayoutRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            string? log = null;
            if (req.Template is { Length: > 0 } id)
            {
                // 0.9.23: Vorlage übernehmen – der bisherige Aufbau wird gesichert (ein Schritt zurück)
                var template = OverviewLayouts.Template(id) ?? throw new LocalizedException("Unbekannte Vorlage.");
                h.Config.OverviewLayoutBackup = h.Config.OverviewLayout ?? OverviewLayouts.Default();
                h.Config.OverviewLayout = template;
                var name = OverviewLayouts.Templates.First(x => x.Id == id).Name;
                log = L.T("Übersicht: Vorlage „{0}“ übernommen, bisheriger Aufbau gesichert.", L.T(name));
            }
            else if (req.Restore)
            {
                var backup = h.Config.OverviewLayoutBackup ?? throw new LocalizedException("Kein gesicherter Aufbau vorhanden.");
                h.Config.OverviewLayoutBackup = h.Config.OverviewLayout ?? OverviewLayouts.Default();
                h.Config.OverviewLayout = backup;
                log = L.T("Übersicht: gesicherten Aufbau zurückgeholt.");
            }
            else if (req.Reset)
            {
                h.Config.OverviewLayout = null;
            }
            else
            {
                var layout = new OverviewLayout { Panels = req.Panels ?? [] };
                OverviewLayouts.Validate(layout);
                h.Config.OverviewLayout = layout;
            }
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, log ?? (req.Reset ? L.T("Übersicht auf den Standardaufbau zurückgesetzt.") : L.T("Aufbau der Übersicht geändert.")));
            return new { ok = true, layout = h.Config.OverviewLayout ?? OverviewLayouts.Default(), custom = h.Config.OverviewLayout is not null,
                hasBackup = h.Config.OverviewLayoutBackup is not null };
        })));

        // Reihenfolge der Miner (IDs wie in /status) – gilt überall
        g.MapPut("/devices/order", async (OrderRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var hosts = (req.Ids ?? []).Select(id => Dto.Find(h, id)?.Host).OfType<string>().ToList();
            if (hosts.Count == 0) throw new LocalizedException("Keine Miner angegeben.");
            h.ReorderDevices(hosts, L.T("Browser"));
            return new { ok = true, order = h.Devices.Select(d => Dto.DeviceId(d.Host)).ToList() };
        })));
    }
}
