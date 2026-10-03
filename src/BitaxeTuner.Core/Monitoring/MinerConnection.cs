using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Einziger Zugang zu einem Miner – für Überwachung, Watchdog, Tuning und Benchmark gleichermaßen.
/// <list type="bullet">
/// <item>Nie mehr als eine HTTP-Anfrage gleichzeitig an dasselbe Gerät (Semaphore).</item>
/// <item>Ein Info-Abruf, der jünger als <see cref="CacheAge"/> ist, wird wiederverwendet –
///       Überwachung und Benchmark pollen den Miner so nicht doppelt.</item>
/// <item>Jede Frequenz-/Spannungsänderung wird mit altem und neuem Wert gemeldet (<see cref="TuningApplied"/>).</item>
/// <item>Änderungen und Neustarts öffnen ein Wartungsfenster (<see cref="MaintenanceTracker"/>).</item>
/// </list>
/// </summary>
public sealed class MinerConnection : IMinerClient, IDisposable
{
    public static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(2);

    private readonly IMinerClient _inner;
    private readonly MaintenanceTracker _maintenance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _now;
    private MinerInfo? _last;
    private DateTime _lastAt = DateTime.MinValue;
    private int _inFlight;

    public MinerConnection(IMinerClient inner, MaintenanceTracker maintenance, Func<DateTime>? utcNow = null)
    {
        _inner = inner;
        _maintenance = maintenance;
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Address => _inner.Address;
    public IMinerClient Inner => _inner;

    /// <summary>Letzte erfolgreiche Antwort (auch für Anzeigen ohne neuen Abruf).</summary>
    public MinerInfo? Last => _last;

    /// <summary>Höchste gleichzeitige Anfragezahl seit Start – für Tests (muss 1 bleiben).</summary>
    public int MaxConcurrentRequests { get; private set; }

    /// <summary>Wird nach jeder erfolgreich gesendeten Frequenz-/Spannungsänderung ausgelöst.</summary>
    public event Action<TuningEvent>? TuningApplied;

    public bool InMaintenance => _maintenance.IsActive(Address);

    /// <summary>Offenes Wartungsfenster, z. B. für einen ganzen Benchmark-Lauf.</summary>
    public IDisposable BeginMaintenance(string reason) => _maintenance.Begin(Address, reason);

    public async Task<MinerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        if (_last is { } cached && _now() - _lastAt < CacheAge) return cached;
        return await RunAsync(async () =>
        {
            // Während wir auf das Gate gewartet haben, hat evtl. jemand anderes frisch abgefragt.
            if (_last is { } fresh && _now() - _lastAt < CacheAge) return fresh;
            var info = await _inner.GetInfoAsync(ct).ConfigureAwait(false);
            _last = info;
            _lastAt = _now();
            return info;
        }, ct).ConfigureAwait(false);
    }

