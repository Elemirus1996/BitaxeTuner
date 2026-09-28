using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.I18n;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace BitaxeTuner.Core.Integrations;

/// <summary>Messwerte eines Miners für MQTT.</summary>
public sealed record MqttMiner(string Key, string Name, string Model, bool Online, double? HashrateGh, double? PowerW, double? EfficiencyJth,
    double? Temp, double? VrTemp, int? FrequencyMhz, int? CoreVoltageMv, string? BestDiff, int? FanPercent, string Soak);

/// <summary>Zusatzlüfter und Temperaturfühler für MQTT.</summary>
public sealed record MqttFan(int Channel, string Name, int Percent, int? Rpm);
public sealed record MqttSensor(string Key, string Name, double? Temp);

/// <summary>Gesamtzustand für MQTT.</summary>
public sealed record MqttSnapshot(double HashrateGh, double PowerW, int Online, int Count, double? PriceCt, bool Paused, string FanMode,
    IReadOnlyList<MqttMiner> Miners, IReadOnlyList<MqttFan> Fans, IReadOnlyList<MqttSensor> Sensors);

/// <summary>
/// Verbindung zum MQTT-Broker (z. B. Mosquitto in Home Assistant): Zustände als JSON (retained), Geräteerkennung
/// für Home Assistant, Verfügbarkeit per Last Will. Befehle: nur Lüfter-Modus (wenn erlaubt) und „Anzeige aktualisieren“.
/// </summary>
public sealed partial class MqttBridge : IAsyncDisposable
{
    /// <summary>Optionen des Auswahlfelds in Home Assistant, in der Sprache des Servers.</summary>
    public static string[] FanModes => [L.T("Automatik"), "100 %", L.T("Aus")];

    /// <summary>Befehl aus Home Assistant in einen Modus übersetzen – nimmt Deutsch und Englisch an (Sprachwechsel).</summary>
    public static FanOverride? ParseFanMode(string payload) => payload.Trim().ToLowerInvariant() switch
    {
        "automatik" or "automatic" or "auto" => FanOverride.None,
        "100 %" or "100%" or "full" => FanOverride.Full,
        "aus" or "off" => FanOverride.Off,
        _ => null,
    };

    public static string FanModeText(FanOverride mode) => mode switch
    {
        FanOverride.Full => "100 %",
        FanOverride.Off => L.T("Aus"),
        _ => L.T("Automatik"),
    };

    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();
    private MqttSettings _settings = new();
    private string _discoveryHash = "";
    private DateTime _nextAttempt = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Befehl aus Home Assistant: ("fan_mode", "Aus") oder ("display_refresh", "").</summary>
    public event Func<string, string, Task>? Command;

    public bool Connected => _client.IsConnected;
    public string? LastError { get; private set; }

