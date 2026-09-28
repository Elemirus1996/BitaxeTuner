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

    public HistoryStore(string? path = null)
    {
        var file = path ?? DataPaths.HistoryFile;
        FilePath = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        _db = new SqliteConnection($"Data Source={file}");
        _db.Open();

        Execute("PRAGMA journal_mode=WAL;");
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
            using var target = new SqliteConnection($"Data Source={targetFile};Pooling=False");
            target.Open();
            _db.BackupDatabase(target);
        }
    }

    /// <summary>Zeilen je Tabelle – für die Prüfung nach Sicherung/Umzug.</summary>
    public Dictionary<string, long> CountRows() => CountRows(_db, _lock);

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
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
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

    public void Dispose() => _db.Dispose();
}
