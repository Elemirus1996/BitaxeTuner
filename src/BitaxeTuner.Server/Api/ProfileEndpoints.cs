using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Server.Api;

/// <summary>Geräteprofile bearbeiten (nur Admin, 0.9.9) – wie am Desktop: anlegen, ändern, kopieren, löschen/zurücksetzen.</summary>
public static class ProfileEndpoints
{
    public sealed record ProfileSaveRequest(DeviceProfile? Profile, bool Confirmed);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/profiles", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            profiles = h.ListProfiles().Select(e => new
            {
                profile = e.Profile,
                builtIn = e.BuiltIn,
                e.IsBuiltIn,
                e.IsCustomized,
                devices = h.Devices.Where(d => string.Equals(d.Profile.Id, e.Profile.Id, StringComparison.OrdinalIgnoreCase)).Select(d => d.Title).ToList(),
            }).ToList(),
        })));

        // Entwurf einer Kopie (nicht gespeichert) – Grundlage für ein eigenes Profil
        g.MapPost("/profiles/{id}/copy", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var list = h.ListProfiles();
            var source = list.FirstOrDefault(e => string.Equals(e.Profile.Id, id, StringComparison.OrdinalIgnoreCase))
                         ?? throw new KeyNotFoundException(L.N("Profil unbekannt."));
            return new { profile = ProfileEditor.CopyOf(source.Profile, list.Select(e => e.Profile.Id)) };
        })));

        g.MapPost("/profiles", async (ProfileSaveRequest req, HubService hub) => await Save(hub, req, null));
        g.MapPut("/profiles/{id}", async (string id, ProfileSaveRequest req, HubService hub) => await Save(hub, req, id));

        g.MapDelete("/profiles/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.RemoveProfile(id);
            return new { ok = true };
        })));
    }

    private static async Task<IResult> Save(HubService hub, ProfileSaveRequest req, string? originalId)
    {
        if (req.Profile is null) return Endpoints.Error(400, L.N("Profil fehlt."));
        try
        {
            return Results.Json(await hub.RunAsync(h =>
            {
                var check = h.SaveProfile(req.Profile, originalId, req.Confirmed);
                var saved = check.Warnings.Count == 0 || req.Confirmed;
                return new { ok = true, saved, needsConfirmation = !saved, warnings = check.Warnings, changes = check.Changes };
            }));
        }
        catch (ArgumentException ex) { return Endpoints.Error(400, ex.Message); }
    }
}
