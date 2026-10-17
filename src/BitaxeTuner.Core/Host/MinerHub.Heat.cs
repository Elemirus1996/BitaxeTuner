using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Ein Fühler, der für die Heiz-Automatik wählbar ist.</summary>
public sealed record RoomSensor(string Key, string Name, double? Temp);

/// <summary>
/// 0.9.21 Heiz-Automatik („Miner als Heizung“): Raumtemperatur von einem Temperaturfühler am Pico („pico:&lt;Id&gt;“) oder aus
/// Home Assistant („ha:sensor.name“, Adresse und Token wie bei der PV-Anlage). Home Assistant wird je Sensor höchstens jede
/// Minute gelesen; ein Wert gilt 10 Minuten. Geschaltet wird nur zwischen bestätigten Voreinstellungen (Automatik-Regel).
/// </summary>
public sealed partial class MinerHub
{
    private static readonly TimeSpan RoomTempMaxAge = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (double Temp, DateTime At)> _roomTemps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _roomTempErrors = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _roomTempsPolledAt = DateTime.MinValue;
    private bool _roomTempsBusy;

    /// <summary>Raumtemperatur zum Fühler-Schlüssel (null = unbekannt oder zu alt).</summary>
    public double? RoomTemp(string key, DateTime now)
    {
        if (key.StartsWith("pico:", StringComparison.Ordinal))
            return (FanStatus.Sensors ?? []).FirstOrDefault(s => s.Id == key[5..])?.Temp;
        if (key.StartsWith("ha:", StringComparison.Ordinal))
            return _roomTemps.TryGetValue(key, out var v) && now - v.At <= RoomTempMaxAge ? v.Temp : null;
        return null;
    }

    /// <summary>Wählbare Fühler: Temperaturfühler am Pico; Home-Assistant-Sensoren, die schon benutzt werden.</summary>
    public IReadOnlyList<RoomSensor> RoomSensors(DateTime now)
    {
        var list = (FanStatus.Sensors ?? []).Select(s => new RoomSensor("pico:" + s.Id, L.T("Fühler {0} (Pico)", s.Name), s.Temp)).ToList();
        foreach (var key in HeatSensorsInUse().Where(k => k.StartsWith("ha:", StringComparison.Ordinal)))
            list.Add(new RoomSensor(key, L.T("{0} (Home Assistant)", key[3..]), RoomTemp(key, now)));
        return list;
    }

    private IEnumerable<string> HeatSensorsInUse() =>
        Config.Devices.Select(d => d.Schedule).Concat(Config.GroupSchedules.Select(g => g.Schedule))
            .Where(s => s.Enabled && s.Mode == "heat" && !string.IsNullOrWhiteSpace(s.HeatSensor))
            .Select(s => s.HeatSensor.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Letzter Lesefehler eines Home-Assistant-Fühlers (null = in Ordnung).</summary>
    public string? RoomTempError(string key) => _roomTempErrors.TryGetValue(key, out var e) ? e : null;

    /// <summary>Aus dem Abfragetakt: benutzte Home-Assistant-Fühler höchstens jede Minute lesen.</summary>
    private void TickRoomTemps(DateTime now)
    {
        if (_roomTempsBusy || now - _roomTempsPolledAt < TimeSpan.FromMinutes(1)) return;
        if (!Options.OnlineChecks && SolarHttpHandler is null) return;
        var keys = HeatSensorsInUse().Where(k => k.StartsWith("ha:", StringComparison.Ordinal)).ToList();
        if (keys.Count == 0) return;
        _roomTempsPolledAt = now;
        _ = PollRoomTempsAsync(keys, now);
    }

    internal async Task PollRoomTempsAsync(IReadOnlyList<string> keys, DateTime now)
    {
        _roomTempsBusy = true;
        try
        {
            foreach (var key in keys)
            {
                try
                {
                    var s = new SolarSettings { Source = "homeassistant", HaUrl = Config.Solar.HaUrl, HaEntity = key[3..] };
                    _roomTemps[key] = (await ReadHomeAssistantAsync(s), now);
                    _roomTempErrors.Remove(key);
                }
                catch (Exception ex) when (ex is LocalizedException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    _roomTempErrors[key] = ex.Message;
                }
            }
        }
        finally
        {
            _roomTempsBusy = false;
        }
    }
}