    public MqttBridge()
    {
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            var b = Base;
            if (topic == $"{b}/server/fan_mode/set" && _settings.AllowFanControl && ParseFanMode(payload) is not null)
            {
                if (Command is { } c) await c("fan_mode", payload);
            }
            else if (topic == $"{b}/server/display_refresh/press")
            {
                if (Command is { } c) await c("display_refresh", "");
            }
        };
    }

    private string Base => Clean(_settings.BaseTopic, "bitaxetuner");

    [GeneratedRegex("[^a-zA-Z0-9_]")]
    private static partial Regex NonId();

    /// <summary>Schlüssel für Topics und IDs (a–z, 0–9, _).</summary>
    public static string Key(string s) => NonId().Replace(s.Trim().ToLowerInvariant(), "_");

    private static string Clean(string topic, string fallback)
    {
        var t = (topic ?? "").Trim().Trim('/');
        return t.Length == 0 || t.Contains('#') || t.Contains('+') ? fallback : t;
    }

    /// <summary>Einstellungen übernehmen; bei Änderungen neu verbinden.</summary>
    public async Task ApplyAsync(MqttSettings settings, string? password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client.IsConnected) await DisconnectCoreAsync();
            _settings = settings;
            _discoveryHash = "";
            _nextAttempt = DateTime.MinValue;
            LastError = null;
            if (settings.Enabled) await ConnectCoreAsync(password, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectCoreAsync(string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.Host)) throw new InvalidOperationException(L.T("MQTT: Broker-Adresse fehlt."));
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host.Trim(), _settings.Port is > 0 and < 65536 ? _settings.Port : 1883)
            .WithClientId("bitaxetuner-" + Key(Environment.MachineName))
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic($"{Base}/status").WithWillPayload("offline").WithWillRetain().WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (_settings.User.Trim() is { Length: > 0 } user) b.WithCredentials(user, password ?? "");
        if (_settings.Tls) b.WithTlsOptions(o => o.UseTls());
        try
        {
            var result = await _client.ConnectAsync(b.Build(), ct);
            if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException(L.T("MQTT: Verbindung abgelehnt ({0}).", result.ResultCode));
            await PublishAsync($"{Base}/status", "online", retain: true, ct);
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"{Base}/server/+/set"))
                .WithTopicFilter(f => f.WithTopic($"{Base}/server/+/press"))
                .Build(), ct);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            _nextAttempt = DateTime.UtcNow.AddSeconds(30);
            throw ex as IOException ?? new IOException("MQTT: " + ex.Message, ex);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        try
        {
            await PublishAsync($"{Base}/status", "offline", retain: true, default);
            await _client.DisconnectAsync();
        }
        catch { /* egal */ }
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(Encoding.UTF8.GetBytes(payload)).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build(), ct);

    /// <summary>Zustand senden (bei Bedarf vorher verbinden und Geräte für Home Assistant anmelden).</summary>
    public async Task PublishAsync(MqttSnapshot s, string? password, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_client.IsConnected)
            {
                if (DateTime.UtcNow < _nextAttempt) return;
                try { await ConnectCoreAsync(password, ct); }
                catch (IOException) { return; }
                _discoveryHash = "";
            }
            if (_settings.Discovery) await PublishDiscoveryAsync(s, ct);

            var b = Base;
            await PublishAsync($"{b}/server/state", JsonSerializer.Serialize(new
            {
                hashrate_gh = Math.Round(s.HashrateGh, 1),
                power_w = Math.Round(s.PowerW, 1),
                efficiency_jth = s.HashrateGh > 1 ? Math.Round(s.PowerW / (s.HashrateGh / 1000), 2) : (double?)null,
                online = s.Online,
                count = s.Count,
                price_ct = s.PriceCt is { } p ? Math.Round(p, 2) : (double?)null,
                paused = s.Paused ? "ON" : "OFF",
                fan_mode = s.FanMode,
                fans = s.Fans.ToDictionary(f => $"k{f.Channel}", f => new { percent = f.Percent, rpm = f.Rpm }),
                sensors = s.Sensors.ToDictionary(x => x.Key, x => x.Temp is { } t ? Math.Round(t, 1) : (double?)null),
            }), retain: true, ct);
            foreach (var m in s.Miners)
                await PublishAsync($"{b}/miner/{m.Key}/state", JsonSerializer.Serialize(new
                {
                    online = m.Online ? "ON" : "OFF",
                    hashrate_gh = Round(m.HashrateGh, 1),
                    power_w = Round(m.PowerW, 1),
                    efficiency_jth = Round(m.EfficiencyJth, 2),
                    temp = Round(m.Temp, 1),
                    vr_temp = Round(m.VrTemp, 1),
                    frequency_mhz = m.FrequencyMhz,
                    core_voltage_mv = m.CoreVoltageMv,
                    best_diff = m.BestDiff,
                    fan_percent = m.FanPercent,
                    soak = m.Soak,
                }), retain: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double? Round(double? v, int digits) => v is { } x ? Math.Round(x, digits) : null;

    /// <summary>Home-Assistant-Geräteerkennung – nur neu senden, wenn sich Geräte/Namen/Einstellungen ändern.</summary>
    private async Task PublishDiscoveryAsync(MqttSnapshot s, CancellationToken ct)
    {
        var configs = DiscoveryConfigs(s);
        var hash = string.Join("\n", configs.Select(c => c.Topic + c.Payload));
        if (hash == _discoveryHash) return;
        foreach (var (topic, payload) in configs) await PublishAsync(topic, payload, retain: true, ct);
        _discoveryHash = hash;
    }

    /// <summary>Alle Discovery-Nachrichten (öffentlich für Tests).</summary>
    public IReadOnlyList<(string Topic, string Payload)> DiscoveryConfigs(MqttSnapshot s)
    {
        var b = Base;
        var prefix = Clean(_settings.DiscoveryPrefix, "homeassistant");
        var list = new List<(string, string)>();
        var serverDevice = new JsonObject
        {
            ["identifiers"] = new JsonArray("bitaxetuner_server"),
            ["name"] = "BitaxeTuner Server",
            ["manufacturer"] = "BitaxeTuner",
            ["model"] = "Server",
        };

        void Add(string component, string objectId, string name, string stateTopic, string template, JsonObject device, Action<JsonObject>? extra = null)
        {
            var o = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = $"bitaxetuner_{objectId}",
                ["object_id"] = $"bitaxetuner_{objectId}",
                ["state_topic"] = stateTopic,
                ["value_template"] = template,
                ["availability_topic"] = $"{b}/status",
                ["device"] = device.DeepClone(),
            };
            extra?.Invoke(o);
            list.Add(($"{prefix}/{component}/bitaxetuner/{objectId}/config", o.ToJsonString()));
        }
        void Unit(JsonObject o, string unit, string? deviceClass = null, string stateClass = "measurement")
        {
            o["unit_of_measurement"] = unit;
            if (deviceClass is not null) o["device_class"] = deviceClass;
            o["state_class"] = stateClass;
        }

        var st = $"{b}/server/state";
        Add("sensor", "server_hashrate", L.T("Hashrate gesamt"), st, "{{ value_json.hashrate_gh }}", serverDevice, o => Unit(o, "GH/s"));
        Add("sensor", "server_power", L.T("Leistung gesamt"), st, "{{ value_json.power_w }}", serverDevice, o => Unit(o, "W", "power"));
        Add("sensor", "server_efficiency", L.T("Effizienz gesamt"), st, "{{ value_json.efficiency_jth }}", serverDevice, o => Unit(o, "J/TH"));
        Add("sensor", "server_online", L.T("Miner online"), st, "{{ value_json.online }}", serverDevice, o => o["state_class"] = "measurement");
        Add("sensor", "server_price", "Strompreis", st, "{{ value_json.price_ct }}", serverDevice, o => Unit(o, "ct/kWh"));
        Add("binary_sensor", "server_paused", "Pausiert", st, "{{ value_json.paused }}", serverDevice);
        Add("button", "server_display_refresh", L.T("Anzeige aktualisieren"), st, "", serverDevice, o =>
        {
            o.Remove("state_topic");
            o.Remove("value_template");
            o["command_topic"] = $"{b}/server/display_refresh/press";
        });
        if (_settings.AllowFanControl)
            Add("select", "server_fan_mode", L.T("Zusatzlüfter"), st, "{{ value_json.fan_mode }}", serverDevice, o =>
            {
                o["command_topic"] = $"{b}/server/fan_mode/set";
                o["options"] = new JsonArray(FanModes.Select(m => (JsonNode)m).ToArray());
            });
        else
        {
            list.Add(($"{prefix}/select/bitaxetuner/server_fan_mode/config", "")); // früher freigegeben → wieder entfernen
            Add("sensor", "server_fan_mode_state", L.T("Zusatzlüfter"), st, "{{ value_json.fan_mode }}", serverDevice);
        }
        foreach (var f in s.Fans)
        {
            Add("sensor", $"fan_k{f.Channel}_percent", L.T("Lüfter K{0} {1}", f.Channel, f.Name), st, $"{{{{ value_json.fans.k{f.Channel}.percent }}}}", serverDevice, o => Unit(o, "%"));
            Add("sensor", $"fan_k{f.Channel}_rpm", L.T("Lüfter K{0} Drehzahl", f.Channel), st, $"{{{{ value_json.fans.k{f.Channel}.rpm }}}}", serverDevice, o => Unit(o, "rpm"));
        }
        foreach (var t in s.Sensors)
            Add("sensor", $"temp_{t.Key}", t.Name, st, $"{{{{ value_json.sensors['{t.Key}'] }}}}", serverDevice, o => Unit(o, "°C", "temperature"));

        foreach (var m in s.Miners)
        {
            var device = new JsonObject
            {
                ["identifiers"] = new JsonArray($"bitaxetuner_{m.Key}"),
                ["name"] = m.Name,
                ["manufacturer"] = "Bitaxe / NerdAxe",
                ["model"] = m.Model,
                ["via_device"] = "bitaxetuner_server",
            };
            var mt = $"{b}/miner/{m.Key}/state";
            var id = $"miner_{m.Key}";
            Add("binary_sensor", $"{id}_online", "Online", mt, "{{ value_json.online }}", device, o => o["device_class"] = "connectivity");
            Add("sensor", $"{id}_hashrate", "Hashrate", mt, "{{ value_json.hashrate_gh }}", device, o => Unit(o, "GH/s"));
            Add("sensor", $"{id}_power", "Leistung", mt, "{{ value_json.power_w }}", device, o => Unit(o, "W", "power"));
            Add("sensor", $"{id}_efficiency", "Effizienz", mt, "{{ value_json.efficiency_jth }}", device, o => Unit(o, "J/TH"));
            Add("sensor", $"{id}_temp", "ASIC-Temperatur", mt, "{{ value_json.temp }}", device, o => Unit(o, "°C", "temperature"));
            Add("sensor", $"{id}_vr_temp", "VR-Temperatur", mt, "{{ value_json.vr_temp }}", device, o => Unit(o, "°C", "temperature"));
            Add("sensor", $"{id}_frequency", "Frequenz", mt, "{{ value_json.frequency_mhz }}", device, o => Unit(o, "MHz", "frequency"));
            Add("sensor", $"{id}_voltage", "Kernspannung", mt, "{{ value_json.core_voltage_mv }}", device, o => Unit(o, "mV", "voltage"));
            Add("sensor", $"{id}_best_diff", "Best Diff", mt, "{{ value_json.best_diff }}", device);
            Add("sensor", $"{id}_fan", L.T("VR-Lüfter"), mt, "{{ value_json.fan_percent }}", device, o => Unit(o, "%"));
            Add("sensor", $"{id}_soak", "Dauertest", mt, "{{ value_json.soak }}", device);
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected) await DisconnectCoreAsync();
        _client.Dispose();
        _gate.Dispose();
    }
}
