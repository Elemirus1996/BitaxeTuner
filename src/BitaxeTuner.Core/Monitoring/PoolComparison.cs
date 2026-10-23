namespace BitaxeTuner.Core.Monitoring;

/// <summary>Eine Zeile im Pool-Vergleich. Werte null = zu wenige Daten.</summary>
/// <param name="Pool">„url:port“ in Kleinbuchstaben.</param>
/// <param name="Miners">Wie viele Miner im Zeitraum auf diesem Pool gearbeitet haben.</param>
/// <param name="Hours">Miner-Stunden auf diesem Pool.</param>
/// <param name="RejectPercent">Anteil abgelehnter Shares in %.</param>
/// <param name="ResponseMs">Ø Antwortzeit des Pools.</param>
/// <param name="OutageHours">Miner-Stunden, in denen dieser Pool als Haupt-Pool eingestellt war, der Miner aber auf dem Ersatz-Pool lief.</param>
/// <param name="AvailabilityPercent">Anteil der Zeit als Haupt-Pool, in der er erreichbar war.</param>
/// <param name="FallbackHours">Miner-Stunden, in denen dieser Pool als Ersatz-Pool eingesprungen ist.</param>
/// <param name="AvgHashrateGh">Ø Hashrate der Miner auf diesem Pool (GH/s je Miner).</param>
/// <param name="IncomePerThDay">Ertrag je TH und Tag in der gewählten Währung (nur mit Pool-Konto).</param>
/// <param name="ConnectMs">0.9.24: Ø Zeit für den TCP-Verbindungsaufbau (eigene Messung alle 10 min).</param>
/// <param name="ConnectOkPercent">0.9.24: Anteil der Messungen, bei denen der Pool erreichbar war.</param>
public sealed record PoolComparisonRow(string Pool, int Miners, double Hours, double? RejectPercent, double? ResponseMs,
    double OutageHours, double? AvailabilityPercent, double FallbackHours, double? AvgHashrateGh, decimal? IncomePerThDay,
    double? ConnectMs = null, double? ConnectOkPercent = null, int ConnectSamples = 0);

/// <summary>
/// 0.9.15 Pool-Vergleich: wertet die 10-Minuten-Werte (<see cref="HistoryStore.PoolSample"/>) je Pool aus – abgelehnte
/// Shares, Antwortzeit, Ausfälle als Haupt-Pool, Einsatz als Ersatz-Pool und auf Wunsch Ertrag je TH.
/// </summary>
public static class PoolComparison
{
    /// <summary>Abstand zweier Werte, der noch als zusammenhängend gilt (sonst: Lücke, Miner offline).</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(25);
    private const double SlotHours = 10.0 / 60;

    /// <param name="income">Ertrag eines Pools im Zeitraum (null = unbekannt), z. B. aus dem Pool-Konto.</param>
    public static List<PoolComparisonRow> Build(IReadOnlyList<HistoryStore.PoolSample> samples, Func<string, decimal?>? income = null,
        IReadOnlyDictionary<string, (double? AvgMs, int Ok, int Total)>? latency = null)
    {
        var acc = new Dictionary<string, (double Acc, double Rej)>();
        foreach (var host in samples.GroupBy(s => s.Host))
        {
            HistoryStore.PoolSample? prev = null;
            foreach (var s in host.OrderBy(s => s.Time))
            {
                // Zähler-Differenz nur zwischen zwei Werten desselben Pools ohne Neustart (Zähler sinken nicht) und ohne Lücke
                if (prev is { } p && p.Pool == s.Pool && s.Time - p.Time <= MaxGap && s.Accepted >= p.Accepted && s.Rejected >= p.Rejected)
                {
                    var cur = acc.GetValueOrDefault(s.Pool);
                    acc[s.Pool] = (cur.Acc + s.Accepted - p.Accepted, cur.Rej + s.Rejected - p.Rejected);
                }
                prev = s;
            }
        }

        // 0.9.24: auch Pools, die nur gemessen werden (z. B. Vorlage, auf der noch kein Miner lief)
        var pools = samples.Select(s => s.Pool).Concat(samples.Select(s => s.PrimaryPool)).Concat(latency?.Keys ?? [])
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PoolComparisonRow>();
        foreach (var pool in pools)
        {
            var on = samples.Where(s => s.Pool == pool).ToList();
            var asPrimary = samples.Where(s => s.PrimaryPool == pool).ToList();
            var outage = asPrimary.Count(s => s.Fallback) * SlotHours;
            var shares = acc.GetValueOrDefault(pool);
            double? reject = shares.Acc + shares.Rej >= 20 ? shares.Rej / (shares.Acc + shares.Rej) * 100 : null;
            var ms = on.Where(s => s.ResponseMs > 0).Select(s => s.ResponseMs!.Value).ToList();
            var hashes = on.Where(s => s.Hashrate > 0).Select(s => s.Hashrate).ToList();
            var thDays = on.Sum(s => s.Hashrate) / 1000 * SlotHours / 24;
            decimal? perTh = income?.Invoke(pool) is { } inc && thDays > 0.01 ? Math.Round(inc / (decimal)thDays, 4) : null;
            rows.Add(new PoolComparisonRow(pool,
                on.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Math.Round(on.Count * SlotHours, 1),
                reject is { } r ? Math.Round(r, 2) : null,
                ms.Count > 0 ? Math.Round(ms.Average(), 0) : null,
                Math.Round(outage, 1),
                asPrimary.Count > 0 ? Math.Round(100.0 * asPrimary.Count(s => !s.Fallback) / asPrimary.Count, 1) : null,
                Math.Round(on.Count(s => s.Fallback) * SlotHours, 1),
                hashes.Count > 0 ? Math.Round(hashes.Average(), 0) : null,
                perTh)
            {
                ConnectMs = latency?.TryGetValue(pool, out var l) == true ? l.AvgMs : null,
                ConnectOkPercent = latency?.TryGetValue(pool, out var l2) == true && l2.Total > 0 ? Math.Round(100.0 * l2.Ok / l2.Total, 1) : null,
                ConnectSamples = latency?.TryGetValue(pool, out var l3) == true ? l3.Total : 0,
            });
        }
        return rows.OrderByDescending(r => r.Hours).ThenBy(r => r.Pool).ToList();
    }

    /// <summary>0.9.24: schnellster zuverlässiger Pool – mind. 6 Messungen, mind. 95 % erreichbar, kürzester Verbindungsaufbau.</summary>
    public static PoolComparisonRow? Fastest(IEnumerable<PoolComparisonRow> rows) =>
        rows.Where(r => r.ConnectSamples >= 6 && r.ConnectOkPercent >= 95 && r.ConnectMs is not null).MinBy(r => r.ConnectMs);
}