    public Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) =>
        RunAsync(() => _inner.GetAsicInfoAsync(ct), ct);

    /// <summary>Log-Puffer – ebenfalls über das Gate, also nie parallel zu anderen Anfragen an diesen Miner.</summary>
    public Task<string> GetLogBufferAsync(CancellationToken ct = default) =>
        RunAsync(() => _inner.GetLogBufferAsync(ct), ct);

    /// <summary>Live-Log-Quelle für diesen Miner (WebSocket bzw. Simulation).</summary>
    public IMinerLogSource CreateLogSource() => LogSourceOverride?.Invoke() ?? (_inner is Simulation.SimulatedMinerClient sim
        ? new SimulatedLogSource(sim)
        : new WebSocketLogSource(Address, () => BootTime));

    /// <summary>Nur für Tests: eigene Log-Quelle.</summary>
    internal Func<IMinerLogSource>? LogSourceOverride { get; set; }

    private LogHub? _logs;

    /// <summary>Gemeinsame Live-Logs (eine WebSocket-Verbindung für Log-Tab und Log-Alarme).</summary>
    public LogHub Logs => _logs ??= new LogHub(CreateLogSource);

    /// <summary>Unveränderte Antwort von /api/system/info (für Einstellungs-Sicherungen).</summary>
    public Task<string> GetRawInfoAsync(CancellationToken ct = default) =>
        RunAsync(() => _inner.GetRawInfoAsync(ct), ct);

    /// <summary>
    /// Beliebige Einstellungen per PATCH /api/system (Lüfter, Pool …). Frequenz und Spannung bitte über
    /// <see cref="ApplySettingsAsync"/>, damit sie protokolliert werden.
    /// </summary>
    public Task PatchSettingsAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default)
    {
        _maintenance.Extend(Address, MaintenanceTracker.DefaultTail);
        return RunAsync(async () =>
        {
            await _inner.PatchSettingsAsync(values, ct).ConfigureAwait(false);
            _lastAt = DateTime.MinValue;
            return true;
        }, ct);
    }

    /// <summary>Startzeit des Miners aus der letzten Abfrage (Abfragezeit − Uptime); rechnet Log-Zeitstempel in Uhrzeit um.</summary>
    public DateTime? BootTime =>
        _last is { UptimeSeconds: > 0 } l && _lastAt > DateTime.MinValue.AddYears(1)
            ? _lastAt.ToLocalTime().AddSeconds(-l.UptimeSeconds)
            : null;

    public Task<bool> WillEnableOverclockAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default) =>
        RunAsync(() => _inner.WillEnableOverclockAsync(frequencyMhz, coreVoltageMv, ct), ct);

    public Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, TuningSource source = TuningSource.Manual, CancellationToken ct = default) =>
        ApplySettingsAsync(frequencyMhz, coreVoltageMv, source, null, ct);

    /// <param name="note">Begründung für das Protokoll, z. B. "Temperaturschutz: 67 °C".</param>
    public async Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, TuningSource source, string? note, CancellationToken ct = default)
    {
        // Alter Wert aus einer frischen Abfrage, damit das Protokoll stimmt.
        int? oldFreq = null, oldMv = null;
        try
        {
            var before = await GetInfoAsync(ct).ConfigureAwait(false);
            oldFreq = before.FrequencyMhz;
            oldMv = before.CoreVoltageMv;
        }
        catch (MinerApiException) { /* alter Wert unbekannt – Änderung trotzdem protokollieren */ }

        _maintenance.Extend(Address, MaintenanceTracker.DefaultTail);
        await RunAsync(async () =>
        {
            await _inner.ApplySettingsAsync(frequencyMhz, coreVoltageMv, source, ct).ConfigureAwait(false);
            _lastAt = DateTime.MinValue; // nächster Abruf zeigt den neuen Stand
            return true;
        }, ct).ConfigureAwait(false);

        TuningApplied?.Invoke(new TuningEvent(Address, DateTime.Now, source, oldFreq, oldMv, frequencyMhz, coreVoltageMv, note));
    }

    public Task SetFanMinAsync(int percent, CancellationToken ct = default) =>
        RunAsync(async () => { await _inner.SetFanMinAsync(percent, ct).ConfigureAwait(false); return true; }, ct);

    public Task SetFanTargetAsync(int targetTempC, CancellationToken ct = default) =>
        RunAsync(async () => { await _inner.SetFanTargetAsync(targetTempC, ct).ConfigureAwait(false); return true; }, ct);

    public Task SetFanAsync(int autoFanMode, int manualPercent, CancellationToken ct = default) =>
        RunAsync(async () => { await _inner.SetFanAsync(autoFanMode, manualPercent, ct).ConfigureAwait(false); return true; }, ct);

    public async Task RestartAsync(CancellationToken ct = default)
    {
        // Gewollter Neustart: Watchdog und Offline-Meldung sollen die Nullphase nicht als Störung werten.
        _maintenance.Extend(Address, MaintenanceTracker.DefaultTail);
        await RunAsync(async () =>
        {
            await _inner.RestartAsync(ct).ConfigureAwait(false);
            _lastAt = DateTime.MinValue;
            return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var now = Interlocked.Increment(ref _inFlight);
        if (now > MaxConcurrentRequests) MaxConcurrentRequests = now;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _logs?.Dispose();
        (_inner as IDisposable)?.Dispose();
        _gate.Dispose();
    }
}
