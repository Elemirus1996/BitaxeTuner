using System.Globalization;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Eine zu versendende Meldung. <see cref="Key"/> dient der Sperrzeit im NotificationService.</summary>
public sealed record Alert(string Key, string Title, string Message, NotifyPriority Priority, TimeSpan Cooldown);

// ---------------------------------------------------------------------------------------------
// 1. Log-Alarme
// ---------------------------------------------------------------------------------------------

/// <summary>Prüft Miner-Logzeilen gegen die eingestellten Regeln.</summary>
public sealed class LogAlertRules
{
    private readonly bool _onErrors;
    private readonly List<(string Pattern, Regex Regex)> _patterns = new();

    public LogAlertRules(LogAlertSettings settings)
    {
        _onErrors = settings.OnErrors;
        foreach (var p in settings.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try { _patterns.Add((p, new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))); }
            catch (ArgumentException) { InvalidPatterns.Add(p); }
        }
    }

    /// <summary>Muster, die kein gültiger regulärer Ausdruck sind (werden ignoriert).</summary>
    public List<string> InvalidPatterns { get; } = new();

    /// <summary>Name der passenden Regel oder null.</summary>
    public string? Match(LogLine line)
    {
        if (line.Level == LogLevel.App) return null; // eigene Markierungen der App
        var text = line.Tag.Length > 0 ? $"{line.Tag}: {line.Message}" : line.Message;
        foreach (var (pattern, regex) in _patterns)
        {
            try { if (regex.IsMatch(text)) return pattern; }
            catch (RegexMatchTimeoutException) { }
        }
        return _onErrors && line.Level == LogLevel.Error ? "Fehlerzeile (E)" : null;
    }
}

/// <summary>
/// Liest für Miner mit aktivierten Log-Alarmen dauerhaft die Logs mit (über den gemeinsamen <see cref="LogHub"/>)
/// und meldet Treffer. Während eines Wartungsfensters (Tuning, gewollter Neustart) wird nichts gemeldet.
/// </summary>
public sealed class LogAlertService : IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly MaintenanceTracker _maintenance;
    private readonly Action<Alert> _send;
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastTriggered = new();
    private readonly Func<DateTime> _now;
    private LogAlertRules _rules;

    public LogAlertService(Func<AppConfig> config, MaintenanceTracker maintenance, Action<Alert> send, Func<DateTime>? utcNow = null)
    {
        _now = utcNow ?? (() => DateTime.UtcNow);
        _config = config;
        _maintenance = maintenance;
        _send = send;
        _rules = new LogAlertRules(config().LogAlerts);
    }

    /// <summary>Ausgelöste Meldungen (auch wenn Push aus ist), z. B. für die Statuszeile.</summary>
    public event Action<string, LogLine, string>? Triggered;

    public IReadOnlyCollection<string> ActiveHosts => _subscriptions.Keys;

    /// <summary>An Geräteliste und Einstellungen angleichen.</summary>
    public void Sync(IEnumerable<(DeviceConfig Device, MinerConnection Connection)> devices)
    {
        _rules = new LogAlertRules(_config().LogAlerts);
        var wanted = devices.Where(d => d.Device.LogAlerts).ToDictionary(d => d.Device.Host, StringComparer.OrdinalIgnoreCase);

        foreach (var host in _subscriptions.Keys.Where(h => !wanted.ContainsKey(h)).ToList())
        {
            _subscriptions[host].Dispose();
            _subscriptions.Remove(host);
        }
        foreach (var (host, d) in wanted)
        {
            if (_subscriptions.ContainsKey(host)) continue;
            var connection = d.Connection;
            _subscriptions[host] = connection.Logs.Subscribe(line => OnLine(connection, line));
        }
    }

    internal void OnLine(MinerConnection connection, LogLine line)
    {
        var rule = _rules.Match(line);
        if (rule is null) return;
        var host = connection.Address;
        if (_maintenance.IsActive(host)) return;

        // Dieselbe Regel je Miner höchstens einmal je Sperrzeit – auch im App-Protokoll, sonst flutet eine häufige Zeile alles
        var key = $"log:{host}:{rule}";
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _config().LogAlerts.CooldownMinutes));
        lock (_lastTriggered)
        {
            if (_lastTriggered.TryGetValue(key, out var last) && _now() - last < cooldown) return;
            _lastTriggered[key] = _now();
        }

        var device = _config().Devices.FirstOrDefault(d => string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase));
        var name = device?.Name ?? host;
        Triggered?.Invoke(host, line, rule);

        if (!_config().Notifications.OnLogAlerts) return;
        _send(new Alert(key, $"{name}: Log-Meldung",
            $"{line.LevelText} {line.Tag}: {Shorten(line.Message, 300)}",
            line.Level == LogLevel.Error ? NotifyPriority.High : NotifyPriority.Normal, cooldown));
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        foreach (var s in _subscriptions.Values) s.Dispose();
        _subscriptions.Clear();
    }
}

