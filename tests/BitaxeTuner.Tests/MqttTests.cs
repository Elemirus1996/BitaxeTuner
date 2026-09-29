using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace BitaxeTuner.Tests;

/// <summary>Home Assistant / MQTT gegen einen echten (eingebetteten) Broker.</summary>
public class MqttTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Broker : IAsyncDisposable
    {
        public readonly int Port = FreePort();
        public readonly ConcurrentDictionary<string, string> Last = new();
        public readonly MqttServer Server;

        public Broker()
        {
            var f = new MqttFactory();
            Server = f.CreateMqttServer(f.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(Port).Build());
            Server.ValidatingConnectionAsync += e =>
            {
                if (e.ClientId.StartsWith("bitaxetuner-") && (e.UserName != "ha" || e.Password != "mqtt-geheim"))
                    e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
            Server.InterceptingPublishAsync += e =>
            {
                Last[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString() ?? "";
                return Task.CompletedTask;
            };
        }

        public async Task SendAsync(string topic, string payload)
        {
            var f = new MqttFactory();
            using var c = f.CreateMqttClient();
            await c.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", Port).WithClientId("ha-test").Build());
            await c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build());
            await c.DisconnectAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync();
            Server.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task Publishes_states_and_home_assistant_discovery_and_only_allows_fan_mode()
    {
        await using var broker = new Broker();
        await broker.Server.StartAsync();
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma Wohnzimmer", Host = "10.0.5.1" });
        config.Mqtt = new MqttSettings { Enabled = true, Host = "127.0.0.1", Port = broker.Port, User = "ha" };
        config.Plugs.Items.Add(new SmartPlugConfig { Id = "plug1", Name = "Steckdose", Host = "sim", Miners = ["10.0.5.1"] });
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => new SimulatedFanDevice(),
        });
        try
        {
            // Falsches Passwort: klare Meldung, keine Ausnahme
            hub.Secrets.Set(SecretStore.MqttPassword, "falsch");
            await hub.ApplyMqttSettingsAsync();
            Assert.False(hub.MqttConnected);
            Assert.NotNull(hub.MqttError);

            hub.Secrets.Set(SecretStore.MqttPassword, "mqtt-geheim");
            await hub.PollNowAsync();
            await hub.PlugTickAsync();
            await hub.ApplyMqttSettingsAsync();
            Assert.True(hub.MqttConnected, hub.MqttError);
            await Until(() => broker.Last.ContainsKey("bitaxetuner/miner/10_0_5_1/state"));

            Assert.Equal("online", broker.Last["bitaxetuner/status"]);
            var state = JsonDocument.Parse(broker.Last["bitaxetuner/miner/10_0_5_1/state"]).RootElement;
            Assert.Equal("ON", state.GetProperty("online").GetString());
            Assert.True(state.GetProperty("hashrate_gh").GetDouble() > 0);
            var cfg = JsonDocument.Parse(broker.Last["homeassistant/sensor/bitaxetuner/miner_10_0_5_1_hashrate/config"]).RootElement;
            Assert.Equal("Gamma Wohnzimmer", cfg.GetProperty("device").GetProperty("name").GetString());
            Assert.Equal("bitaxetuner/status", cfg.GetProperty("availability_topic").GetString());
            Assert.Equal("", broker.Last["homeassistant/select/bitaxetuner/server_fan_mode/config"]);   // nicht freigegeben
            var energy = JsonDocument.Parse(broker.Last["homeassistant/sensor/bitaxetuner/plug_plug1_energy/config"]).RootElement;
            Assert.Equal("total_increasing", energy.GetProperty("state_class").GetString());
            var server = JsonDocument.Parse(broker.Last["bitaxetuner/server/state"]).RootElement;
            Assert.True(server.GetProperty("plugs").GetProperty("plug1").GetProperty("power_w").GetDouble() > 0);
            Assert.True(server.GetProperty("wall_power_w").GetDouble() > server.GetProperty("power_w").GetDouble());
            Assert.DoesNotContain(broker.Last.Keys, t => t.Contains("frequency") && t.EndsWith("/set"));

            // Lüfter-Befehl ohne Freigabe: wirkungslos
            await broker.SendAsync("bitaxetuner/server/fan_mode/set", "Aus");
            await Task.Delay(300);
            Assert.Equal(FanOverride.None, hub.FanOverride);

            // Mit Freigabe: wirkt, Home Assistant sieht das Auswahlfeld
            hub.Config.Mqtt.AllowFanControl = true;
            await hub.ApplyMqttSettingsAsync();
            await Until(() => broker.Last.GetValueOrDefault("homeassistant/select/bitaxetuner/server_fan_mode/config", "").Length > 0);
            await broker.SendAsync("bitaxetuner/server/fan_mode/set", "Aus");
            await Until(() => hub.FanOverride == FanOverride.Off);
            await Until(() => JsonDocument.Parse(broker.Last["bitaxetuner/server/state"]).RootElement.GetProperty("fan_mode").GetString() == "Aus");
            await broker.SendAsync("bitaxetuner/server/fan_mode/set", "Unsinn");                     // unbekannt: ignoriert
            await Task.Delay(300);
            Assert.Equal(FanOverride.Off, hub.FanOverride);
        }
        finally
        {
            hub.Dispose();
        }
        await Until(() => broker.Last["bitaxetuner/status"] == "offline");                           // sauber abgemeldet
    }
}
