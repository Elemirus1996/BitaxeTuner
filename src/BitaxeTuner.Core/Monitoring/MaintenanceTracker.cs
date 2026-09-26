namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Merkt sich je Miner, ob gerade absichtlich eingegriffen wird (Tuning, Benchmark, gewollter Neustart).
/// Während eines Wartungsfensters pausiert der Watchdog und Offline-Meldungen werden unterdrückt.
/// </summary>
public sealed class MaintenanceTracker
{
    /// <summary>Nachlauf nach einem Neustart oder dem Ende eines Benchmarks.</summary>
    public static readonly TimeSpan DefaultTail = TimeSpan.FromMinutes(3);

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public MaintenanceTracker(Func<DateTime>? utcNow = null) => _now = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Offenes Wartungsfenster (z. B. ganzer Benchmark); Dispose beendet es mit Nachlauf.</summary>
    public IDisposable Begin(string host, string reason)
    {
        lock (_lock) _open[host] = _open.GetValueOrDefault(host) + 1;
        return new Scope(this, host);
    }

    /// <summary>Wartung bis mindestens jetzt + <paramref name="duration"/> (z. B. nach einem Neustart).</summary>
    public void Extend(string host, TimeSpan duration)
    {
        lock (_lock)
        {
            var until = _now() + duration;
            if (!_until.TryGetValue(host, out var current) || current < until) _until[host] = until;
        }
    }

    public bool IsActive(string host)
    {
        lock (_lock)
        {
            if (_open.GetValueOrDefault(host) > 0) return true;
            return _until.TryGetValue(host, out var until) && until > _now();
        }
    }

    private void End(string host)
    {
        lock (_lock)
        {
            var count = _open.GetValueOrDefault(host) - 1;
            if (count <= 0) _open.Remove(host); else _open[host] = count;
        }
        Extend(host, DefaultTail);
    }

    private sealed class Scope(MaintenanceTracker owner, string host) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(host);
        }
    }
}