// ---------------------------------------------------------------------------------------------
// 2. Pool- und Share-Überwachung
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Wertet je Miner Pool-Wechsel (Fallback), Ablehnungsquote im Zeitfenster und Pool-Antwortzeit aus.
/// Zähler-Sprünge nach einem Neustart setzen das Fenster zurück.
/// </summary>
public sealed class PoolWatch
{
    private sealed class HostState
    {
        public readonly Queue<(DateTime Time, double Accepted, double Rejected)> Shares = new();
        public readonly Queue<(DateTime Time, double Ms)> Response = new();
        public int? Fallback;
    }

    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Ablehnungsquote im Fenster in %, null wenn zu wenige Shares.</summary>
    public double? RejectRate(string host, PoolWatchSettings s)
    {
        if (!_hosts.TryGetValue(host, out var st) || st.Shares.Count < 2) return null;
        var first = st.Shares.Peek();
        var last = st.Shares.Last();
        var acc = last.Accepted - first.Accepted;
        var rej = last.Rejected - first.Rejected;
        return acc + rej >= Math.Max(1, s.MinShares) ? rej / (acc + rej) * 100 : null;
    }

    public double? AverageResponse(string host) =>
        _hosts.TryGetValue(host, out var st) && st.Response.Count > 0 ? st.Response.Average(r => r.Ms) : null;

    /// <summary>Kurzbeschreibung für die Anzeige, z. B. "Pool: … · Primär · Antwort 38 ms · Ablehnung 30 min 0,4 %".</summary>
    public string StatusText(string host, SystemInfo info, PoolWatchSettings s)
    {
        var parts = new List<string>();
        var fallback = info.isUsingFallbackStratum != 0;
        var url = fallback && !string.IsNullOrWhiteSpace(info.fallbackStratumURL)
            ? $"{info.fallbackStratumURL}:{info.fallbackStratumPort}"
            : $"{info.stratumURL}:{info.stratumPort}";
        parts.Add($"Pool: {url}");
        parts.Add(fallback ? "FALLBACK aktiv" : "Primär-Pool");
        if (AverageResponse(host) is { } ms && ms > 0) parts.Add($"Antwort Ø {ms.ToString("0", De)} ms");
        parts.Add(RejectRate(host, s) is { } r
            ? $"abgelehnt {s.WindowMinutes} min: {r.ToString("0.0", De)} %"
            : $"abgelehnt {s.WindowMinutes} min: – (zu wenige Shares)");
        return string.Join(" · ", parts);
    }

    /// <summary>Neuen Messwert aufnehmen und ggf. Meldungen liefern.</summary>
    public List<Alert> Evaluate(string host, string name, SystemInfo info, DateTime now, PoolWatchSettings s, bool inMaintenance)
    {
        var alerts = new List<Alert>();
        if (!_hosts.TryGetValue(host, out var st)) _hosts[host] = st = new HostState();
        var window = TimeSpan.FromMinutes(Math.Max(1, s.WindowMinutes));

        // Shares: nach Neustart (Zähler kleiner) Fenster neu beginnen
        if (st.Shares.Count > 0 && (info.sharesAccepted < st.Shares.Last().Accepted || info.sharesRejected < st.Shares.Last().Rejected))
            st.Shares.Clear();
        st.Shares.Enqueue((now, info.sharesAccepted, info.sharesRejected));
        while (st.Shares.Count > 2 && now - st.Shares.Peek().Time > window) st.Shares.Dequeue();

        // Antwortzeit: gleitender Mittelwert über das Fenster, nur echte Werte
        if (info.responseTime > 0) st.Response.Enqueue((now, info.responseTime));
        while (st.Response.Count > 0 && now - st.Response.Peek().Time > window) st.Response.Dequeue();

        var fallbackBefore = st.Fallback;
        st.Fallback = info.isUsingFallbackStratum;

        if (!s.Enabled || inMaintenance) return alerts;

        if (fallbackBefore is 0 && info.isUsingFallbackStratum != 0)
            alerts.Add(new Alert($"pool-fallback:{host}", $"{name}: Fallback-Pool aktiv",
                $"Der Primär-Pool ({info.stratumURL}:{info.stratumPort}) ist nicht erreichbar, {name} mined jetzt auf " +
                $"{info.fallbackStratumURL}:{info.fallbackStratumPort}.", NotifyPriority.High, TimeSpan.FromHours(1)));
        else if (fallbackBefore is { } f && f != 0 && info.isUsingFallbackStratum == 0)
            alerts.Add(new Alert($"pool-primary:{host}", $"{name}: wieder auf Primär-Pool",
                $"{info.stratumURL}:{info.stratumPort}", NotifyPriority.Normal, TimeSpan.FromMinutes(5)));

        if (RejectRate(host, s) is { } rate && rate > s.RejectPercent)
            alerts.Add(new Alert($"pool-reject:{host}", $"{name}: viele abgelehnte Shares",
                $"{rate.ToString("0.0", De)} % abgelehnt in den letzten {s.WindowMinutes} min (Grenze {s.RejectPercent.ToString("0.#", De)} %).",
                NotifyPriority.High, TimeSpan.FromHours(2)));

        // Antwortzeit erst bewerten, wenn das Fenster zur Hälfte gefüllt ist
        if (s.ResponseMs > 0 && AverageResponse(host) is { } avg && avg > s.ResponseMs &&
            st.Response.Count > 0 && now - st.Response.Peek().Time >= window / 2)
            alerts.Add(new Alert($"pool-slow:{host}", $"{name}: Pool antwortet langsam",
                $"Ø {avg.ToString("0", De)} ms in den letzten {s.WindowMinutes} min (Grenze {s.ResponseMs.ToString("0", De)} ms).",
                NotifyPriority.Normal, TimeSpan.FromHours(3)));

        return alerts;
    }

