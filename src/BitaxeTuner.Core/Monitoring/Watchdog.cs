using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Entscheidet, wann ein Miner automatisch neu gestartet werden soll: erreichbar,
/// aber seit <see cref="WatchdogSettings.ZeroHashMinutes"/> unter 1 GH/s.
/// Nicht erreichbare Miner lassen sich über die API ohnehin nicht neu starten.
/// Während einer Tuning-Wartung (<paramref name="inMaintenance"/>) greift der Watchdog nie,
/// und die Nullphase beginnt danach von vorn.
/// </summary>
public sealed class Watchdog
{
    private readonly Dictionary<string, DateTime> _zeroSince = new();
    private readonly Dictionary<string, DateTime> _lastReboot = new();

    /// <summary>Liefert true, wenn jetzt neu gestartet werden soll. Merkt sich den Zeitpunkt.</summary>
    public bool ShouldReboot(string host, bool online, double hashrateGh, WatchdogSettings settings, bool inMaintenance = false)
    {
        if (!settings.Enabled || !online || hashrateGh >= 1 || inMaintenance)
        {
            _zeroSince.Remove(host);
            return false;
        }

        var now = DateTime.UtcNow;
        if (!_zeroSince.TryGetValue(host, out var since))
        {
            _zeroSince[host] = now;
            return false;
        }

        if (now - since < TimeSpan.FromMinutes(Math.Max(1, settings.ZeroHashMinutes))) return false;

        if (_lastReboot.TryGetValue(host, out var last) &&
            now - last < TimeSpan.FromMinutes(Math.Max(5, settings.CooldownMinutes)))
            return false;

        _lastReboot[host] = now;
        _zeroSince.Remove(host);
        return true;
    }

    /// <summary>Minuten seit Beginn der Nullphase, für die Anzeige.</summary>
    public double? ZeroMinutes(string host)
        => _zeroSince.TryGetValue(host, out var since) ? (DateTime.UtcNow - since).TotalMinutes : null;
}
