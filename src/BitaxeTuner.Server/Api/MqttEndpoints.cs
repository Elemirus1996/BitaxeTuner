using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

public sealed record MqttRequest(MqttSettings Settings, string? Password, bool ClearPassword);

/// <summary>Home Assistant / MQTT: Einstellungen (Passwort nur schreibbar), Status.</summary>
public static class MqttEndpoints
{
    private static object Status(Core.Host.MinerHub h) => new
    {
        settings = Dto.Copy(h.Config.Mqtt),
        passwordSet = h.Secrets.Has(SecretStore.MqttPassword),
        connected = h.MqttConnected,
        error = h.MqttError,
    };

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/mqtt", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        g.MapPut("/mqtt", async (MqttRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var s = req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            s.Host = (s.Host ?? "").Trim();
            s.User = (s.User ?? "").Trim();
            s.Port = s.Port is > 0 and < 65536 ? s.Port : 1883;
            s.IntervalSeconds = Math.Clamp(s.IntervalSeconds, 5, 3600);
            s.BaseTopic = (s.BaseTopic ?? "").Trim().Trim('/');
            s.DiscoveryPrefix = (s.DiscoveryPrefix ?? "").Trim().Trim('/');
            if (s.BaseTopic.Length == 0) s.BaseTopic = "bitaxetuner";
            if (s.DiscoveryPrefix.Length == 0) s.DiscoveryPrefix = "homeassistant";
            if (s.BaseTopic.IndexOfAny(['#', '+']) >= 0 || s.DiscoveryPrefix.IndexOfAny(['#', '+']) >= 0)
                throw new InvalidOperationException(L.N("MQTT: Topics dürfen kein # oder + enthalten."));
            if (s.Enabled && s.Host.Length == 0) throw new InvalidOperationException(L.N("MQTT: Broker-Adresse angeben (z. B. IP von Home Assistant)."));
            h.Config.Mqtt = s;
            if (req.ClearPassword) h.Secrets.Set(SecretStore.MqttPassword, null);
            else if (!string.IsNullOrEmpty(req.Password)) h.Secrets.Set(SecretStore.MqttPassword, req.Password);
            h.Config.Save();
            await h.ApplyMqttSettingsAsync();
            return Status(h);
        })));
    }
}
