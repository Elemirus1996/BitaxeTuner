using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

/// <summary>Kiosk-Designer (0.9.8): Designs anlegen, bearbeiten, löschen, je Kiosk-Link zuordnen; Anzeige liest ihr Design.</summary>
public static class KioskEndpoints
{
    public sealed record NewDesignRequest(string? Name, string? Preset, string? CopyOf);
    public sealed record KioskDesignRequest(string? DesignId);

    /// <summary>Standard-Design: das markierte, sonst das erste, sonst eine Vorlage (ohne zu speichern).</summary>
    internal static KioskDesign DefaultDesign(AppConfig c) =>
        c.KioskDesigns.FirstOrDefault(d => d.IsDefault) ?? c.KioskDesigns.FirstOrDefault() ?? KioskDesigns.Create("Standard", "bitcoin");

    /// <summary>Für Ansicht und Admin: Design der eigenen Kiosk-Sitzung, ein bestimmtes (nur Admin, Vorschau) oder das Standard-Design.</summary>
    public static void MapViewer(RouteGroupBuilder g)
    {
        // Hilfe (0.9.9): Anleitung, Protokoll und Miner-Logs erklärt, häufige Fragen
        g.MapGet("/help", () => Results.Json(new { sections = Core.Help.HelpContent.Sections(server: true) }));

        g.MapGet("/kiosk/design", async (string? id, HttpContext http, AuthStore auth, HubService hub) =>
        {
            var ctx = AuthContext.Of(http);
            var wanted = ctx.Role == Role.Admin && !string.IsNullOrEmpty(id) ? id : auth.KioskDesignOf(ctx.Session?.AccessId);
            return Results.Json(await hub.RunAsync(h => new
            {
                design = h.Config.KioskDesigns.FirstOrDefault(d => d.Id == wanted) ?? DefaultDesign(h.Config),
                title = h.Config.Display.Title,
            }));
        });
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/kiosk-designs", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            designs = h.Config.KioskDesigns,
            presets = KioskDesigns.Presets,
            panelTypes = KioskDesigns.PanelTypes,
        })));

        g.MapPost("/kiosk-designs", async (NewDesignRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            if (h.Config.KioskDesigns.Count >= KioskDesigns.MaxDesigns) throw new LocalizedException("Höchstens {0} Designs.", KioskDesigns.MaxDesigns);
            var name = string.IsNullOrWhiteSpace(req.Name) ? L.T("Design {0}", h.Config.KioskDesigns.Count + 1) : req.Name.Trim();
            KioskDesign d;
            if (h.Config.KioskDesigns.FirstOrDefault(x => x.Id == req.CopyOf) is { } src)
            {
                d = System.Text.Json.JsonSerializer.Deserialize<KioskDesign>(System.Text.Json.JsonSerializer.Serialize(src))!;
                d.Id = Guid.NewGuid().ToString("N")[..10];
                d.Name = name;
                d.IsDefault = false;
            }
            else d = KioskDesigns.Create(name, req.Preset ?? "bitcoin");
            KioskDesigns.Validate(d);
            if (h.Config.KioskDesigns.Count == 0) d.IsDefault = true;
            h.Config.KioskDesigns.Add(d);
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Kiosk-Design „{0}“ angelegt.", d.Name));
            return d;
        })));

        g.MapPut("/kiosk-designs/{id}", async (string id, KioskDesign req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var i = h.Config.KioskDesigns.FindIndex(d => d.Id == id);
            if (i < 0) throw new LocalizedException("Design nicht gefunden.") { Status = 404 };
            req.Id = id;
            KioskDesigns.Validate(req);
            if (req.IsDefault) foreach (var d in h.Config.KioskDesigns) d.IsDefault = false;
            else if (h.Config.KioskDesigns[i].IsDefault) req.IsDefault = true;   // es gibt immer ein Standard-Design
            h.Config.KioskDesigns[i] = req;
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Kiosk-Design „{0}“ gespeichert.", req.Name));
            return req;
        })));

        g.MapDelete("/kiosk-designs/{id}", async (string id, HubService hub, AuthStore auth) => Results.Json(await hub.RunAsync(h =>
        {
            var d = h.Config.KioskDesigns.FirstOrDefault(x => x.Id == id) ?? throw new LocalizedException("Design nicht gefunden.") { Status = 404 };
            h.Config.KioskDesigns.Remove(d);
            if (d.IsDefault && h.Config.KioskDesigns.Count > 0) h.Config.KioskDesigns[0].IsDefault = true;
            foreach (var k in auth.Kiosks.Where(k => k.DesignId == id)) auth.SetKioskDesign(k.Id, null);
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Kiosk-Design „{0}“ gelöscht.", d.Name));
            return new { ok = true };
        })));

        g.MapPut("/kiosks/{kioskId}/design", async (string kioskId, KioskDesignRequest req, AuthStore auth, HubService hub) =>
        {
            var exists = await hub.RunAsync(h => req.DesignId is null || h.Config.KioskDesigns.Any(d => d.Id == req.DesignId));
            if (!exists) throw new LocalizedException("Design nicht gefunden.") { Status = 404 };
            if (!auth.SetKioskDesign(kioskId, req.DesignId)) throw new LocalizedException("Kiosk-Link nicht gefunden.") { Status = 404 };
            return Results.Ok(new { ok = true });
        });
    }
}
