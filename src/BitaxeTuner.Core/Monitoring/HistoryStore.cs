using System.IO;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using Microsoft.Data.Sqlite;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Ein Eintrag der Best-Diff-Rekordliste.</summary>
public sealed record BestDiffRecord(string Host, string Coin, double Value, string Raw, DateTime AchievedAt);

/// <summary>Ergebnis eines Dauertests (Tabelle soak_results).</summary>
public sealed record SoakResultRecord(string Host, DateTime Started, DateTime Ended, int FrequencyMhz, int CoreVoltageMv, bool Passed,
    double? Ratio, string Message);

/// <summary>Eine protokollierte Tuning-Änderung (Tabelle tuning_events).</summary>
public sealed record TuningEvent(
    string Host, DateTime Time, TuningSource Source,
    int? OldFrequencyMhz, int? OldCoreVoltageMv, int NewFrequencyMhz, int NewCoreVoltageMv, string? Note)
{
    public string SourceText => Source switch
    {
        TuningSource.Manual => "manuell",
        TuningSource.Benchmark => "Benchmark",
        TuningSource.Restore => "Wiederherstellung",
        TuningSource.Automatic => "Automatik",
        _ => Source.ToString(),
    };

    public string ChangeText =>
        $"{OldFrequencyMhz?.ToString() ?? "?"}→{NewFrequencyMhz} MHz / {OldCoreVoltageMv?.ToString() ?? "?"}→{NewCoreVoltageMv} mV" +
        (string.IsNullOrWhiteSpace(Note) ? "" : $" ({Note})");
}

/// <summary>Mittelwerte eines Zeitfensters (nur Online-Minuten).</summary>
public sealed record WindowAverage(int Minutes, double HashRateGh, double Temp, double Power)
{
    public double? EfficiencyJth => HashRateGh > 0 ? Power / (HashRateGh / 1000.0) : null;
}

