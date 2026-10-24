using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>Miner per Geräte-ID für das Firmware-Update (leer = alle aktualisierbaren).</summary>
public sealed record FirmwareUpdateRequest(List<string>? Ids);

/// <summary>0.9.25 Firmware-Update für alle Miner nacheinander – nur Admin, nur per Knopf.</summary>
public static class FirmwareEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/firmware", async (HubService hub) => Results.Json(await hub.RunAsync(h => State(h))));

        g.MapPost("/firmware/update", async (FirmwareUpdateRequest req, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var hosts = req.Ids is { Count: > 0 } ids
                ? ids.Select(id => Dto.Find(h, id)?.Host ?? throw new KeyNotFoundException(L.N("Gerät nicht gefunden."))).ToList()
                : h.FirmwareCandidates().Where(c => c.CanUpdate).Select(c => c.Host).ToList();
            h.StartFirmwareUpdate(hosts, Endpoints.Who(http));
            return State(h);
        })));

        g.MapPost("/firmware/cancel", async (HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.CancelFirmwareUpdate(Endpoints.Who(http));
            return State(h);
        })));

        // Nach dem Update abweichende Einstellungen zurückspielen – die Oberfläche zeigt vorher alt → neu und fragt nach
        g.MapPost("/firmware/restore/{id}", async (string id, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Dto.Find(h, id) ?? throw new KeyNotFoundException(L.N("Gerät nicht gefunden."));
            await h.RestoreAfterFirmwareAsync(d.Host, Endpoints.Who(http));
            return State(h);
        })));
    }

    private static object State(MinerHub h) => new
    {
        candidates = h.FirmwareCandidates().Select(c => new
        {
            id = Dto.DeviceId(c.Host), c.Name, c.Installed, c.Target, c.Prerelease, c.Beta, c.ReleaseUrl, c.Reason, c.CanUpdate,
        }).ToList(),
        job = h.FirmwareJobState is { } j ? new
        {
            j.Started, j.Finished, j.Who, j.Running, j.CancelRequested,
            items = j.Items.Select(i => new
            {
                id = Dto.DeviceId(i.Host), i.Name, i.From, i.To, i.Status, i.Message, i.Finished,
                changes = i.Changes.Select(c => new { c.Field, c.Label, group = c.GroupText, before = c.Saved, now = c.Current }).ToList(),
            }).ToList(),
        } : null,
    };
}
