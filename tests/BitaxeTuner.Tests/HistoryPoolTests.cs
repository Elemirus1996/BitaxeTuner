using BitaxeTuner.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace BitaxeTuner.Tests;

/// <summary>
/// 0.9.25: Verlaufsdatenbank bleibt nutzbar, während andere Stellen den globalen SQLite-Pool leeren (Sicherung,
/// Datenübernahme, Aufräumen, TempDir in Tests) – vermutete Ursache gelegentlich roter Tests unter Last.
/// </summary>
public class HistoryPoolTests
{
    [Fact]
    public async Task Open_stores_survive_ClearAllPools()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clearer = Task.Run(async () => { while (!cts.IsCancellationRequested) { SqliteConnection.ClearAllPools(); await Task.Yield(); } });
        var workers = Enumerable.Range(0, 6).Select(w => Task.Run(() =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var h = new HistoryStore(Path.Combine(dir.Path, $"h{w}-{n++}.db"));
                    for (var i = 0; i < 20; i++)
                    {
                        h.AddSample("10.0.0.1", DateTime.Now.AddMinutes(-i), 1000, 50, 15, true);
                        h.Query("10.0.0.1", DateTime.Now.AddHours(-1), DateTime.Now);
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name + ": " + ex.Message.Split('\n')[0] + " @ " + ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("BitaxeTuner"))?.Trim()); }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        await clearer;
        Assert.True(errors.Count == 0, $"{errors.Count} Fehler, z. B.: " + string.Join(" | ", errors.Distinct().Take(3)));
    }
}
