using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.11: Reihenfolge der Miner – gilt überall (Übersicht, E-Paper, Kiosk, Desktop, Berichte).</summary>
public sealed partial class MinerHub
{
    /// <summary>
    /// Miner in die angegebene Reihenfolge bringen (Hosts). Nicht genannte behalten ihre Reihenfolge und folgen danach,
    /// unbekannte Hosts werden ignoriert – so geht beim gleichzeitigen Hinzufügen nichts verloren.
    /// </summary>
    public void ReorderDevices(IEnumerable<string> hosts, string who)
    {
        var order = hosts.Select(h => h.Trim()).ToList();
        int Rank(Config.DeviceConfig d)
        {
            var i = order.FindIndex(h => string.Equals(h, d.Host.Trim(), StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        var before = Config.Devices.Select(d => d.Host).ToList();
        var sorted = Config.Devices.Select((d, i) => (d, i)).OrderBy(x => Rank(x.d)).ThenBy(x => x.i).Select(x => x.d).ToList();
        if (sorted.Select(d => d.Host).SequenceEqual(before)) return;
        Config.Devices.Clear();
        Config.Devices.AddRange(sorted);
        Config.Save();
        SyncDevices();
        LogEvent(null, EventCategories.Settings, L.T("Reihenfolge der Miner geändert ({0}): {1}", who, string.Join(", ", sorted.Select(d => d.Name))));
    }
}
