using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Plugs;
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
        // Mit Smart Plugs: Kosten und Gesamteffizienz aus dem Wert an der Steckdose
        var energy = hub.Config.Plugs.Items.Count > 0 ? hub.CurrentEnergy() : null;
        var costPower = energy is { FromPlugs: true } ? energy.TotalPowerW : power;
        return new
        {
            time = now,
            running = hub.IsRunning,
            totals = new
            {
                hashrate = hash,
                power,
                wallPower = energy is { FromPlugs: true } ? energy.TotalPowerW : (double?)null,
                overhead = energy?.OverheadW,
                efficiency = hash > 1 ? power / (hash / 1000.0) : (double?)null,
                wallEfficiency = energy is { FromPlugs: true } && hash > 1 ? costPower / (hash / 1000.0) : (double?)null,
                online = online.Count,
                count = devices.Count,
                maxTemp = online.Count > 0 ? online.Max(i => i.temp) : (double?)null,
                costPerDay = costPower * 24 / 1000.0 * EnergyCost.CurrentCt(hub.Config, price) / 100.0,
                currency = hub.Config.Currency,
            },
            price = price is null ? null : new { source = hub.Prices.SourceName, ct = price },
            history = hub.AggregateHistory.TakeLast(360).Select(s => new[] { Unix(s.Time), R(s.HashRateGh), R(s.Temp), R(s.Power) }).ToList(),
            devices = devices.Select(d => Summary(hub, d, role)).ToList(),
            fans = Fans(hub, role),
            plugs = Plugs(hub, role),
            // Einführung „Erste Schritte“ – nur für Admins und nur solange nicht ausgeblendet
            // „Neu in dieser Version“ nach einem Update – nur für Admins, bis gesehen oder abgelehnt
            whatsNew = role == Role.Admin && WhatsNew.ShouldAsk(hub.Config, Endpoints.Version, server: true)
                ? new
                {
                    version = Endpoints.Version,
                    features = WhatsNew.Since(hub.Config.LastSeenVersion, Endpoints.Version, server: true)
                        .Select(f => new { f.Version, f.Title, f.Text, f.Section }).ToList(),
                }
                : null,
            onboarding = role == Role.Admin && Onboarding.ShouldShow(hub.Config)
                ? Onboarding.Steps(hub.Config, server: true).Select(o => new { o.Id, o.Title, o.Text, o.Done, o.Section }).ToList()
                : null,
        };
    }

    /// <summary>Smart Plugs; null, wenn keine eingerichtet. Adressen und Fehlertexte nur für Admins.</summary>
    public static object? Plugs(MinerHub hub, Role role)
    {
        if (hub.Config.Plugs.Items.Count == 0) return null;
        var admin = role == Role.Admin;
        return hub.PlugStatuses().Select(p => new
        {
            p.Id, p.Name, p.Role, p.Online,
            powerW = R(p.PowerW), energyKwh = p.EnergyWh is { } e ? Math.Round(e / 1000, 3) : (double?)null,
            p.Voltage, p.Current, minerPowerW = R(p.MinerPowerW), overheadW = R(p.OverheadW),
            miners = p.Miners.Select(DeviceId).ToList(),
            host = admin ? p.Host : null,
            model = p.Model,
            error = admin ? p.Error : null,
            p.Updated,
        }).ToList();
    }

    /// <summary>Zusatzlüfter; null, wenn nicht eingeschaltet.</summary>
    public static object? Fans(MinerHub hub, Role role)
    {
        var f = hub.FanStatus;
        if (!f.Enabled && !hub.Config.Display.Enabled) return null;
        return new
        {
            connected = f.Connected,
            @override = f.Override.ToString(),
            caseTemp = f.CaseTemp,
            sensors = (f.Sensors ?? []).Select(s => new { s.Id, s.Name, s.Temp, s.WarnTemp, s.Hot, s.CaseFans, s.ShowOnDisplay }).ToList(),
            device = role == Role.Admin ? f.Device : null,
            error = f.Error,
            updated = f.Updated,
            channels = f.Channels.Where(c => c.Role != "none").Select(c => new
            {
                c.Channel, c.Name, c.Role, c.Mode, c.Percent, c.Rpm, c.Reason, c.Stalled,
                minerId = c.MinerHost is { Length: > 0 } h ? DeviceId(h) : null,
            }).ToList(),
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
            wallPower = i is not null && hub.Config.Plugs.Items.Count > 0 ? R(hub.CurrentEnergy().WallPowerOf(d.Host)) : null,
            efficiency = n?.EfficiencyJth,
            frequency = n?.FrequencyMhz,
            voltage = n?.CoreVoltageMv,
            fanRpm = i?.fanrpm,
            fanPercent = i?.fanspeed,
            uptimeSeconds = i?.uptimeSeconds,
            sharesAccepted = i?.sharesAccepted,
            sharesRejected = i?.sharesRejected,
            bestDiff = i?.bestDiff,
            poolDifficulty = n?.PoolDifficulty,
            errorPercent = n?.ErrorPercent,
            pool = admin ? hub.PoolText(s) : (i is null ? null : i.isUsingFallbackStratum != 0 ? L.T("Fallback-Pool") : L.T("Primär-Pool")),
            // Enthält den Pool-Benutzer (Wallet-Adresse) – daher nur für Admins
            poolLink = admin && !d.IsSimulated && PoolQuickLinks.For(i) is { } pl ? new { name = pl.Pool, url = pl.Url.AbsoluteUri } : null,
            automation = d.AutomationStatus,
            soak = d.Config.Soak is null ? null : new { status = d.SoakStatus, until = d.Config.Soak.Until },
            suggestion = d.PendingSuggestion,
            benchmark = d.Benchmark is { } b ? Run(b) : null,
            fan = hub.FanStatus.Channels.FirstOrDefault(c => c.Role == "miner" && string.Equals(c.MinerHost?.Trim(), d.Config.Host.Trim(), StringComparison.OrdinalIgnoreCase)) is { } fc
                ? new { fc.Channel, fc.Percent, fc.Rpm, fc.Reason, fc.Stalled, fc.Mode } : null,
            history = s.History.TakeLast(120).Select(x => new[] { Unix(x.Time), R(x.HashRateGh), R(x.Temp), R(x.Power) }).ToList(),
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
            profiles = admin ? hub.ProfilesFor(d).Select(x => new { x.Id, x.Name }).ToList() : null,
            benchmarkDefaults = admin ? BenchmarkSettings.FromProfile(p) : null,
            estimatedDuration = BenchmarkManager.EstimatedDurationText(BenchmarkSettings.FromProfile(p)),
            session = session is null ? null : Session(session),
            config = admin ? new
            {
                c.Name, c.Host, c.WalletAddress, c.Coin, c.FirmwareRepo, c.LogAlerts, c.ProfileId,
                presets = Copy(c.Presets),
                thermalGuard = Copy(c.ThermalGuard),
                thermalGuardApproved = c.ThermalGuard.IsApproved(d.Host),
                schedule = Copy(c.Schedule),
                scheduleApproved = c.Schedule.IsApproved(d.Host),
            } : null,
            log = admin ? d.LogLines.TakeLast(300).ToList() : null,
        };
    }

    public static object Session(BenchmarkSession s) => new
    {
        s.Id, s.StartedAt, s.FinishedAt, s.FinishReason, s.IsFinished, s.DeviceModel, settings = Copy(s.Settings),
        results = s.Results.Select(r => new
        {
            r.FrequencyMhz, r.CoreVoltageMv, outcome = r.Outcome.ToString(), r.OutcomeText, r.IsStable, r.Message,
            r.AvgHashRateGh, r.ExpectedHashRateGh, r.AvgPowerW, r.EfficiencyJth, r.MaxChipTempC, r.MaxVrTempC, r.AvgErrorPercent,
        }).ToList(),
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
            samples = samples.Select(x => new[] { Unix(x.Time), R(x.HashRateGh), R(x.Temp), R(x.Power) }).ToList(),
            tuning = events.Select(e => new { time = Unix(e.Time), source = e.SourceText, change = e.ChangeText, e.Note }).ToList(),
        };
    }

    /// <summary>
    /// Verlauf eines Smart Plugs aus history.db und zum Vergleich die AxeOS-Leistung dahinter:
    /// Summe der zugeordneten Miner (Rolle „miners“) bzw. aller Miner (Gesamtmessung). Gleiche Zeitfenster wie der Plug.
    /// </summary>
    public static object PlugHistory(MinerHub hub, string id, string range, DateTime now)
    {
        var plug = hub.Config.Plugs.Items.FirstOrDefault(p => p.Id == id)
                   ?? throw new LocalizedException("Smart Plug nicht gefunden.") { Status = 404 };
        var span = range switch { "1h" => TimeSpan.FromHours(1), "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };
        var from = now - span;
        if (hub.History is not { } db) return new { range, plug = Array.Empty<double[]>(), axeos = (List<double[]>?)null };
        var hosts = plug.Role switch
        {
            "miners" => plug.Miners,
            "total" => [HistoryStore.AggregateHost],
            _ => [],
        };
        List<double[]>? axeos = null;
        if (hosts.Count > 0)
            axeos = hosts.SelectMany(h => db.Query(h, from, now))
                .GroupBy(s => Unix(s.Time)).OrderBy(g => g.Key)
                .Select(g => new[] { g.Key, R(g.Sum(s => s.Power)) }).ToList();
        return new
        {
            range,
            plug = db.QueryPlug(id, from, now).Select(p => new[] { Unix(p.Time), R(p.PowerW) }).ToList(),
            axeos,
        };
    }

    public static object Comparison(TuningComparisonRow r) => new
    {
        time = r.Time, source = r.Source, change = r.Change, before = r.BeforeText, after = r.AfterText, delta = r.DeltaText,
    };

    /// <summary>
    /// Unabhängige Kopie für die Antwort. Antworten werden erst nach dem Verlassen des Hub-Kontexts serialisiert –
    /// alles, was der Hub währenddessen ändern könnte, muss vorher kopiert bzw. als Liste festgeschrieben sein.
    /// </summary>
    public static T Copy<T>(T value) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;

    public static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static double R(double v) => Math.Round(v, 2);
    private static double? R(double? v) => v is { } x ? Math.Round(x, 2) : null;
}
