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
    private readonly Dictionary<string, DateTime> _dropSince = new();
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

    /// <summary>
    /// 0.9.12 Hashrate-Einbruch: true, wenn der Miner erreichbar ist, Hashrate liefert (≥ 1 GH/s – sonst greift die
    /// Null-Regel), aber seit <see cref="WatchdogSettings.DropMinutes"/> ohne Unterbrechung mehr als
    /// <see cref="WatchdogSettings.DropPercent"/> % unter <paramref name="normalGh"/> liegt. Gleiche Sperrzeit wie oben.
    /// </summary>
    public bool ShouldRebootForDrop(string host, bool online, double hashrateGh, double? normalGh, WatchdogSettings settings,
        bool inMaintenance = false, DateTime? utcNow = null)
    {
        var limit = normalGh is > 1 ? normalGh.Value * (1 - Math.Clamp(settings.DropPercent, 5, 90) / 100.0) : 0;
        if (!settings.DropEnabled || !online || inMaintenance || hashrateGh < 1 || limit <= 0 || hashrateGh >= limit)
        {
            _dropSince.Remove(host);
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        if (!_dropSince.TryGetValue(host, out var since))
        {
            _dropSince[host] = now;
            return false;
        }
        if (now - since < TimeSpan.FromMinutes(Math.Max(5, settings.DropMinutes))) return false;
        if (_lastReboot.TryGetValue(host, out var last) && now - last < TimeSpan.FromMinutes(Math.Max(5, settings.CooldownMinutes)))
            return false;

        _lastReboot[host] = now;
        _dropSince.Remove(host);
        return true;
    }

    /// <summary>Minuten seit Beginn der Nullphase, für die Anzeige.</summary>
    public double? ZeroMinutes(string host)
        => _zeroSince.TryGetValue(host, out var since) ? (DateTime.UtcNow - since).TotalMinutes : null;
}
