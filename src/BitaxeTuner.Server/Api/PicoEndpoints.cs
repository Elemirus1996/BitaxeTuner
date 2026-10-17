using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// 0.9.23 Pico-Übersicht (nur Admin): alle benutzten Picos mit Rolle, Verbindung, Programm und WLAN-Signal; blinken, neu
/// starten, Programm neu aufspielen, Schlüssel erneuern und neu koppeln (nach Tastendruck am Pico).
/// </summary>
public static class PicoEndpoints
{
    public static void MapAdmin(RouteGroupBuilder g)
    {
        g.MapGet("/picos", async (HubService hub) => Results.Json(await hub.RunAsync(async h => new
        {
            picos = await h.PicosAsync(refresh: true),
            firmware = Core.Fans.PicoFanDevice.FirmwareVersion,
        })));

        g.MapPost("/picos/{slot}/blink", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.PicoBlinkAsync(slot, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPost("/picos/{slot}/restart", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.PicoRestartAsync(slot, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPost("/picos/{slot}/reinstall", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.PicoReinstallAsync(slot, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPost("/picos/{slot}/rotate-key", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.PicoRotateKeyAsync(slot, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPost("/picos/{slot}/pair", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            new { ok = true, code = await h.PicoPairStartAsync(slot, L.T("Browser")) })));

        g.MapPost("/picos/{slot}/pair/confirm", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.PicoPairConfirmAsync(slot, L.T("Browser"));
            return new { ok = true };
        })));

        g.MapPost("/picos/{slot}/pair/cancel", async (string slot, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.PicoPairCancel(slot);
            return new { ok = true };
        })));
    }
}
