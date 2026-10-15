using BitaxeTuner.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace BitaxeTuner.Tests;

public class HistoryCompactTests
{
    /// <summary>Datenbank wie vor 0.9.16: ohne auto_vacuum, viele gelöschte Zeilen (leere Seiten).</summary>
    private static void CreateLegacy(string file, int keep, int deleted)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA auto_vacuum=NONE; PRAGMA journal_mode=WAL;
            CREATE TABLE miner_log (host TEXT NOT NULL, ts INTEGER NOT NULL, level TEXT, tag TEXT, msg TEXT);
            """;
        cmd.ExecuteNonQuery();
        using var tx = c.BeginTransaction();
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO miner_log (host, ts, level, tag, msg) VALUES ('10.0.7.1', $ts, 'I', 'asic', $msg);";
        var ts = ins.Parameters.Add("$ts", SqliteType.Integer);
        ins.Parameters.AddWithValue("$msg", new string('x', 400));
        for (var i = 0; i < keep + deleted; i++) { ts.Value = i; ins.ExecuteNonQuery(); }
        tx.Commit();
        using var del = c.CreateCommand();
        del.CommandText = $"DELETE FROM miner_log WHERE ts < {deleted};";
        del.ExecuteNonQuery();
    }

    private static long Pragma(string file, string pragma)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False;Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Old_database_is_compacted_once_without_losing_rows()
    {
        using var dir = new TempDir();
        var file = dir.File("history.db");
        CreateLegacy(file, keep: 1000, deleted: 9000);

        var keepAlive = 0;
        HistoryStore.KeepAlive = () => Interlocked.Increment(ref keepAlive);
        try
        {
            using (var store = new HistoryStore(file))
            {
                var c = store.Compaction;
                Assert.NotNull(c);
                Assert.Null(c!.Skipped);
                Assert.True(c.AfterBytes < c.BeforeBytes / 3, $"{c.BeforeBytes} → {c.AfterBytes}");
                Assert.Equal(1000, store.CountRows()["miner_log"]);
                Assert.True(store.SpaceInfo().Incremental);
                Assert.True(keepAlive >= 1);

                // Danach: Aufräumen gibt den Platz gleich zurück
                store.PruneMinerLog(DateTimeOffset.FromUnixTimeMilliseconds(9500).LocalDateTime);
                Assert.True(store.SpaceInfo().FreeBytes > 0);
                Assert.True(store.ReleaseFreeSpace() > 0);
                Assert.Equal(0, store.SpaceInfo().FreeBytes);
            }
            Assert.False(File.Exists(file + ".compact"));
            Assert.False(File.Exists(file + ".pre-compact"));

            // Zweites Öffnen: nichts mehr zu tun
            using var again = new HistoryStore(file);
            Assert.Null(again.Compaction);
            Assert.Equal(2, Pragma(file, "auto_vacuum"));
        }
        finally { HistoryStore.KeepAlive = null; }
    }

    [Fact]
    public void New_database_starts_incremental_and_leftovers_are_removed()
    {
        using var dir = new TempDir();
        var file = dir.File("history.db");
        using (var store = new HistoryStore(file))
        {
            Assert.Null(store.Compaction);
            Assert.True(store.SpaceInfo().Incremental);
        }

        // Rest eines abgebrochenen Versuchs stört nicht
        var legacy = dir.File("old.db");
        CreateLegacy(legacy, keep: 10, deleted: 100);
        File.WriteAllText(legacy + ".compact", "kaputt");
        using var s2 = new HistoryStore(legacy);
        Assert.Null(s2.Compaction!.Skipped);
        Assert.Equal(10, s2.CountRows()["miner_log"]);
        Assert.False(File.Exists(legacy + ".compact"));
    }
}
