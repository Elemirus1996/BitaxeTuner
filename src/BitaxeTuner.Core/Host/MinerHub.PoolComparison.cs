using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Core.Host;

/// <summary>Ergebnis des Pool-Vergleichs für einen Zeitraum.</summary>
public sealed record PoolComparisonResult(string Range, string Currency, IReadOnlyList<PoolComparisonRow> Rows, bool IncomeShared)
{
    /// <summary>0.9.24: schnellster zuverlässiger Pool nach Verbindungsaufbau (null = zu wenig Messungen).</summary>
    public PoolComparisonRow? Fastest => PoolComparison.Fastest(Rows);
}

/// <summary>
/// 0.9.15 Pool-Vergleich: alle 10 min je Miner den Pool-Stand sichern (mit den Gesundheitswerten) und je Pool auswerten –
/// abgelehnte Shares, Antwortzeit, Ausfälle als Haupt-Pool, Einsatz als Ersatz-Pool, Ertrag je TH (mit Pool-Konto).
/// </summary>
public sealed partial class MinerHub
{
    public static readonly string[] PoolComparisonRanges = ["24h", "7d", "30d"];

    /// <summary>„url:port“ in Kleinbuchstaben (wie <see cref="Pools.PoolSlot.Address"/>).</summary>
    internal static string PoolKey(string? url, int port) =>
        string.IsNullOrWhiteSpace(url) ? "" : $"{url.Trim().ToLowerInvariant()}:{port}";

    private void RecordPoolSample(string host, SystemInfo i, DateTime now)
    {
        if (History is null) return;
        var fallback = i.isUsingFallbackStratum != 0;
        var primary = PoolKey(i.stratumURL, i.stratumPort);
        var active = fallback && !string.IsNullOrWhiteSpace(i.fallbackStratumURL) ? PoolKey(i.fallbackStratumURL, i.fallbackStratumPort) : primary;
        if (active.Length == 0) return;
        var ms = PoolWatch.AverageResponse(host) ?? (i.responseTime > 0 ? i.responseTime : null);
        try { History.AddPoolSample(host, now, active, primary, fallback, i.sharesAccepted, i.sharesRejected, ms, i.hashRate); }
        catch { /* nicht kritisch */ }
    }

    internal void RecordPoolSampleForTest(string host, SystemInfo info, DateTime now) => RecordPoolSample(host, info, now);

    /// <summary>Pool-Vergleich für „24h“, „7d“ oder „30d“ (Standard 7 Tage).</summary>
    public PoolComparisonResult PoolComparisonFor(string? range, DateTime now)
    {
        range = PoolComparisonRanges.Contains(range) ? range! : "7d";
        var from = range switch { "24h" => now.AddHours(-24), "30d" => now.AddDays(-30), _ => now.AddDays(-7) };
        var cur = Currencies.Of(Config).Code;
        if (History is null) return new PoolComparisonResult(range, cur, [], false);
        var samples = History.QueryPoolSamples(from, now);

        // Ertrag: nur mit Pool-Konto (Mining-Dutch). Das Konto kennt keinen Port – der Ertrag gilt für alle Mining-Dutch-Pools
        // zusammen und wird nach TH-Tagen verteilt (je TH also gleich).
        Func<string, decimal?>? income = null;
        var shared = false;
        if (Config.PoolAccount.Enabled)
        {
            List<PoolTransaction> ledger;
            try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
            if (ledger.Count > 0 && PoolIncome(ledger, from.ToUniversalTime(), now.ToUniversalTime(), cur).Income is { } total)
            {
                var dutch = samples.Where(s => IsMiningDutch(s.Pool)).ToList();
                var thAll = dutch.Sum(s => s.Hashrate);
                shared = dutch.Select(s => s.Pool).Distinct().Count() > 1;
                income = pool => IsMiningDutch(pool) && thAll > 0
                    ? total * (decimal)(samples.Where(s => s.Pool == pool).Sum(s => s.Hashrate) / thAll)
                    : null;
            }
        }
        Dictionary<string, (double? AvgMs, int Ok, int Total)>? latency = null;
        try { latency = History.QueryPoolLatency(from, now); } catch { /* ältere Datenbank */ }
        return new PoolComparisonResult(range, cur, PoolComparison.Build(samples, income, latency), shared);
    }

    private static bool IsMiningDutch(string pool) => pool.Contains("mining-dutch.", StringComparison.OrdinalIgnoreCase);
}
