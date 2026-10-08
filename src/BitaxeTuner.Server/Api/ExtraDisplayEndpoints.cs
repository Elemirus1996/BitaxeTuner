using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

/// <summary>0.9.11: weitere E-Paper-Anzeigen mit eigenem Display-Pico – anlegen, einstellen, Vorschau, Taste 1 aus dem Browser.</summary>
public static class ExtraDisplayEndpoints
{
    public const int MaxDisplays = 8;

    public sealed record ExtraDisplayRequest(string? Name, string? Group, DisplaySettings? Settings);

    private static ExtraDisplayRuntime Find(MinerHub h, string id) =>
        h.ExtraDisplay(id) ?? throw new LocalizedException("Anzeige nicht gefunden.") { Status = 404 };

    private static object View(ExtraDisplayRuntime r, bool admin) => new
    {
        id = r.Config.Id,
        name = r.Config.Name,
        group = r.Config.Group,
        status = r.Status with { Enabled = r.Config.Settings.Enabled },
        lastScene = r.LastScene.ToString(),
        device = admin ? r.Description : null,
        settings = admin ? Dto.Copy(r.Config.Settings) : null,
    };

    /// <summary>Lesen und Vorschau (wie die erste Anzeige nicht für Ansicht-Zugänge mit Gruppen).</summary>
    public static void MapViewer(RouteGroupBuilder g)
    {
        g.MapGet("/displays", async (HttpContext http, HubService hub) =>
        {
            var auth = AuthContext.Of(http);
            if (auth.Scope.Restricted) return Results.Json(new { displays = Array.Empty<object>() });
            return Results.Json(await hub.RunAsync(h => new { displays = h.ExtraRuntimes().Select(r => View(r, auth.Role == Role.Admin)).ToList() }));
        });

        g.MapGet("/displays/{id}/preview.png", async (string id, string? scene, HttpContext http, HubService hub) =>
        {
            if (AuthContext.Of(http).Scope.Restricted) return Results.Json(new { error = L.N("Für diesen Ansicht-Zugang nicht freigegeben.") }, statusCode: 403);
            var model = await hub.RunAsync(h => h.ComposeExtraDisplay(Find(h, id), DateTime.Now,
                Enum.TryParse<DisplayScene>(scene, true, out var sc) ? sc : null));
            using var img = StatusRenderer.RenderImage(model);
            var ms = new MemoryStream();
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(img, ms);
            return Results.File(ms.ToArray(), "image/png");
        });
    }

    public static void MapAdmin(RouteGroupBuilder g)
    {
        g.MapPost("/displays", async (ExtraDisplayRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            if (h.Config.ExtraDisplays.Count >= MaxDisplays) throw new LocalizedException("Höchstens {0} weitere Anzeigen.", MaxDisplays);
            var c = new ExtraDisplayConfig();
            c.Name = Name(req.Name, L.T("Anzeige {0}", h.Config.ExtraDisplays.Count + 2));
            c.Group = Group(h, req.Group);
            c.Settings.Title = c.Name;
            c.Settings.NetworkHost = $"bitaxetuner-display-{c.Id}.local";
            h.Config.ExtraDisplays.Add(c);
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Weitere Anzeige „{0}“ angelegt.", c.Name));
            return View(h.ExtraDisplay(c.Id)!, true);
        })));

        g.MapPut("/displays/{id}", async (string id, ExtraDisplayRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var r = Find(h, id);
            var s = req.Settings ?? r.Config.Settings;
            Endpoints.NormalizeDisplay(s);
            s.Device = "own";                                   // weitere Anzeigen haben immer einen eigenen Pico
            var c = new ExtraDisplayConfig { Id = r.Config.Id, Name = Name(req.Name, r.Config.Name), Group = Group(h, req.Group), Settings = s };
            h.Config.ExtraDisplays[h.Config.ExtraDisplays.FindIndex(x => x.Id == id)] = c;
            h.Config.Save();
            var updated = h.ExtraDisplay(id)!;
            h.RequestExtraDisplayRefresh(updated);
            await h.ApplyFanSettingsAsync();
            return View(updated, true);
        })));

        g.MapDelete("/displays/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = Find(h, id);
            h.Config.ExtraDisplays.RemoveAll(x => x.Id == id);
            h.Config.Save();
            h.ExtraRuntimes();                                  // schließt die Verbindung
            h.LogEvent(null, EventCategories.Settings, L.T("Weitere Anzeige „{0}“ entfernt.", MinerHub.ExtraName(r.Config)));
            return new { ok = true };
        })));

        // Wie Taste 1 an dieser Anzeige
        g.MapPost("/displays/{id}/next", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = Find(h, id);
            h.ExtraDisplayNext(r, "Browser");
            return View(r, true);
        })));

        g.MapPost("/displays/{id}/refresh", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = Find(h, id);
            h.RequestExtraDisplayRefresh(r);
            return View(r, true);
        })));
    }

    private static string Name(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        return n.Length == 0 ? fallback : n[..Math.Min(40, n.Length)];
    }

    /// <summary>Leer oder eine vorhandene Gruppe (Groß-/Kleinschreibung wie in den Geräten).</summary>
    private static string Group(MinerHub h, string? group)
    {
        var g = (group ?? "").Trim();
        if (g.Length == 0) return "";
        return MinerGroups.All(h.Config.Devices).FirstOrDefault(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase))
               ?? throw new LocalizedException("Gruppe „{0}“ hat keine Miner.", g);
    }
}
