using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Frühwarnung für Verschleiß und Kühlung: Werte alle 10 min sichern, einmal am Tag auswerten, Hinweis per Push.</summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, DateTime> _lastHealthWrite = new();
    private DateTime _healthCheckedDay = DateTime.MinValue;

    /// <summary>Alle 10 min je Miner: Lüfter, VR-Temperatur, Share-Zähler (nur echte Miner, nur online).</summary>
    private void RecordHealth(DateTime now)
    {
        if (History is null) return;
        foreach (var s in States)
        {
            var host = s.Config.Host;
            if (IsSimulated(host) || !s.Online || s.Info is not { } i) continue;
            if (_lastHealthWrite.TryGetValue(host, out var last) && now - last < TimeSpan.FromMinutes(10)) continue;
            _lastHealthWrite[host] = now;
            try { History.AddHealthSample(host, now, i.fanrpm, i.fanspeed, i.vrTemp, i.sharesAccepted, i.sharesRejected); }
            catch { /* nicht kritisch */ }
        }
    }

    /// <summary>Hinweise und verglichene Zeiträume eines Miners (für Oberfläche und API).</summary>
    public (List<HealthFinding> Findings, HealthWindow Recent, HealthWindow Base) HealthOf(string host, DateTime now)
    {
        if (History is null) return ([], Empty, Empty);
        var (recent, @base) = HealthCheck.Windows(History, host, now);
        var changed = History.QueryTuningEvents(host, now - HealthCheck.Recent - HealthCheck.Base, now).Count > 0;
        return (HealthCheck.Evaluate(recent, @base, changed), recent, @base);
    }

    private static readonly HealthWindow Empty = new(null, null, null, null, null, null);

    internal void CheckHealthWarningsForTest(DateTime now) => CheckHealthWarnings(now);

    /// <summary>Einmal am Tag: neue Hinweise melden (je Hinweis und Miner höchstens einmal pro Woche).</summary>
    private void CheckHealthWarnings(DateTime now)
    {
        if (History is null || _healthCheckedDay == now.Date || now.Hour < 9) return;
        _healthCheckedDay = now.Date;
        if (!Config.Notifications.Wants(NotifyCategory.Health)) return;
        foreach (var d in Devices.Where(d => !d.IsSimulated))
        {
            List<HealthFinding> findings;
            try { findings = HealthOf(d.Host, now).Findings; }
            catch { continue; }
            foreach (var f in findings)
                _ = Notify.SendAsync($"health:{d.Host}:{f.Code}", L.T("{0}: {1}", d.Title, f.Title), f.Text,
                    NotifyPriority.Normal, TimeSpan.FromDays(7), NotifyCategory.Health, d.Host);
        }
    }
}
