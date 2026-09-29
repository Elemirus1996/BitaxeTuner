using System.Net;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Plugs;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

public sealed record PlugsRequest(SmartPlugSettings Settings, Dictionary<string, string>? Passwords, string[]? ClearPasswords);

public sealed record PlugProbeRequest(string Host, string? User, string? Password, string? Id, int Channel);

/// <summary>Smart Plugs (Shelly): Einstellungen (Passwörter nur schreibbar), Status, Verbindungstest. Nur Admin.</summary>
public static class PlugEndpoints
{
    public static object Status(MinerHub h) => new
    {
        settings = Dto.Copy(h.Config.Plugs),
        passwordSet = h.Config.Plugs.Items.ToDictionary(p => p.Id, p => h.Secrets.Has(SmartPlugConfig.SecretKey(p.Id))),
        status = Dto.Plugs(h, Role.Admin),
        miners = h.Devices.Select(d => new { host = d.Host, name = d.Title }).ToList(),
    };

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/plugs", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        g.MapPut("/plugs", async (PlugsRequest req, HubService hub) =>
        {
            var s = req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            foreach (var p in s.Items) await CheckHostAsync(p.Host);
            return Results.Json(await hub.RunAsync(async h =>
            {
                Normalize(s, h);
                var removed = h.Config.Plugs.Items.Select(p => p.Id).Except(s.Items.Select(p => p.Id)).ToList();
                h.Config.Plugs = s;
                foreach (var id in removed.Concat(req.ClearPasswords ?? [])) h.Secrets.Set(SmartPlugConfig.SecretKey(id), null);
                foreach (var (id, pw) in req.Passwords ?? [])
                    if (s.Items.Any(p => p.Id == id) && !string.IsNullOrEmpty(pw)) h.Secrets.Set(SmartPlugConfig.SecretKey(id), pw);
                h.Config.Save();
                await h.ApplyPlugSettingsAsync();
                return Status(h);
            }));
        });

        // Vor dem Speichern prüfen: Gerät erkennen und einmal messen (nie schalten)
        g.MapPost("/plugs/probe", async (PlugProbeRequest req, HubService hub) =>
        {
            await CheckHostAsync(req.Host);
            var stored = req.Id is { Length: > 0 } id && string.IsNullOrEmpty(req.Password)
                ? await hub.RunAsync(h => h.Secrets.Get(SmartPlugConfig.SecretKey(id)))
                : req.Password;
            if (SimulatedPlugClient.IsSimAddress(req.Host))
                return Results.Json(new { ok = true, generation = 2, model = L.N("Simulation"), authRequired = false, powerW = 12.5 });
            using var client = new ShellyClient(req.Host, req.User, stored);
            try
            {
                var id2 = await client.IdentifyAsync();
                var r = await client.ReadAsync(Math.Clamp(req.Channel, 0, 3));
                return Results.Json(new { ok = true, generation = id2.Generation, model = id2.Model, id2.AuthRequired, r.PowerW, r.EnergyWh, r.Voltage });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new LocalizedException("Plug nicht erreichbar: {0}", ex is TaskCanceledException ? L.N("keine Antwort") : ex.Message);
            }
        });
    }

    private static void Normalize(SmartPlugSettings s, MinerHub h)
    {
        s.IntervalSeconds = Math.Clamp(s.IntervalSeconds, 5, 300);
        var ids = new HashSet<string>();
        foreach (var p in s.Items)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9]{4,32}$") || !ids.Add(p.Id))
            {
                p.Id = SmartPlugConfig.NewId();
                ids.Add(p.Id);
            }
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Smart Plug" : p.Name.Trim();
            p.Host = (p.Host ?? "").Trim();
            p.User = string.IsNullOrWhiteSpace(p.User) ? "admin" : p.User.Trim();
            p.Channel = Math.Clamp(p.Channel, 0, 3);
            if (!SmartPlugConfig.Roles.Contains(p.Role)) throw new LocalizedException("Unbekannte Rolle: {0}", p.Role);
            p.Miners = p.Role == "miners"
                ? (p.Miners ?? []).Where(m => h.Device(m) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        if (s.Items.Count(p => p.Role == "total") > 1)
            throw new LocalizedException("Höchstens ein Plug als Gesamtmessung.");
    }

    /// <summary>Nur Geräte im Heimnetz (auch nach Namensauflösung) – der Server fragt keine Adressen im Internet ab.</summary>
    private static async Task CheckHostAsync(string host)
    {
        if (SimulatedPlugClient.IsSimAddress(host)) return;
        var uri = ShellyClient.BaseUri(host);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.IdnHost, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(uri.IdnHost);
        }
        catch (System.Net.Sockets.SocketException)
        {
            throw new LocalizedException("Plug-Adresse nicht gefunden: {0}", host);
        }
        if (addresses.Length == 0 || !addresses.All(NetworkRules.IsPrivate))
            throw new LocalizedException("Nur Adressen im Heimnetz erlaubt: {0}", host);
    }
}
