using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.26: Einordnung eines Miners im Community-Vergleich.</summary>
public sealed record CommunityCompare(string Model, int N, double MyJth, double? BetterThan, double MedianJth, string Text);

/// <summary>
/// 0.9.26 Benchmark-Vergleich mit der Community: freiwillig je Miner (<see cref="Config.DeviceConfig.ShareBenchmark"/>).
/// Einsenden höchstens einmal pro Woche, nur bei stabilem Betrieb (24 h fast durchgehend online, keine Tuning-Änderung).
/// Statistik einmal täglich laden (Datei im Datenordner, gilt auch offline weiter).
/// </summary>
public sealed partial class MinerHub
{
    private DateTime _communityStatsAt = DateTime.MinValue;
    private DateTime _communityShareAt = DateTime.MinValue;
    private int _communityBusy;
    private Dictionary<string, ModelStats>? _communityStats;

    /// <summary>Für Tests: Einsenden ersetzen.</summary>
    internal Func<BenchSample, Task>? BenchSender { get; set; }
    /// <summary>Für Tests: Statistik-Datei laden ersetzen.</summary>
    internal Func<Task<string>>? BenchStatsFetch { get; set; }

    private string CommunityFile => Path.Combine(DataDirectory, "community-stats.json");

    public IReadOnlyDictionary<string, ModelStats> CommunityStats
    {
        get
        {
            if (_communityStats is null)
                try { _communityStats = File.Exists(CommunityFile) ? CommunityBench.ParseStats(File.ReadAllText(CommunityFile)) : []; }
                catch (IOException) { _communityStats = []; }
            return _communityStats;
        }
    }

    /// <summary>24-Stunden-Mittel als Einsendung – null, wenn nicht stabil genug (Ausfälle, Tuning, zu wenig Werte).</summary>
    internal BenchSample? BenchSampleOf(HubDevice d, DateTime now)
    {
        if (History is null || d.State.Normalized is not { } info || !d.State.Online || d.IsSimulated) return null;
        var avg = History.Average(d.Host, now.AddHours(-24), now);
        if (avg is null || avg.Minutes < 20 * 60 || avg.HashRateGh <= 0 || avg.Power <= 0) return null;
        if (History.QueryTuningEvents(d.Host, now.AddHours(-24), now).Count > 0) return null;
        if (info.FrequencyMhz <= 0 || info.CoreVoltageMv <= 0) return null;
        return new BenchSample(d.Profile.Id, info.AsicModel, info.FirmwareVersion, info.FrequencyMhz, info.CoreVoltageMv,
            avg.HashRateGh, avg.Power / (avg.HashRateGh / 1000), avg.Temp);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, CommunityCompare? Value)> _communityCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Einordnung nach dem 24-Stunden-Mittel (oder dem aktuellen Wert); null ohne Statistik für das Modell. 10 min gepuffert.</summary>
    public CommunityCompare? CommunityOf(HubDevice d, DateTime now)
    {
        if (CommunityStats.Count == 0) return null;
        if (_communityCache.TryGetValue(d.Host, out var c) && now - c.At < TimeSpan.FromMinutes(10) && now >= c.At) return c.Value;
        var value = BuildCommunity(d, now);
        _communityCache[d.Host] = (now, value);
        return value;
    }

    private CommunityCompare? BuildCommunity(HubDevice d, DateTime now)
    {
        if (!CommunityStats.TryGetValue(d.Profile.Id, out var st)) return null;
        double? jth = null;
        if (History?.Average(d.Host, now.AddHours(-24), now) is { HashRateGh: > 0, Power: > 0 } a) jth = a.Power / (a.HashRateGh / 1000);
        else if (d.State.Info is { hashRate: > 0, power: > 0 } i) jth = i.power / (i.hashRate / 1000);
        if (jth is not { } my) return null;
        var better = st.BetterThan(my);
        var text = better is { } b
            ? L.T("{0} J/TH – {1} der Community ({2} Einsendungen, Median {3} J/TH).", my.ToString("0.0", L.Culture), CommunityBench.RankText(b), st.N, st.JthQ[2].ToString("0.0", L.Culture))
            : L.T("Noch zu wenige Einsendungen für dieses Modell ({0}).", st.N);
        return new CommunityCompare(st.Model, st.N, Math.Round(my, 2), better, st.JthQ[2], text);
    }

    /// <summary>Aus dem Abfragetakt: Statistik täglich laden, eingeschaltete Miner wöchentlich einsenden (im Hintergrund).</summary>
    internal void TickCommunity(DateTime now)
    {
        if (!Options.OnlineChecks && BenchSender is null && BenchStatsFetch is null) return;
        if (now - _communityShareAt < TimeSpan.FromMinutes(30) && now - _communityStatsAt < TimeSpan.FromHours(24)) return;
        if (Interlocked.Exchange(ref _communityBusy, 1) == 1) return;
        // Nur wer mitmacht, lädt die Statistik (sonst kein Kontakt zu GitHub)
        var loadStats = now - _communityStatsAt >= TimeSpan.FromHours(24) && Config.Devices.Any(c => c.ShareBenchmark);
        if (loadStats) _communityStatsAt = now;
        var due = now - _communityShareAt >= TimeSpan.FromMinutes(30)
            ? Devices.Where(d => d.Config.ShareBenchmark && (d.Config.BenchSharedAt is not { } at || now - at >= CommunityBench.ShareInterval))
                .Select(d => (d, s: BenchSampleOf(d, now))).Where(x => x.s is not null).ToList()
            : [];
        _communityShareAt = now;
        var send = BenchSender ?? (s => CommunityBench.SendAsync(s));
        var fetch = BenchStatsFetch ?? (() => CommunityBench.FetchStatsAsync());
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (d, s) in due)
                {
                    try
                    {
                        await send(s!);
                        d.Config.BenchSharedAt = now;
                        LogEvent(d.Host, EventCategories.Other, L.T("Community-Vergleich: 24-Stunden-Mittel anonym eingesendet ({0} MHz, {1} mV, {2} J/TH).", s!.FreqMhz, s.CoreMv, s.Jth.ToString("0.0", L.Culture)));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LocalizedException) { /* nächste Woche bzw. nächster Versuch */ }
                }
                if (due.Count > 0) try { Config.Save(); } catch { /* nicht kritisch */ }
                if (loadStats)
                {
                    var json = await fetch();
                    var parsed = CommunityBench.ParseStats(json);
                    if (parsed.Count > 0 || json.Contains("\"models\"", StringComparison.Ordinal))
                    {
                        _communityStats = parsed;
                        _communityCache.Clear();
                        try { File.WriteAllText(CommunityFile, json); } catch (IOException) { }
                    }
                }
            }
            catch { /* offline – alte Statistik gilt weiter */ }
            finally { Interlocked.Exchange(ref _communityBusy, 0); }
        });
    }
}
