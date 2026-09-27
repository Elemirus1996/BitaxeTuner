using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Integrations;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// Home Assistant / MQTT: eigener Takt (läuft auch, wenn der Server pausiert ist), Befehle aus Home Assistant
/// werden im Hub-Kontext ausgeführt. Frequenz und Spannung sind über MQTT nicht änderbar.
/// </summary>
public sealed partial class MinerHub
{
    private MqttBridge? _mqtt;
    private CancellationTokenSource? _mqttLoop;

    public bool MqttConnected => _mqtt?.Connected == true;
    public string? MqttError => _mqtt?.LastError;

    /// <summary>Nach Änderung der MQTT-Einstellungen (und beim Start): verbinden bzw. trennen.</summary>
    public async Task ApplyMqttSettingsAsync()
    {
        _mqttLoop?.Cancel();
        _mqttLoop = null;
        var s = Config.Mqtt;
        if (!s.Enabled)
        {
            if (_mqtt is { } old)
            {
                _mqtt = null;
                await old.DisposeAsync();
            }
            return;
        }
        if (_mqtt is null)
        {
            _mqtt = new MqttBridge();
            _mqtt.Command += (cmd, payload) => InvokeAsync(() => HandleMqttCommandAsync(cmd, payload));
        }
        try
        {
            await _mqtt.ApplyAsync(s, Secrets.Get(SecretStore.MqttPassword));
            await MqttTickAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            RaiseStatus(false, ex.Message);
        }
        _mqttLoop = new CancellationTokenSource();
        _ = RunLoopAsync(() => TimeSpan.FromSeconds(Math.Clamp(Config.Mqtt.IntervalSeconds, 5, 3600)), MqttTickAsync, _mqttLoop.Token);
    }

    private async Task HandleMqttCommandAsync(string command, string payload)
    {
        switch (command)
        {
            case "fan_mode" when Config.Mqtt.AllowFanControl:
                var mode = payload switch { "100 %" => FanOverride.Full, "Aus" => FanOverride.Off, _ => FanOverride.None };
                await SetFanOverrideAsync(mode, "Home Assistant");
                break;
            case "display_refresh":
                RequestDisplayRefresh();
                break;
        }
        await MqttTickAsync();
    }

    internal async Task MqttTickAsync()
    {
        if (_mqtt is not { } bridge || !Config.Mqtt.Enabled) return;
        await bridge.PublishAsync(BuildMqttSnapshot(), Secrets.Get(SecretStore.MqttPassword));
    }

    /// <summary>Aktueller Stand für MQTT (im Hub-Kontext).</summary>
    public MqttSnapshot BuildMqttSnapshot()
    {
        var fans = FanStatus.Channels;
        var miners = Devices.Select(d =>
        {
            var i = d.Info;
            var fan = fans.FirstOrDefault(c => c.Role == "miner" && string.Equals(c.MinerHost?.Trim(), d.Host.Trim(), StringComparison.OrdinalIgnoreCase));
            var best = BestDiffs.Where(r => r.Host == d.Host).OrderByDescending(r => r.Value).FirstOrDefault()?.Raw;
            return new MqttMiner(MqttBridge.Key(d.Host), d.Title, d.Profile.Name, i is not null,
                i?.HashRateGh, i?.PowerW, i is { HashRateGh: > 1 } x ? x.PowerW / (x.HashRateGh / 1000) : null,
                i?.ChipTempC, i?.VrTempC, i?.FrequencyMhz, i?.CoreVoltageMv, best, fan?.Percent,
                d.Config.Soak is not null ? d.SoakStatus : "–");
        }).ToList();
        var online = miners.Where(m => m.Online).ToList();
        var mode = FanOverride switch { FanOverride.Full => "100 %", FanOverride.Off => "Aus", _ => "Automatik" };
        return new MqttSnapshot(online.Sum(m => m.HashrateGh ?? 0), online.Sum(m => m.PowerW ?? 0), online.Count, miners.Count,
            Prices.PriceAt(DateTime.UtcNow), IsPaused, mode, miners,
            fans.Select(c => new MqttFan(c.Channel, c.Name, c.Percent, c.Rpm)).ToList(),
            (FanStatus.Sensors ?? []).Select(s => new MqttSensor(MqttBridge.Key(s.Id), s.Name, s.Temp)).ToList());
    }
}
