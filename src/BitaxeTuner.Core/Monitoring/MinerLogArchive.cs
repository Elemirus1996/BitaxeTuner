using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// 0.9.11: Miner-Logs eine einstellbare Zeit (Standard 48 h) speichern – je Miner einschaltbar
/// (<see cref="DeviceConfig.LogArchive"/>). Liest über den gemeinsamen <see cref="LogHub"/> mit (dieselbe Verbindung wie
/// die Log-Alarme, also kein zusätzlicher Log-Platz am Miner). Zeilen werden gesammelt und gebündelt in history.db
/// geschrieben (<see cref="Flush"/>, etwa alle 30 s) – das schont SD-Karten.
/// </summary>
public sealed class MinerLogArchive : IDisposable
{
    /// <summary>Höchstens so viele Zeilen warten im Speicher; bei einem Fehler der Datenbank gehen die ältesten verloren.</summary>
    public const int MaxBuffered = 20000;

    /// <summary>Längere Nachrichten werden gekürzt.</summary>
    public const int MaxMessageLength = 1000;

    private readonly Func<HistoryStore?> _history;
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Host, LogLine Line)> _buffer = [];
    private int _dropped;

    public MinerLogArchive(Func<HistoryStore?> history) => _history = history;

    /// <summary>Miner, deren Logs gerade gespeichert werden.</summary>
    public IReadOnlyCollection<string> ActiveHosts => _subscriptions.Keys;

    /// <summary>Seit dem Start verworfene Zeilen (Puffer voll, z. B. Datenbank nicht verfügbar).</summary>
    public int Dropped => _dropped;

    /// <summary>An Geräteliste und Einstellungen angleichen.</summary>
    public void Sync(IEnumerable<(DeviceConfig Device, MinerConnection Connection)> devices)
    {
        var wanted = devices.Where(d => d.Device.LogArchive).ToDictionary(d => d.Device.Host, StringComparer.OrdinalIgnoreCase);
        foreach (var host in _subscriptions.Keys.Where(h => !wanted.ContainsKey(h)).ToList())
        {
            _subscriptions[host].Dispose();
            _subscriptions.Remove(host);
        }
        foreach (var (host, d) in wanted)
        {
            if (_subscriptions.ContainsKey(host)) continue;
            _subscriptions[host] = d.Connection.Logs.Subscribe(line => Add(host, line));
        }
    }

    /// <summary>Zeile vormerken (eigene Markierungen der App werden nicht gespeichert).</summary>
    internal void Add(string host, LogLine line)
    {
        if (line.Level == LogLevel.App) return;
        lock (_buffer)
        {
            _buffer.Add((host, line));
            if (_buffer.Count > MaxBuffered)
            {
                var over = _buffer.Count - MaxBuffered;
                _buffer.RemoveRange(0, over);
                _dropped += over;
            }
        }
    }

    /// <summary>Gesammelte Zeilen in einer Transaktion schreiben. Liefert die Anzahl geschriebener Zeilen.</summary>
    public int Flush()
    {
        if (_history() is not { } db) return 0;
        List<(string Host, LogLine Line)> batch;
        lock (_buffer)
        {
            if (_buffer.Count == 0) return 0;
            batch = [.. _buffer];
            _buffer.Clear();
        }
        try
        {
            db.AddMinerLog(batch.Select(x => (x.Host, x.Line with
            {
                Message = x.Line.Message.Length > MaxMessageLength ? x.Line.Message[..MaxMessageLength] + "…" : x.Line.Message,
            })));
            return batch.Count;
        }
        catch
        {
            // Datenbank gerade nicht verfügbar: zurück in den Puffer (begrenzt), beim nächsten Mal erneut
            lock (_buffer)
            {
                _buffer.InsertRange(0, batch);
                if (_buffer.Count > MaxBuffered)
                {
                    var over = _buffer.Count - MaxBuffered;
                    _buffer.RemoveRange(0, over);
                    _dropped += over;
                }
            }
            return 0;
        }
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions.Values) s.Dispose();
        _subscriptions.Clear();
        Flush();
    }
}