/// <summary>
/// Dauerhafte Verlaufsdaten in SQLite unter &lt;Datenordner&gt;\history.db (Standard %AppData%\BitaxeMonitor).
///
/// Pro Miner höchstens ein Datenpunkt je Minute, zusätzlich ein Summen-Datenpunkt
/// unter dem Host "*". Offline-Minuten werden mit online=0 gespeichert, daraus
/// ergibt sich die Verfügbarkeit.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const string AggregateHost = "*";

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private bool _inBatch;

    /// <summary>
    /// Plug-Messwerte der laufenden Minute (Audit E1): Plugs liefern alle paar Sekunden, gespeichert wird je Minute nur der
    /// letzte Wert – also erst beim Minutenwechsel schreiben statt bei jedem Messwert. Vor jedem Lesen, jeder Sicherung und
    /// beim Schließen wird der Puffer geschrieben, sodass Abfragen nichts fehlt.
    /// </summary>
    private readonly Dictionary<string, (long Ts, double PowerW, double? EnergyWh)> _pendingPlugs = new();

    public HistoryStore(string? path = null)
    {
        var file = path ?? DataPaths.HistoryFile;
        FilePath = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        _db = new SqliteConnection($"Data Source={file}");
        _db.Open();

        Execute("PRAGMA journal_mode=WAL;");
        // Audit E1: im WAL-Modus genügt NORMAL – kein fsync je Commit (schont SD-Karte/SSD), bei Stromausfall gehen
        // höchstens die letzten Sekunden verloren, die Datei bleibt aber konsistent.
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS samples (
                host     TEXT    NOT NULL,
                ts       INTEGER NOT NULL,
                hashrate REAL    NOT NULL,
                temp     REAL    NOT NULL,
                power    REAL    NOT NULL,
                online   INTEGER NOT NULL,
                PRIMARY KEY (host, ts)
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS best_diff (
                host        TEXT    NOT NULL,
                coin        TEXT    NOT NULL,
                value       REAL    NOT NULL,
                raw         TEXT    NOT NULL,
                achieved_at INTEGER NOT NULL,
                PRIMARY KEY (host, coin)
            );
            """);
        // Neu durch die Zusammenführung: rein additiv, bestehende Tabellen bleiben unverändert.
        Execute("""
            CREATE TABLE IF NOT EXISTS tuning_events (
                host     TEXT    NOT NULL,
                ts       INTEGER NOT NULL,
                source   TEXT    NOT NULL,
                old_freq INTEGER,
                old_mv   INTEGER,
                new_freq INTEGER NOT NULL,
                new_mv   INTEGER NOT NULL,
                note     TEXT,
                PRIMARY KEY (host, ts, source)
            );
            """);
        // 0.4.0: Dauertest-Ergebnisse für den Effizienz-Ratgeber – rein additiv
        Execute("""
            CREATE TABLE IF NOT EXISTS soak_results (
                host    TEXT    NOT NULL,
                started INTEGER NOT NULL,
                ended   INTEGER NOT NULL,
                freq    INTEGER NOT NULL,
                mv      INTEGER NOT NULL,
                passed  INTEGER NOT NULL,
                ratio   REAL,
                message TEXT    NOT NULL,
                PRIMARY KEY (host, started)
            );
            """);
        // 0.7.0: Werte für die Gesundheits-Frühwarnung (alle 10 min): Lüfter, VR-Temperatur, Share-Zähler – rein additiv
        Execute("""
            CREATE TABLE IF NOT EXISTS health_samples (
                host     TEXT    NOT NULL,
                ts       INTEGER NOT NULL,
                fan_rpm  INTEGER NOT NULL,
                fan_pct  REAL    NOT NULL,
                vr_temp  REAL    NOT NULL,
                accepted REAL    NOT NULL,
                rejected REAL    NOT NULL,
                PRIMARY KEY (host, ts)
            );
            """);
        // 0.7.0: abgeschlossene Monatsberichte (JSON), damit Jahresberichte über die Aufbewahrungszeit
        // der Minutenwerte hinaus möglich sind – rein additiv, wird nie bereinigt
        Execute("""
            CREATE TABLE IF NOT EXISTS period_reports (
                period  TEXT    NOT NULL PRIMARY KEY,
                json    TEXT    NOT NULL,
                created INTEGER NOT NULL
            );
            """);
        // 0.6.1: Strompreise der gewählten Quelle (ct/kWh je Zeitraum, UTC) für die Kostenrechnung – rein additiv
        Execute("""
            CREATE TABLE IF NOT EXISTS prices (
                ts      INTEGER NOT NULL PRIMARY KEY,
                ends    INTEGER NOT NULL,
                ct      REAL    NOT NULL
            );
            """);
        // 0.6.1: Smart-Plug-Messwerte (Minutenmittel an der Steckdose) – rein additiv
        Execute("""
            CREATE TABLE IF NOT EXISTS plug_samples (
                plug    TEXT    NOT NULL,
                ts      INTEGER NOT NULL,
                power   REAL    NOT NULL,
                energy  REAL,
                PRIMARY KEY (plug, ts)
            );
            """);
        // 0.9.0: dauerhaftes Protokoll (Tuning, Benchmark, Lüfter, Verbindung …) – rein additiv
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                ts       INTEGER NOT NULL,
                host     TEXT,
                category TEXT    NOT NULL,
                message  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events (ts);
            """);
    }

    /// <summary>
    /// Mehrere Schreibvorgänge in einer Transaktion (Audit E1): ein Commit je Takt statt einer je Zeile. Wirft
    /// <paramref name="write"/> eine Ausnahme, wird alles zurückgenommen und die Ausnahme weitergereicht.
    /// </summary>
    public void Batch(Action write)
    {
        lock (_lock)
        {
            if (_inBatch) { write(); return; }
            Execute("BEGIN;");
            _inBatch = true;
            try
            {
                write();
                Execute("COMMIT;");
            }
            catch
            {
                try { Execute("ROLLBACK;"); } catch { /* Transaktion bereits beendet */ }
                throw;
            }
            finally { _inBatch = false; }
        }
    }

    // ---------- Protokoll ----------

    public void AddEvent(string? host, string category, string message, DateTime time)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events (ts, host, category, message) VALUES ($ts, $h, $c, $m);";
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(time).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$h", (object?)host ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", category);
            cmd.Parameters.AddWithValue("$m", message);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Einträge im Zeitraum, neueste zuerst; optional nur ein Host ("" = nur Server), bestimmte Kategorien, Suchtext.</summary>
    public List<EventEntry> QueryEvents(DateTime from, DateTime to, string? host = null, IReadOnlyCollection<string>? categories = null,
        string? text = null, int limit = 5000)
    {
        var list = new List<EventEntry>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var where = "ts BETWEEN $from AND $to";
            if (host == "") where += " AND host IS NULL";                     // nur Server/allgemein
            else if (host is not null) { where += " AND host = $host"; cmd.Parameters.AddWithValue("$host", host); }
            if (categories is { Count: > 0 })
            {
                var names = categories.Select((c, i) => { cmd.Parameters.AddWithValue($"$c{i}", c); return $"$c{i}"; });
                where += $" AND category IN ({string.Join(",", names)})";
            }
            if (!string.IsNullOrWhiteSpace(text)) { where += " AND message LIKE $q ESCAPE '\\'"; cmd.Parameters.AddWithValue("$q", "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); }
            cmd.CommandText = $"SELECT ts, host, category, message FROM events WHERE {where} ORDER BY ts DESC, rowid DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new EventEntry(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime, r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        return list;
    }

    // ---------- Gesundheit ----------

    public sealed record HealthSample(DateTime Time, int FanRpm, double FanPercent, double VrTemp, double Accepted, double Rejected);

    /// <summary>Wert für die Frühwarnung; je 10 Minuten bleibt der letzte.</summary>
    public void AddHealthSample(string host, DateTime time, int fanRpm, double fanPercent, double vrTemp, double accepted, double rejected)
    {
        var ts = new DateTimeOffset(time).ToUnixTimeSeconds() / 600 * 600;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO health_samples (host, ts, fan_rpm, fan_pct, vr_temp, accepted, rejected)
                VALUES ($h, $ts, $rpm, $pct, $vr, $a, $r);
                """;
            cmd.Parameters.AddWithValue("$h", host);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$rpm", fanRpm);
            cmd.Parameters.AddWithValue("$pct", fanPercent);
            cmd.Parameters.AddWithValue("$vr", vrTemp);
            cmd.Parameters.AddWithValue("$a", accepted);
            cmd.Parameters.AddWithValue("$r", rejected);
            cmd.ExecuteNonQuery();
        }
    }

    public List<HealthSample> QueryHealth(string host, DateTime from, DateTime to)
    {
        var list = new List<HealthSample>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT ts, fan_rpm, fan_pct, vr_temp, accepted, rejected FROM health_samples
                WHERE host = $h AND ts >= $from AND ts < $to ORDER BY ts;
                """;
            cmd.Parameters.AddWithValue("$h", host);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new HealthSample(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime, (int)r.GetInt64(1),
                    r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)));
        }
        return list;
    }

    // ---------- Berichte ----------

    /// <summary>Gespeicherter Bericht eines abgeschlossenen Monats (JSON), null wenn keiner.</summary>
    public string? GetPeriodReport(string period)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM period_reports WHERE period = $p;";
            cmd.Parameters.AddWithValue("$p", period);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SavePeriodReport(string period, string json)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO period_reports (period, json, created) VALUES ($p, $j, $c);";
            cmd.Parameters.AddWithValue("$p", period);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Gespeicherte Monate (yyyy-MM), aufsteigend.</summary>
    public List<string> StoredPeriods()
    {
        var list = new List<string>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT period FROM period_reports ORDER BY period;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    /// <summary>Minuten mit Messwert und davon online im Zeitraum.</summary>
    public (int Online, int Total) MinuteCounts(string host, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(online), 0), COUNT(*) FROM samples WHERE host = $host AND ts >= $from AND ts < $to;";
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            r.Read();
            return ((int)r.GetInt64(0), (int)r.GetInt64(1));
        }
    }

    /// <summary>Ältester Minutenwert (für Hinweise, ab wann Berichte vollständig sind).</summary>
    public DateTime? EarliestSample()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(ts) FROM samples;";
            return cmd.ExecuteScalar() is long ts ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime : null;
        }
    }

    // ---------- Strompreise und Stundenwerte ----------

    /// <summary>Preise speichern (vorhandene Zeiträume werden überschrieben).</summary>
    public void AddPrices(IEnumerable<Automation.PricePoint> points)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in points)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO prices (ts, ends, ct) VALUES ($ts, $e, $ct);";
                cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(DateTime.SpecifyKind(p.StartUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$e", new DateTimeOffset(DateTime.SpecifyKind(p.EndUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$ct", p.CtPerKwh);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Mittlerer Preis je Stunde (Unix-Sekunden des Stundenbeginns) im Zeitraum; auch 15-min-Preise.</summary>
    public Dictionary<long, double> HourlyPrices(DateTime from, DateTime to) =>
        Hourly("SELECT (ts / 3600) * 3600 AS h, AVG(ct), COUNT(*) FROM prices WHERE ts >= $from AND ts < $to GROUP BY h;", null, from, to)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>Energie eines Miners je Stunde in Wh (Minutenwerte, nur online).</summary>
    public Dictionary<long, double> HourlyEnergyWh(string host, DateTime from, DateTime to) =>
        Hourly("SELECT (ts / 3600) * 3600 AS h, SUM(power) / 60.0, COUNT(*) FROM samples WHERE host = $key AND online = 1 AND ts >= $from AND ts < $to GROUP BY h;",
            host, from, to).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>Mittlere Leistung eines Plugs je Stunde und Anzahl Messminuten.</summary>
    public Dictionary<long, (double Value, int Count)> HourlyPlugPower(string plugId, DateTime from, DateTime to) =>
        Hourly("SELECT (ts / 3600) * 3600 AS h, AVG(power), COUNT(*) FROM plug_samples WHERE plug = $key AND ts >= $from AND ts < $to GROUP BY h;",
            plugId, from, to);

    private Dictionary<long, (double Value, int Count)> Hourly(string sql, string? key, DateTime from, DateTime to)
    {
        var result = new Dictionary<long, (double, int)>();
        lock (_lock)
        {
            FlushPlugSamples();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            if (key is not null) cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetInt64(0)] = (r.GetDouble(1), (int)r.GetInt64(2));
        }
        return result;
    }

    // ---------- Smart Plugs ----------

    /// <summary>
    /// Messwert eines Plugs; je Minute bleibt der letzte Wert (Zeit auf die volle Minute gerundet). Geschrieben wird beim
    /// Wechsel der Minute (bzw. vor dem nächsten Lesen), nicht bei jedem Messwert.
    /// </summary>
    public void AddPlugSample(string plugId, DateTime time, double powerW, double? energyWh)
    {
        var ts = new DateTimeOffset(time).ToUnixTimeSeconds() / 60 * 60;
        lock (_lock)
        {
            if (_pendingPlugs.TryGetValue(plugId, out var pending) && pending.Ts != ts) WritePlugSample(plugId, pending);
            _pendingPlugs[plugId] = (ts, powerW, energyWh);
        }
    }

    /// <summary>Gepufferte Plug-Werte schreiben (vor Abfragen, Sicherung, Aufräumen und beim Schließen).</summary>
    public void FlushPlugSamples()
    {
        lock (_lock)
        {
            if (_pendingPlugs.Count == 0) return;
            var pending = _pendingPlugs.ToList();
            _pendingPlugs.Clear();
            Batch(() => { foreach (var (plug, v) in pending) WritePlugSample(plug, v); });
        }
    }

    private void WritePlugSample(string plugId, (long Ts, double PowerW, double? EnergyWh) v)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO plug_samples (plug, ts, power, energy) VALUES ($plug, $ts, $p, $e);";
        cmd.Parameters.AddWithValue("$plug", plugId);
        cmd.Parameters.AddWithValue("$ts", v.Ts);
        cmd.Parameters.AddWithValue("$p", v.PowerW);
        cmd.Parameters.AddWithValue("$e", v.EnergyWh is { } e ? e : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Mittlere Leistung und Anzahl Messminuten eines Plugs im Zeitfenster; null ohne Daten.</summary>
    public (double PowerW, int Minutes)? AveragePlugPower(string plugId, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            FlushPlugSamples();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), AVG(power) FROM plug_samples WHERE plug = $plug AND ts BETWEEN $from AND $to;";
            cmd.Parameters.AddWithValue("$plug", plugId);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.GetInt64(0) == 0 || r.IsDBNull(1)) return null;
            return (r.GetDouble(1), (int)r.GetInt64(0));
        }
    }

    /// <summary>Leistungsverlauf eines Plugs, auf höchstens <paramref name="maxPoints"/> Punkte gemittelt.</summary>
    public List<(DateTime Time, double PowerW)> QueryPlug(string plugId, DateTime from, DateTime to, int maxPoints = 400)
    {
        var fromTs = new DateTimeOffset(from).ToUnixTimeSeconds();
        var toTs = new DateTimeOffset(to).ToUnixTimeSeconds();
        var bucket = Math.Max(60, (toTs - fromTs) / Math.Max(1, maxPoints));
        var result = new List<(DateTime, double)>();
        lock (_lock)
        {
            FlushPlugSamples();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT (ts / $b) * $b AS bucket, AVG(power) FROM plug_samples
                WHERE plug = $plug AND ts BETWEEN $from AND $to GROUP BY bucket ORDER BY bucket;
                """;
            cmd.Parameters.AddWithValue("$b", bucket);
            cmd.Parameters.AddWithValue("$plug", plugId);
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add((DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime, r.GetDouble(1)));
        }
        return result;
    }

    // ---------- Dauertests ----------

    public void AddSoakResult(SoakResultRecord r)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO soak_results (host, started, ended, freq, mv, passed, ratio, message)
                VALUES ($host, $s, $e, $f, $mv, $p, $r, $m);
                """;
            cmd.Parameters.AddWithValue("$host", r.Host);
            cmd.Parameters.AddWithValue("$s", new DateTimeOffset(r.Started).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$e", new DateTimeOffset(r.Ended).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$f", r.FrequencyMhz);
            cmd.Parameters.AddWithValue("$mv", r.CoreVoltageMv);
            cmd.Parameters.AddWithValue("$p", r.Passed ? 1 : 0);
            cmd.Parameters.AddWithValue("$r", (object?)r.Ratio ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", r.Message);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Dauertests eines Hosts, neueste zuerst.</summary>
    public List<SoakResultRecord> QuerySoakResults(string host)
    {
        var list = new List<SoakResultRecord>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT started, ended, freq, mv, passed, ratio, message FROM soak_results WHERE host = $host ORDER BY started DESC;";
            cmd.Parameters.AddWithValue("$host", host);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new SoakResultRecord(host, DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime,
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).LocalDateTime, (int)r.GetInt64(2), (int)r.GetInt64(3), r.GetInt64(4) == 1,
                    r.IsDBNull(5) ? null : r.GetDouble(5), r.GetString(6)));
        }
        return list;
    }

    public string FilePath { get; }

    // ---------- Tuning-Ereignisse ----------

    public void AddTuningEvent(TuningEvent e)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO tuning_events (host, ts, source, old_freq, old_mv, new_freq, new_mv, note)
                VALUES ($host, $ts, $src, $of, $om, $nf, $nm, $note);
                """;
            cmd.Parameters.AddWithValue("$host", e.Host);
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(e.Time).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$src", e.Source.ToString());
            cmd.Parameters.AddWithValue("$of", (object?)e.OldFrequencyMhz ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$om", (object?)e.OldCoreVoltageMv ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nf", e.NewFrequencyMhz);
            cmd.Parameters.AddWithValue("$nm", e.NewCoreVoltageMv);
            cmd.Parameters.AddWithValue("$note", (object?)e.Note ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Tuning-Ereignisse eines Hosts im Zeitraum (host "*" = alle Hosts), aufsteigend.</summary>
    public List<TuningEvent> QueryTuningEvents(string host, DateTime from, DateTime to)
    {
        var list = new List<TuningEvent>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT host, ts, source, old_freq, old_mv, new_freq, new_mv, note FROM tuning_events
                WHERE ($host = '*' OR host = $host) AND ts BETWEEN $from AND $to
                ORDER BY ts;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TuningEvent(
                    r.GetString(0),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).LocalDateTime,
                    Enum.TryParse<TuningSource>(r.GetString(2), out var src) ? src : TuningSource.Manual,
                    r.IsDBNull(3) ? null : r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetInt32(4),
                    r.GetInt32(5),
                    r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }
        return list;
    }

    /// <summary>Mittelwerte der Online-Minuten im Zeitfenster; null wenn keine Daten.</summary>
    public WindowAverage? Average(string host, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), AVG(hashrate), AVG(temp), AVG(power) FROM samples
                WHERE host = $host AND ts BETWEEN $from AND $to AND online = 1;
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.GetInt64(0) == 0 || r.IsDBNull(1)) return null;
            return new WindowAverage((int)r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
        }
    }

    // ---------- Sicherung ----------

    /// <summary>
    /// Konsistente Kopie über die SQLite-Backup-API (berücksichtigt WAL). Überschreibt nie eine vorhandene Datei.
    /// </summary>
    public void BackupTo(string targetFile)
    {
        if (File.Exists(targetFile))
            throw new IOException(L.T("Ziel existiert bereits und wird nicht überschrieben: {0}", targetFile));
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        lock (_lock)
        {
            FlushPlugSamples();
            using var target = new SqliteConnection($"Data Source={targetFile};Pooling=False");
            target.Open();
            _db.BackupDatabase(target);
        }
    }

    /// <summary>Zeilen je Tabelle – für die Prüfung nach Sicherung/Umzug.</summary>
    public Dictionary<string, long> CountRows()
    {
        lock (_lock) FlushPlugSamples();
        return CountRows(_db, _lock);
    }

    public static Dictionary<string, long> CountRows(SqliteConnection db, object? gate = null)
    {
        var result = new Dictionary<string, long>();
        lock (gate ?? new object())
        {
            var tables = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            foreach (var t in tables)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{t.Replace("\"", "\"\"")}\";";
                result[t] = (long)cmd.ExecuteScalar()!;
            }
        }
        return result;
    }

    /// <summary>PRAGMA integrity_check einer beliebigen Datenbankdatei (nur lesend).</summary>
    public static (bool Ok, Dictionary<string, long> Rows) Verify(string file)
    {
        using var db = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var ok = string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        return (ok, CountRows(db));
    }

    // ---------- Samples ----------

    /// <summary>Speichert einen Minutenwert; Zeit wird auf die volle Minute gerundet.</summary>
    public void AddSample(string host, DateTime time, double hashrateGh, double temp, double power, bool online)
    {
        var ts = new DateTimeOffset(time).ToUnixTimeSeconds() / 60 * 60;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO samples (host, ts, hashrate, temp, power, online)
                VALUES ($host, $ts, $h, $t, $p, $o);
                """;
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$h", hashrateGh);
            cmd.Parameters.AddWithValue("$t", temp);
            cmd.Parameters.AddWithValue("$p", power);
            cmd.Parameters.AddWithValue("$o", online ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Viele Minutenwerte in einer Transaktion (Tests, Import).</summary>
    internal void AddSamples(string host, IEnumerable<(DateTime Time, double HashrateGh, double Temp, double Power, bool Online)> samples)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO samples (host, ts, hashrate, temp, power, online) VALUES ($host, $ts, $h, $t, $p, $o);";
            var ts = cmd.Parameters.Add("$ts", Microsoft.Data.Sqlite.SqliteType.Integer);
            var hr = cmd.Parameters.Add("$h", Microsoft.Data.Sqlite.SqliteType.Real);
            var tp = cmd.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Real);
            var pw = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Real);
            var on = cmd.Parameters.Add("$o", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$host", host);
            foreach (var s in samples)
            {
                ts.Value = new DateTimeOffset(s.Time).ToUnixTimeSeconds() / 60 * 60;
                (hr.Value, tp.Value, pw.Value, on.Value) = (s.HashrateGh, s.Temp, s.Power, s.Online ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Verlauf eines Hosts im Zeitraum, auf höchstens <paramref name="maxPoints"/>
    /// Punkte gemittelt. Nur Online-Minuten.
    /// </summary>
    public List<Sample> Query(string host, DateTime from, DateTime to, int maxPoints = 400)
    {
        var fromTs = new DateTimeOffset(from).ToUnixTimeSeconds();
        var toTs = new DateTimeOffset(to).ToUnixTimeSeconds();
        var bucket = Math.Max(60, (toTs - fromTs) / Math.Max(1, maxPoints));

        var result = new List<Sample>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT (ts / $b) * $b AS bucket, AVG(hashrate), AVG(temp), AVG(power)
                FROM samples
                WHERE host = $host AND ts BETWEEN $from AND $to AND online = 1
                GROUP BY bucket
                ORDER BY bucket;
                """;
            cmd.Parameters.AddWithValue("$b", bucket);
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime;
                result.Add(new Sample(time, r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)));
            }
        }
        return result;
    }

    /// <summary>Anteil der Minuten mit online=1 im Zeitraum, null wenn keine Daten.</summary>
    public double? Availability(string host, DateTime since)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT AVG(online), COUNT(*) FROM samples WHERE host = $host AND ts >= $since;";
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue("$since", new DateTimeOffset(since).ToUnixTimeSeconds());

            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0) || r.GetInt64(1) == 0) return null;
            return r.GetDouble(0);
        }
    }

    public void Prune(int keepDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, keepDays)).ToUnixTimeSeconds();
        lock (_lock)
        {
            FlushPlugSamples();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff; DELETE FROM plug_samples WHERE ts < $cutoff; DELETE FROM prices WHERE ts < $cutoff; DELETE FROM health_samples WHERE ts < $cutoff;" +
                              " DELETE FROM events WHERE ts < $eventCutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            // Protokoll mindestens 30 Tage, auch wenn der Verlauf kürzer eingestellt ist
            cmd.Parameters.AddWithValue("$eventCutoff", DateTimeOffset.Now.AddDays(-Math.Max(keepDays, EventCategories.MinKeepDays)).ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- Best Diff ----------

    /// <summary>
    /// Trägt einen Wert ein, wenn er den bisherigen Rekord für Host und Coin übertrifft.
    /// Liefert den alten Rekord zurück, wenn ein neuer gesetzt wurde, sonst null.
    /// Bei einem Ersteintrag ist das Ergebnis ein Rekord mit Value 0.
    /// </summary>
    public BestDiffRecord? UpdateBestDiff(string host, string coin, double value, string raw)
    {
        if (value <= 0) return null;

        lock (_lock)
        {
            BestDiffRecord? previous = null;
            using (var read = _db.CreateCommand())
            {
                read.CommandText = "SELECT value, raw, achieved_at FROM best_diff WHERE host = $h AND coin = $c;";
                read.Parameters.AddWithValue("$h", host);
                read.Parameters.AddWithValue("$c", coin);
                using var r = read.ExecuteReader();
                if (r.Read())
                    previous = new BestDiffRecord(host, coin, r.GetDouble(0), r.GetString(1),
                        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).LocalDateTime);
            }

            if (previous is not null && value <= previous.Value) return null;

            using var write = _db.CreateCommand();
            write.CommandText = """
                INSERT OR REPLACE INTO best_diff (host, coin, value, raw, achieved_at)
                VALUES ($h, $c, $v, $raw, $at);
                """;
            write.Parameters.AddWithValue("$h", host);
            write.Parameters.AddWithValue("$c", coin);
            write.Parameters.AddWithValue("$v", value);
            write.Parameters.AddWithValue("$raw", raw);
            write.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToUnixTimeSeconds());
            write.ExecuteNonQuery();

            return previous ?? new BestDiffRecord(host, coin, 0, "", DateTime.MinValue);
        }
    }

    public List<BestDiffRecord> GetBestDiffs()
    {
        var list = new List<BestDiffRecord>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT host, coin, value, raw, achieved_at FROM best_diff ORDER BY value DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new BestDiffRecord(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetString(3),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).LocalDateTime));
        }
        return list;
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Verbindung schließen und aus dem Pool nehmen – sonst hält SQLite die Datei (und das WAL) noch offen.</summary>
    public void Dispose()
    {
        try { FlushPlugSamples(); } catch { /* Datei weg oder gesperrt – nur die letzte Minute fehlt */ }
        var cs = _db.ConnectionString;
        _db.Dispose();
        using var pooled = new SqliteConnection(cs);
        SqliteConnection.ClearPool(pooled);
    }
}
