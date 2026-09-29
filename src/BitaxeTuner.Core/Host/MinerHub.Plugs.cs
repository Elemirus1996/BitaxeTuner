using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Plugs;

namespace BitaxeTuner.Core.Host;

/// <summary>Zustand eines Smart Plugs für Oberfläche, API und MQTT. PowerW null = keine aktuelle Messung.</summary>
public sealed record PlugStatus(string Id, string Name, string Host, string Role, IReadOnlyList<string> Miners, bool Online,
    double? PowerW, double? EnergyWh, double? Voltage, double? Current, string? Model, string? Error, DateTime? Updated,
    double? MinerPowerW)
{
    /// <summary>Steckdose minus AxeOS der zugeordneten Miner (Netzteil, Kabel, Zusatzlüfter).</summary>
    public double? OverheadW => Role == "miners" && PowerW is { } p && MinerPowerW is { } m ? p - m : null;
}

/// <summary>
/// Smart Plugs (Shelly): eigener Takt, nur lesend. Messwerte gehen in history.db (plug_samples) und in die
/// Energiebilanz für Kosten/Effizienz. Ein Plug gilt als offline, wenn die letzte Messung älter als 3 Takte ist.
/// </summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, (IPlugClient Client, string Key)> _plugClients = new();
    private readonly Dictionary<string, (PlugReading Reading, DateTime At, string? Model)> _plugReadings = new();
    private readonly Dictionary<string, string> _plugErrors = new();
    private bool _plugBusy;
    private readonly Dictionary<string, DateTime> _plugFailSince = new();
    private readonly HashSet<string> _plugOfflineNotified = [];
    private DateTime _plugOverheadCheck = DateTime.MinValue;

    /// <summary>So lange darf ein Plug schweigen, bevor eine Meldung kommt (WLAN-Aussetzer sind normal).</summary>
    public static readonly TimeSpan PlugOfflineAfter = TimeSpan.FromMinutes(5);

    /// <summary>Nur für Tests: eigener Plug-Client je Konfiguration.</summary>
    internal Func<SmartPlugConfig, IPlugClient?>? PlugClientFactory { get; set; }

    public event Action? PlugsUpdated;

    internal TimeSpan PlugInterval => TimeSpan.FromSeconds(Math.Clamp(Config.Plugs.IntervalSeconds, 5, 300));

    /// <summary>Nach Änderung der Plug-Einstellungen: Verbindungen neu aufbauen und sofort messen.</summary>
    public async Task ApplyPlugSettingsAsync()
    {
        foreach (var id in _plugClients.Keys.ToList()) ClosePlug(id);
        foreach (var id in _plugReadings.Keys.Where(id => Config.Plugs.Items.All(p => p.Id != id)).ToList()) _plugReadings.Remove(id);
        _plugErrors.Clear();
        await PlugTickAsync();
    }

    private void ClosePlug(string id)
    {
        if (_plugClients.Remove(id, out var c)) c.Client.Dispose();
    }

    internal async Task PlugTickAsync()
    {
        if (_plugBusy || _paused || Config.Plugs.Items.Count == 0) return;
        _plugBusy = true;
        try
        {
            var now = Options.Clock?.Invoke() ?? DateTime.Now;
            await Task.WhenAll(Config.Plugs.Items.Select(p => ReadPlugAsync(p, now)));
            CheckPlugHealth(now);
            PlugsUpdated?.Invoke();
        }
        finally { _plugBusy = false; }
    }

    private async Task ReadPlugAsync(SmartPlugConfig plug, DateTime now)
    {
        try
        {
            var client = PlugClient(plug);
            var reading = await client.ReadAsync(plug.Channel);
            var model = _plugReadings.TryGetValue(plug.Id, out var old) ? old.Model : null;
            if (model is null)
                try { model = (await client.IdentifyAsync()).Model; } catch { model = null; }
            _plugReadings[plug.Id] = (reading, now, model);
            _plugErrors.Remove(plug.Id);
            History?.AddPlugSample(plug.Id, now, reading.PowerW, reading.EnergyWh);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _plugErrors[plug.Id] = ex is TaskCanceledException ? L.T("keine Antwort") : ex.Message;
            ClosePlug(plug.Id); // beim nächsten Takt neu verbinden (z. B. geänderte IP, Neustart des Plugs)
        }
    }

    /// <summary>Meldungen: Plug antwortet nicht (nach 5 min) bzw. wieder; stündlich Mehrverbrauch gegenüber der Vorwoche.</summary>
    private void CheckPlugHealth(DateTime now)
    {
        var n = Config.Notifications;
        foreach (var plug in Config.Plugs.Items)
        {
            var key = $"plug-offline:{plug.Id}";
            if (_plugErrors.TryGetValue(plug.Id, out var error))
            {
                var since = _plugFailSince.TryGetValue(plug.Id, out var s) ? s : _plugFailSince[plug.Id] = now;
                if (now - since >= PlugOfflineAfter && n.OnPlugs && _plugOfflineNotified.Add(plug.Id))
                    _ = Notify.SendAsync(key, L.T("Smart Plug {0} nicht erreichbar", plug.Name),
                        L.T("{0} antwortet seit {1} min nicht ({2}). Kosten rechnen so lange mit AxeOS-Werten.", plug.Name,
                            (int)(now - since).TotalMinutes, error), NotifyPriority.Normal, TimeSpan.FromHours(6));
                continue;
            }
            _plugFailSince.Remove(plug.Id);
            if (_plugOfflineNotified.Remove(plug.Id))
            {
                Notify.Reset(key);
                if (n.OnPlugs)
                    _ = Notify.SendAsync($"plug-online:{plug.Id}", L.T("Smart Plug {0} wieder erreichbar", plug.Name),
                        L.T("{0} misst wieder.", plug.Name), NotifyPriority.Low, TimeSpan.FromMinutes(1));
            }
        }

        if (now < _plugOverheadCheck || History is not { } db || !n.OnPlugs) return;
        _plugOverheadCheck = now.AddHours(1);
        foreach (var plug in Config.Plugs.Items.Where(p => p.Role is "miners" or "total"))
        {
            var hosts = plug.Role == "total" ? [Monitoring.HistoryStore.AggregateHost] : plug.Miners;
            double? Axe(DateTime from, DateTime to)
            {
                var avgs = hosts.Select(h => db.Average(h, from, to)).ToList();
                return avgs.All(a => a is not null) ? avgs.Sum(a => a!.Power) : null;
            }
            var recentFrom = now.AddHours(-24);
            var baseFrom = now.AddDays(-8);
            // Mindestens die Hälfte beider Zeiträume gemessen, sonst keine Aussage
            if (db.AveragePlugPower(plug.Id, recentFrom, now) is not { Minutes: >= 720 } pr ||
                db.AveragePlugPower(plug.Id, baseFrom, recentFrom) is not { Minutes: >= 5040 } pb ||
                Axe(recentFrom, now) is not { } ar || Axe(baseFrom, recentFrom) is not { } ab ||
                PlugHealth.Check(pr.PowerW, ar, pb.PowerW, ab) is not { } change)
                continue;
            _ = Notify.SendAsync($"plug-overhead:{plug.Id}", L.T("{0}: Mehrverbrauch gestiegen", plug.Name),
                L.T("An der Steckdose {0} W mehr als AxeOS ({1} %), in der Vorwoche {2} W ({3} %). Netzteil, Kabel und Zusatzverbraucher prüfen.",
                    change.RecentW.ToString("0.0", L.Culture), change.RecentPercent.ToString("0", L.Culture),
                    change.BaseW.ToString("0.0", L.Culture), change.BasePercent.ToString("0", L.Culture)),
                NotifyPriority.Normal, TimeSpan.FromDays(1));
        }
    }

    private IPlugClient PlugClient(SmartPlugConfig plug)
    {
        var password = Secrets.Get(SmartPlugConfig.SecretKey(plug.Id));
        var key = $"{plug.Host}|{plug.User}|{password?.GetHashCode()}";
        if (_plugClients.TryGetValue(plug.Id, out var c) && c.Key == key) return c.Client;
        ClosePlug(plug.Id);
        var client = PlugClientFactory?.Invoke(plug)
                     ?? (SimulatedPlugClient.IsSimAddress(plug.Host)
                         ? new SimulatedPlugClient(() => plug.Role switch
                         {
                             "miners" => MinerPowerSum(plug.Miners),
                             "total" => MinerPowerSum(Devices.Select(d => d.Host)),
                             _ => 6,
                         })
                         : new ShellyClient(plug.Host, plug.User, password));
        _plugClients[plug.Id] = (client, key);
        return client;
    }

    private double MinerPowerSum(IEnumerable<string> hosts) =>
        hosts.Sum(h => Device(h)?.Info?.PowerW ?? 0);

    /// <summary>Aktuelle Messung eines Plugs, null wenn älter als 3 Takte oder nie gemessen.</summary>
    private PlugReading? FreshReading(string id, DateTime now) =>
        _plugReadings.TryGetValue(id, out var r) && now - r.At <= PlugInterval * 3 ? r.Reading : null;

    public IReadOnlyList<PlugStatus> PlugStatuses()
    {
        var now = Options.Clock?.Invoke() ?? DateTime.Now;
        return Config.Plugs.Items.Select(p =>
        {
            var fresh = FreshReading(p.Id, now);
            _plugReadings.TryGetValue(p.Id, out var last);
            var miners = p.Role == "miners" ? p.Miners.Where(h => Device(h)?.Info is not null).ToList() : [];
            return new PlugStatus(p.Id, p.Name, p.Host, p.Role, p.Miners, fresh is not null,
                fresh?.PowerW, fresh?.EnergyWh, fresh?.Voltage, fresh?.Current, last.Model, _plugErrors.GetValueOrDefault(p.Id),
                last.At == default ? null : last.At, miners.Count > 0 ? MinerPowerSum(miners) : null);
        }).ToList();
    }

    /// <summary>Aktuelle Energiebilanz (AxeOS + Plugs) für Übersicht, MQTT und Kosten.</summary>
    public EnergyBalance CurrentEnergy()
    {
        var now = Options.Clock?.Invoke() ?? DateTime.Now;
        var miners = Devices.Where(d => d.Info is not null)
            .ToDictionary(d => d.Host, d => d.Info!.PowerW, StringComparer.OrdinalIgnoreCase);
        return EnergyBalance.Compute(Config.Plugs, miners, p => FreshReading(p.Id, now)?.PowerW);
    }
}
