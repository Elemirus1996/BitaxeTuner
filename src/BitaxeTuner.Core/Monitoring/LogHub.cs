namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Verteilt die Live-Logs eines Miners an beliebig viele Abonnenten (Log-Tab, Log-Alarme)
/// über GENAU EINE Verbindung. Die Verbindung läuft nur, solange mindestens ein Abonnent da ist –
/// der Miner hat nur wenige WebSocket-Plätze, die AxeOS-Weboberfläche braucht auch einen.
/// </summary>
public sealed class LogHub : IDisposable
{
    private readonly Func<IMinerLogSource> _createSource;
    private readonly object _lock = new();
    private readonly List<Subscriber> _subscribers = new();
    private CancellationTokenSource? _cts;
    private string _status = "Aus";

    public LogHub(Func<IMinerLogSource> createSource) => _createSource = createSource;

    public string Status { get { lock (_lock) return _status; } }
    public bool IsRunning { get { lock (_lock) return _cts is not null; } }
    public int SubscriberCount { get { lock (_lock) return _subscribers.Count; } }

    /// <summary>Anmelden; Dispose meldet ab. Callbacks kommen auf einem Hintergrund-Thread.</summary>
    public IDisposable Subscribe(Action<LogLine> onLine, Action<string>? onStatus = null)
    {
        var sub = new Subscriber(this, onLine, onStatus);
        string status;
        lock (_lock)
        {
            _subscribers.Add(sub);
            status = _status;
            if (_cts is null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var source = _createSource();
                _ = Task.Run(() => source.RunAsync(Publish, SetStatus, token));
            }
        }
        onStatus?.Invoke(status);
        return sub;
    }

    private void Unsubscribe(Subscriber sub)
    {
        lock (_lock)
        {
            _subscribers.Remove(sub);
            if (_subscribers.Count > 0 || _cts is null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            _status = "Aus";
        }
    }

    private void Publish(LogLine line)
    {
        Subscriber[] subs;
        lock (_lock) subs = _subscribers.ToArray();
        foreach (var s in subs)
        {
            try { s.OnLine(line); } catch { /* ein fehlerhafter Abonnent darf die anderen nicht stören */ }
        }
    }

    private void SetStatus(string status)
    {
        Subscriber[] subs;
        lock (_lock)
        {
            if (_cts is null) return; // nach dem Stoppen eintreffende Meldungen ignorieren
            _status = status;
            subs = _subscribers.ToArray();
        }
        foreach (var s in subs)
        {
            try { s.OnStatus?.Invoke(status); } catch { /* egal */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _subscribers.Clear();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private sealed class Subscriber(LogHub hub, Action<LogLine> onLine, Action<string>? onStatus) : IDisposable
    {
        private int _disposed;
        public Action<LogLine> OnLine { get; } = onLine;
        public Action<string>? OnStatus { get; } = onStatus;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) hub.Unsubscribe(this);
        }
    }
}
