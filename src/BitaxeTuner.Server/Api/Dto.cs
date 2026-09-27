using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// Antworten der REST-API. Werden im Hub-Kontext gebaut (konsistenter Stand).
/// Für die Rolle „Nur ansehen“ ohne IP-Adressen, Wallet-Adressen, Pool-Benutzer und Protokolle.
/// </summary>
public static class Dto
{
    /// <summary>Stabile, nicht sprechende Kennung eines Geräts (die IP-Adresse soll in URLs der Ansicht nicht auftauchen).</summary>
    public static string DeviceId(string host) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant())))[..12].ToLowerInvariant();

    public static HubDevice? Find(MinerHub hub, string id) => hub.Devices.FirstOrDefault(d => DeviceId(d.Host) == id);

    public static object Status(MinerHub hub, Role role, DateTime now)
    {
        var devices = hub.Devices;
        var online = devices.Where(d => d.State.Online && d.State.Info is not null).Select(d => d.State.Info!).ToList();
        var hash = online.Sum(i => i.hashRate);
        var power = online.Sum(i => i.power);
        var price = hub.Prices.PriceAt(now.ToUniversalTime());
        return new
        {
            time = now,
            running = hub.IsRunning,
            totals = new
            {
                hashrate = hash,
                power,
                efficiency = hash > 1 ? power / (hash / 1000.0) : (double?)null,
                online = online.Count,
                count = devices.Count,
                maxTemp = online.Count > 0 ? online.Max(i => i.temp) : (double?)null,
                costPerDay = power * 24 / 1000.0 * hub.Config.ElectricityCtPerKwh / 100.0,
                currency = hub.Config.Currency,
            },
            price = price is null ? null : new { source = hub.Prices.SourceName, ct = price },
            history = hub.AggregateHistory.TakeLast(360).Select(s => new[] { Unix(s.Time), R(s.HashRateGh), R(s.Temp), R(s.Power) }),
            devices = devices.Select(d => Summary(hub, d, role)),
        };
    }

    public static object Summary(MinerHub hub, HubDevice d, Role role)
    {
        var s = d.State;
        var i = s.Online ? s.Info : null;
        var n = s.Online ? s.Normalized : null;
        var admin = role == Role.Admin;
        return new
        {
            id = DeviceId(d.Host),
            name = d.Title,
            host = admin ? d.Host : null,
            online = s.Online,
            error = s.Online ? null : s.Error,
            maintenance = d.Connection.InMaintenance,
            simulated = d.IsSimulated,
            model = n?.DeviceModel ?? n?.AsicModel,
            firmware = i?.version,
            firmwareText = hub.FirmwareText(s),
            profile = d.Profile.Name,
            hashrate = i?.hashRate,
            expectedHashrate = n?.ExpectedHashRateGh,
            temp = i?.temp,
            vrTemp = i?.vrTemp,
            power = i?.power,
            efficiency = n?.EfficiencyJth,
            frequency = n?.FrequencyMhz,
            voltage = n?.CoreVoltageMv,
            fanRpm = i?.fanrpm,
            fanPercent = i?.fanspeed,
            uptimeSeconds = i?.uptimeSeconds,
            sharesAccepted = i?.sharesAccepted,
            sharesRejected = i?.sharesRejected,
            bestDiff = i?.bestDiff,
            errorPercent = n?.ErrorPercent,
            pool = admin ? hub.PoolText(s) : (i is null ? null : i.isUsingFallbackStratum != 0 ? "Fallback-Pool" : "Primär-Pool"),
            automation = d.AutomationStatus,
            soak = d.Config.Soak is null ? null : new { status = d.SoakStatus, until = d.Config.Soak.Until },
            suggestion = d.PendingSuggestion,
            benchmark = d.Benchmark is { } b ? Run(b) : null,
            history = s.History.TakeLast(120).Select(x => new[] { Unix(x.Time), R(x.HashRateGh), R(x.Temp), R(x.Power) }),
        };
    }

    public static object Run(BenchmarkRun b) => new
    {
        running = b.IsRunning,
        paused = b.IsPaused,
        phase = b.PhaseText,
        step = b.StepText,
        eta = b.EtaText,
        phaseProgress = R(b.PhaseProgress),
        overallProgress = R(b.OverallProgress),
        started = b.Started,
    };

    /// <summary>Einzelansicht eines Geräts: Zusammenfassung plus Profil, Ergebnisse, Automatik (Admin: Protokoll, Wallet).</summary>
    public static object Detail(MinerHub hub, HubDevice d, Role role)
    {
        var admin = role == Role.Admin;
        var p = d.Profile;
        var session = hub.Benchmarks.LatestSession(d);
        var c = d.Config;
        return new
        {
            summary = Summary(hub, d, role),
            profile = new
            {
                p.Id, p.Name, p.Notes,
                p.MinFrequencyMhz, p.MaxFrequencyMhz, p.DefaultFrequencyMhz,
                p.MinVoltageMv, p.MaxVoltageMv, p.DefaultVoltageMv,
                p.MaxChipTempC, p.MaxPowerW,
            },
            profiles = admin ? hub.ProfilesFor(d).Select(x => new { x.Id, x.Name }) : null,
            benchmarkDefaults = admin ? BenchmarkSettings.FromProfile(p) : null,
            estimatedDuration = BenchmarkManager.EstimatedDurationText(BenchmarkSettings.FromProfile(p)),
            session = session is null ? null : Session(session),
            config = admin ? new
            {
                c.Name, c.Host, c.WalletAddress, c.Coin, c.FirmwareRepo, c.LogAlerts, c.ProfileId,
                c.Presets,
                thermalGuard = c.ThermalGuard,
                thermalGuardApproved = c.ThermalGuard.IsApproved(d.Host),
                schedule = c.Schedule,
                scheduleApproved = c.Schedule.IsApproved(d.Host),
            } : null,
            log = admin ? d.LogLines.TakeLast(300) : null,
        };
    }

    public static object Session(BenchmarkSession s) => new
    {
        s.Id, s.StartedAt, s.FinishedAt, s.FinishReason, s.IsFinished, s.DeviceModel, s.Settings,
        results = s.Results.Select(r => new
        {
            r.FrequencyMhz, r.CoreVoltageMv, outcome = r.Outcome.ToString(), r.OutcomeText, r.IsStable, r.Message,
            r.AvgHashRateGh, r.ExpectedHashRateGh, r.AvgPowerW, r.EfficiencyJth, r.MaxChipTempC, r.MaxVrTempC, r.AvgErrorPercent,
        }),
        ranking = new
        {
            hashrate = Best(s, RankingMode.MaxHashrate),
            efficiency = Best(s, RankingMode.Efficiency),
            balanced = Best(s, RankingMode.Balanced),
        },
    };

    private static object? Best(BenchmarkSession s, RankingMode mode) =>
        ResultRanking.Best(s.Results, mode) is { } r ? new { r.FrequencyMhz, r.CoreVoltageMv, r.AvgHashRateGh, r.EfficiencyJth } : null;

    /// <summary>Verlauf: 1 h aus dem Speicher, längere Zeiträume aus history.db; dazu Tuning-Markierungen.</summary>
    public static object History(MinerHub hub, string host, string range, DateTime now)
    {
        var span = range switch { "24h" => TimeSpan.FromHours(24), "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(1) };
        IEnumerable<Sample> samples;
        if (range is "24h" or "7d" or "30d" && hub.History is { } db)
            samples = db.Query(host, now - span, now);
        else if (host == HistoryStore.AggregateHost)
            samples = hub.AggregateHistory;
        else
            samples = hub.Device(host)?.State.History ?? (IEnumerable<Sample>)[];
        var events = host == HistoryStore.AggregateHost || hub.History is null
            ? [] : hub.History.QueryTuningEvents(host, now - span, now);
        return new
        {
            range,
            samples = samples.Select(x => new[] { Unix(x.Time), R(x.HashRateGh), R(x.Temp), R(x.Power) }),
            tuning = events.Select(e => new { time = Unix(e.Time), source = e.SourceText, change = e.ChangeText, e.Note }),
        };
    }

    public static object Comparison(TuningComparisonRow r) => new
    {
        time = r.Time, source = r.Source, change = r.Change, before = r.BeforeText, after = r.AfterText, delta = r.DeltaText,
    };

    public static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static double R(double v) => Math.Round(v, 2);
}
