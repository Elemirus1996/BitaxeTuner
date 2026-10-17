using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

public sealed record SolarRequest(SolarSettings Settings, string? Token);

/// <summary>0.9.17: Quelle der PV-Leistung (Messsteckdose oder Home Assistant). Token nur schreibbar. Nur Admin.</summary>
public static class SolarEndpoints
{
    public static object Status(MinerHub h)
    {
        var now = DateTime.Now;
        var st = h.SolarStatusNow(now);
        return new
        {
            settings = Dto.Copy(h.Config.Solar),
            tokenSet = h.Secrets.Has(MinerHub.SolarTokenKey),
            powerW = st.PowerW,
            at = st.At,
            error = st.Error,
            plugs = h.Config.Plugs.Items.Select(p => new { id = p.Id, name = p.Name, role = p.Role }).ToList(),
        };
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/solar", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        // 0.9.21 Heiz-Automatik: wählbare Fühler (Pico) und benutzte Home-Assistant-Sensoren mit aktuellem Wert
        g.MapGet("/heat-sensors", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var now = DateTime.Now;
            return new
            {
                sensors = h.RoomSensors(now).Select(s => new { s.Key, s.Name, s.Temp, error = h.RoomTempError(s.Key) }).ToList(),
                haConfigured = h.Config.Solar.HaUrl.Length > 0 && h.Secrets.Has(MinerHub.SolarTokenKey),
            };
        })));

        g.MapPut("/solar", async (SolarRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SaveSolarSettings(req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen.")), req.Token, "Browser");
            return Status(h);
        })));

        // Einmal lesen, ohne zu speichern (Token leer = gespeichertes verwenden)
        g.MapPost("/solar/test", async (SolarRequest req, HubService hub) =>
        {
            var s = req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            if (s.Source == "plug")
                return Results.Json(await hub.RunAsync(h => h.PlugSolarW(s.PlugId, DateTime.Now) is { } w
                    ? new { ok = true, powerW = (double?)w }
                    : throw new LocalizedException("Von dieser Messsteckdose liegt gerade kein frischer Messwert vor.")));
            if (s.Source != "homeassistant") throw new LocalizedException("Bitte zuerst eine Quelle auswählen.");
            var w = await hub.RunAsync(h => h.ReadHomeAssistantAsync(s, req.Token));
            return Results.Json(new { ok = true, powerW = (double?)w });
        });
    }
}
