using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.15 Pool-Vergleich: Auswertung der 10-Minuten-Werte je Pool.</summary>
public sealed class PoolComparisonTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DateTime _t0 = new(2026, 10, 12, 0, 0, 0);

    public void Dispose() => _dir.Dispose();

    private HistoryStore.PoolSample S(string host, int slot, string pool, string primary, bool fallback, double acc, double rej, double? ms = 40, double gh = 1000) =>
        new(host, _t0.AddMinutes(10 * slot), pool, primary, fallback, acc, rej, ms, gh);

    [Fact]
    public void Rejects_outages_and_fallback_time_are_counted_per_pool()
    {
        var samples = new List<HistoryStore.PoolSample>
        {
            // Miner A: 4 Werte auf a:3333 (100 Shares, 2 abgelehnt), dann 2 Werte auf dem Ersatz-Pool b:4444
            S("A", 0, "a:3333", "a:3333", false, 1000, 10),
            S("A", 1, "a:3333", "a:3333", false, 1040, 11),
            S("A", 2, "a:3333", "a:3333", false, 1070, 11),
            S("A", 3, "a:3333", "a:3333", false, 1098, 12),
            S("A", 4, "b:4444", "a:3333", true, 1100, 12, ms: 120),
            S("A", 5, "b:4444", "a:3333", true, 1110, 15, ms: 140),
            // Miner B: Neustart (Zähler fällt) zählt nicht als negative Differenz
            S("B", 0, "a:3333", "a:3333", false, 500, 0, gh: 2000),
            S("B", 1, "a:3333", "a:3333", false, 10, 0, gh: 2000),
            S("B", 2, "a:3333", "a:3333", false, 40, 1, gh: 2000),
        };
        var rows = PoolComparison.Build(samples);
        var a = rows.Single(r => r.Pool == "a:3333");
        Assert.Equal(2, a.Miners);
        Assert.Equal(1.2, a.Hours);                         // 7 Werte × 10 min
        Assert.Equal(2.29, a.RejectPercent);                // 3 abgelehnt / (128 + 3) – Differenzen ohne Neustart
        Assert.Equal(0.3, a.OutageHours);                   // 2 Werte Ersatz-Pool, während a Haupt-Pool war
        Assert.Equal(77.8, a.AvailabilityPercent);          // 7 von 9 Werten als Haupt-Pool erreichbar
        var b = rows.Single(r => r.Pool == "b:4444");
        Assert.Equal(0.3, b.FallbackHours);
        Assert.Null(b.RejectPercent);                       // zu wenige Shares
        Assert.Equal(130, b.ResponseMs);
        Assert.Null(b.AvailabilityPercent);                 // nie Haupt-Pool
    }

    [Fact]
    public void Income_per_th_day_uses_the_hashrate_on_the_pool()
    {
        // 1 Miner mit 1 TH/s einen ganzen Tag (144 Werte) auf p:1
        var samples = Enumerable.Range(0, 144).Select(i => S("A", i, "p:1", "p:1", false, i * 10, 0)).ToList();
        var row = PoolComparison.Build(samples, pool => pool == "p:1" ? 2.5m : null).Single();
        Assert.Equal(2.5m, row.IncomePerThDay);
        Assert.Equal(1000, row.AvgHashrateGh);
    }

    [Fact]
    public void Store_keeps_pool_samples_per_ten_minutes_and_prunes_them()
    {
        using var store = new HistoryStore(Path.Combine(_dir.Path, "h.db"));
        store.AddPoolSample("A", _t0.AddMinutes(1), "a:3333", "a:3333", false, 10, 0, 35, 1200);
        store.AddPoolSample("A", _t0.AddMinutes(9), "a:3333", "a:3333", false, 12, 0, null, 1210);   // gleicher 10-min-Platz
        store.AddPoolSample("A", _t0.AddMinutes(11), "b:1", "a:3333", true, 13, 1, 80, 1190);
        var list = store.QueryPoolSamples(_t0.AddHours(-1), _t0.AddHours(1));
        Assert.Equal(2, list.Count);
        Assert.Null(list[0].ResponseMs);
        Assert.True(list[1].Fallback);
        store.Prune(1, new DateTimeOffset(_t0.AddDays(3)));
        Assert.Empty(store.QueryPoolSamples(_t0.AddHours(-1), _t0.AddHours(1)));
    }
}
