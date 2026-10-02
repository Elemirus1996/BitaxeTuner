using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// Live-Ereignisse für Browser und Desktop-App (Server-Sent Events unter /api/v1/events).
/// Die Ereignisse entstehen im Hub-Kontext; je Rolle wird der Stand nur einmal gebaut.
/// <list type="bullet">
/// <item><c>status</c> – Gesamtstand nach jeder Abfragerunde</item>
/// <item><c>device</c> – Profil/Automatik/Dauertest eines Geräts geändert (Client lädt Details neu)</item>
/// <item><c>benchmark</c> – Fortschritt eines Benchmarks</item>
/// <item><c>tuning</c> – protokollierte Frequenz-/Spannungsänderung</item>
/// <item><c>message</c> – Statuszeile, <c>notification</c> – versendete Push-Meldung (nur Admin)</item>
/// <item><c>log</c> – Protokollzeile eines Geräts (nur Admin)</item>
/// </list>
/// </summary>
public sealed class EventStream : IDisposable
{
    private sealed record Client(Role Role, ViewScope Scope, Channel<string> Channel);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly HubService _hub;
    private readonly List<Action> _unsubscribe = new();
    private readonly HashSet<HubDevice> _logSubscribed = new();

    public EventStream(HubService hub)
    {
        _hub = hub;
        // Anmeldung an Hub-Ereignisse im Hub-Kontext; nach einer Datenübernahme am neuen Motor
        hub.RunAsync(h => { Attach(h); return true; }).GetAwaiter().GetResult();
        hub.HubReplaced += h =>
        {
            Detach();
            Attach(h);
            Publish("reload", new { reason = "Daten übernommen" }, Role.Viewer);
        };
    }

    private void Attach(MinerHub h)
    {
        Action polled = OnPolled;
        Action<HubDevice> changed = d => Publish("device", new { id = Dto.DeviceId(d.Host) }, Role.Viewer, device: d);
        Action<HubDevice, BenchmarkProgress> progress = (d, _) => { if (d.Benchmark is { } b) PublishBenchmark(d, b); };
        Action<HubDevice> benchState = d => { if (d.Benchmark is { } b) PublishBenchmark(d, b); };
        Action<bool, string> message = (ok, text) => Publish("message", new { ok, text }, Role.Admin);
        Action<TuningEvent> tuning = e => Publish("tuning", new { id = Dto.DeviceId(e.Host), change = e.ChangeText, source = e.SourceText }, Role.Viewer,
            device: h.Device(e.Host));
        Action<string, string, string, NotifyPriority> sent = (_, title, text, prio) =>
            Publish("notification", new { title, text, priority = prio.ToString(), time = DateTime.Now }, Role.Admin);
        Action devices = () => SubscribeLogs(h);

        h.Polled += polled;
        h.DeviceChanged += changed;
        h.Benchmarks.Progress += progress;
        h.Benchmarks.StateChanged += benchState;
        h.StatusMessage += message;
        h.TuningApplied += tuning;
        h.Notify.Sending += sent;
        h.DevicesChanged += devices;
        _logSubscribed.Clear();
        SubscribeLogs(h);
        _unsubscribe.Add(() =>
        {
            h.Polled -= polled;
            h.DeviceChanged -= changed;
            h.Benchmarks.Progress -= progress;
            h.Benchmarks.StateChanged -= benchState;
            h.StatusMessage -= message;
            h.TuningApplied -= tuning;
            h.Notify.Sending -= sent;
            h.DevicesChanged -= devices;
        });
    }

    private void Detach()
    {
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
    }

    public int ClientCount => _clients.Count;

    private void SubscribeLogs(MinerHub hub)
    {
        foreach (var d in hub.Devices.Where(d => _logSubscribed.Add(d)))
        {
            var id = Dto.DeviceId(d.Host);
            d.LogAdded += line => Publish("log", new { id, line }, Role.Admin);
        }
    }

    private void OnPolled()
    {
        if (_clients.IsEmpty) return;
        var now = DateTime.Now;
        if (_clients.Values.Any(c => c.Role == Role.Admin)) Publish("status", Dto.Status(_hub.Hub, Role.Admin, now), Role.Admin, only: true);
        // Nur ansehen: Stand je Freigabe (alle Miner bzw. je Gruppen-Auswahl) nur einmal bauen
        foreach (var scope in _clients.Values.Where(c => c.Role == Role.Viewer).Select(c => c.Scope).DistinctBy(s => s.Key).ToList())
            Publish("status", Dto.Status(_hub.Hub, Role.Viewer, now, scope), Role.Viewer, only: true, scopeKey: scope.Key);
    }

    private void PublishBenchmark(HubDevice d, BenchmarkRun b) =>
        Publish("benchmark", new { id = Dto.DeviceId(d.Host), run = Dto.Run(b) }, Role.Viewer, device: d);

    /// <param name="minimum">Mindestrolle der Empfänger.</param>
    /// <param name="only">Nur genau diese Rolle (für rollenabhängige Inhalte).</param>
    /// <param name="device">Ereignis eines Geräts: nur an Ansichten, die dieses Gerät sehen dürfen.</param>
    /// <param name="scopeKey">Inhalt für genau diese Freigabe (<see cref="ViewScope.Key"/>).</param>
    private void Publish(string type, object payload, Role minimum, bool only = false, HubDevice? device = null, string? scopeKey = null)
    {
        if (_clients.IsEmpty) return;
        var frame = $"event: {type}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n";
        foreach (var c in _clients.Values)
        {
            if (only ? c.Role != minimum : c.Role < minimum) continue;
            if (c.Role != Role.Admin && c.Scope.Restricted && device is not null && !c.Scope.Allows(device)) continue;
            if (scopeKey is not null && c.Scope.Key != scopeKey) continue;
            c.Channel.Writer.TryWrite(frame); // volle Warteschlange (langsamer Client): ältestes verwerfen
        }
    }

    /// <summary>Verbindung bedienen, bis der Client trennt.</summary>
    public async Task ServeAsync(HttpContext http, Role role, ViewScope? scope = null)
    {
        scope = role == Role.Admin ? ViewScope.All : scope ?? ViewScope.All;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _clients[id] = new Client(role, scope, channel);
        var ct = http.RequestAborted;
        try
        {
            // Sofort ein aktueller Stand, damit die Seite nicht auf die nächste Runde warten muss
            var status = await _hub.RunAsync(h => Dto.Status(h, role, DateTime.Now, scope));
            await http.Response.WriteAsync($"retry: 3000\nevent: status\ndata: {JsonSerializer.Serialize(status, Json)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                string frame;
                try { frame = await channel.Reader.ReadAsync(heartbeat.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = ": ping\n\n"; }
                await http.Response.WriteAsync(frame, ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* Client weg */ }
        catch (IOException) { /* Client weg */ }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        Detach();
        foreach (var c in _clients.Values) c.Channel.Writer.TryComplete();
    }
}