    public void Forget(string host) => _hosts.Remove(host);
}

// ---------------------------------------------------------------------------------------------
// 4. Tagesbericht
// ---------------------------------------------------------------------------------------------

public static class DailyReport
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Soll jetzt ein Bericht gesendet werden?</summary>
    public static bool IsDue(DailyReportSettings s, DateTime now) =>
        s.Enabled && now.Hour >= Math.Clamp(s.Hour, 0, 23) && s.LastSent != now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Bericht über die letzten 24 h aus history.db.</summary>
    public static (string Title, string Message) Build(HistoryStore history, AppConfig config,
        IReadOnlyList<(string Name, string Host)> miners, DateTime now)
    {
        var from = now.AddHours(-24);
        var lines = new List<string>();
        double totalHash = 0, totalPower = 0;

        foreach (var (name, host) in miners)
        {
            var avg = history.Average(host, from, now);
            var availability = history.Availability(host, from);
            if (avg is null)
            {
                lines.Add($"{name}: keine Daten{(availability is { } a0 ? $" (verfügbar {(a0 * 100).ToString("0", De)} %)" : "")}");
                continue;
            }
            totalHash += avg.HashRateGh;
            totalPower += avg.Power;
            var eff = avg.HashRateGh > 0 ? avg.Power / (avg.HashRateGh / 1000.0) : 0;
            var changes = history.QueryTuningEvents(host, from, now).Count(e => e.Source != TuningSource.Benchmark);
            lines.Add($"{name}: {FormatHash(avg.HashRateGh)} · {eff.ToString("0.0", De)} J/TH · {avg.Temp.ToString("0", De)} °C" +
                      (availability is { } a ? $" · verfügbar {(a * 100).ToString("0.0", De)} %" : "") +
                      (changes > 0 ? $" · {changes} Tuning-Änderung(en)" : ""));
        }

        var kwh = totalPower * 24 / 1000.0;
        var cost = kwh * config.ElectricityCtPerKwh / 100.0;
        var best = history.GetBestDiffs()
            .Where(r => miners.Any(m => m.Host == r.Host))
            .OrderByDescending(r => r.Value).FirstOrDefault();

        var header = $"Gesamt Ø {FormatHash(totalHash)} · {totalPower.ToString("0.0", De)} W · " +
                     $"{kwh.ToString("0.00", De)} kWh ≈ {cost.ToString("0.00", De)} {config.Currency}";
        if (best is not null)
            header += $"\nBest Diff (Rekord): {best.Raw} ({miners.First(m => m.Host == best.Host).Name}, {best.AchievedAt.ToString("dd.MM.", De)})";

        return ($"Tagesbericht {now.ToString("dd.MM.yyyy", De)}", header + "\n\n" + string.Join("\n", lines));
    }

    private static string FormatHash(double gh) =>
        gh >= 1000 ? (gh / 1000).ToString("0.00", De) + " TH/s" : gh.ToString("0", De) + " GH/s";
}
