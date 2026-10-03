using BitaxeTuner.Core.Config;
using System.Net.Http;
using System.Net.Http.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

public enum NotifyPriority
{
    Low = 2,
    Normal = 3,
    High = 4,
    Urgent = 5
}

/// <summary>
/// Push-Benachrichtigungen über ntfy, Telegram, Discord, Pushover oder einen eigenen Webhook – an ein oder mehrere
/// Ziele, je Ziel nur die gewünschten Bereiche und Miner (<see cref="PushTarget"/>).
///
/// Jede Meldung hat einen Schlüssel (z. B. "offline:192.168.1.50"). Innerhalb
/// der Sperrzeit wird derselbe Schlüssel nicht erneut gesendet, damit ein
/// wackelnder Miner nicht das Handy flutet. Fehler beim Senden werden
/// verschluckt und nur über <see cref="LastError"/> gemeldet.
/// </summary>
public sealed class NotificationService : IDisposable
{
    public static readonly string[] Providers = ["ntfy", "telegram", "discord", "pushover", "webhook"];

    private readonly HttpClient _http;
    private readonly Func<NotificationSettings> _settings;
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public string? LastError { get; private set; }

    /// <summary>Jede Meldung, die die Sperrzeit passiert hat (für Meldungsverlauf im Browser und Tests).</summary>
    public event Action<string, string, string, NotifyPriority>? Sending;

    /// <summary>Nur für Tests: statt ntfy/Telegram aufrufen (einmal je Ziel).</summary>
    internal Func<string, string, NotifyPriority, Task>? TransportOverride { get; set; }

    /// <summary>Nur für Tests: je gesendete Meldung das Ziel (Id) – prüft die Verteilung.</summary>
    internal event Action<string, string>? DeliveredTo;

    /// <summary>Gruppen eines Miners (vom Hub gesetzt) – für Push-Ziele mit Gruppenauswahl.</summary>
    public Func<string, IReadOnlyCollection<string>>? GroupsOf { get; set; }
    /// <summary>Für Tests: Ziel-ID, Schlüssel und tatsächlich gesendeter Text.</summary>
    internal event Action<string, string, string>? Delivered;

    public NotificationService(Func<NotificationSettings> settings) : this(settings, null) { }

    /// <summary>Mit eigenem Handler (Tests prüfen damit die gesendeten Anfragen).</summary>
    internal NotificationService(Func<NotificationSettings> settings, HttpMessageHandler? handler)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
    }

    public bool Enabled => _settings().EffectiveTargets().Any(t => t.Enabled && Providers.Contains(t.Provider));

    /// <summary>
    /// Sendet an alle aktiven Ziele, die den Bereich (und bei Miner-Meldungen den Miner) wollen – sofern nicht
    /// innerhalb der Sperrzeit schon gesendet. Fehler eines Ziels halten die anderen nicht auf.
    /// </summary>
    public Task SendAsync(string key, string title, string message,
                          NotifyPriority priority = NotifyPriority.Normal, TimeSpan? cooldown = null,
                          NotifyCategory category = NotifyCategory.Other, string? host = null) =>
        SendAsync(key, title, _ => message, priority, cooldown, category, host);

    /// <summary>
    /// Wie oben, aber mit eigenem Text je Ziel (Tages-/Monatsbericht je Push-Dienst angepasst). Liefert
    /// <paramref name="messageFor"/> null, bekommt dieses Ziel nichts.
    /// </summary>
    public async Task SendAsync(string key, string title, Func<PushTarget, string?> messageFor,
                                NotifyPriority priority = NotifyPriority.Normal, TimeSpan? cooldown = null,
                                NotifyCategory category = NotifyCategory.Other, string? host = null)
    {
        var targets = _settings().EffectiveTargets().Where(t => Providers.Contains(t.Provider) && t.Accepts(category, host, GroupsOf))
            .Select(t => (Target: t, Message: messageFor(t))).Where(x => x.Message is not null).ToList();
        if (targets.Count == 0) return;

        var wait = cooldown ?? TimeSpan.FromMinutes(30);
        lock (_lastSent)
        {
            if (_lastSent.TryGetValue(key, out var last) && DateTime.UtcNow - last < wait) return;
            _lastSent[key] = DateTime.UtcNow;
        }

        Sending?.Invoke(key, title, targets[0].Message!, priority);
        var errors = new List<string>();
        foreach (var (target, message) in targets)
        {
            try
            {
                await DeliverAsync(target, title, message!, priority);
                DeliveredTo?.Invoke(target.Id, key);
                Delivered?.Invoke(target.Id, key, message!);
            }
            catch (Exception ex)
            {
                errors.Add(targets.Count > 1 ? $"{target.Title}: {ex.Message}" : ex.Message);
                // Audit I1: später erneut versuchen statt verwerfen – die Sperrzeit bleibt, sonst käme die Meldung doppelt
                Enqueue(target.Id, title, message!, priority, DateTime.UtcNow);
            }
        }
        LastError = errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private Task DeliverAsync(PushTarget target, string title, string message, NotifyPriority priority) =>
        TransportOverride is { } transport ? transport(title, message, priority) : SendRawAsync(target, title, message, priority);

    // ---------- Wiederholung (Audit I1) ----------

    /// <summary>Nicht zugestellte Meldung für ein Ziel; wird mit wachsendem Abstand erneut versucht.</summary>
    public sealed record PendingPush(string TargetId, string Title, string Message, NotifyPriority Priority,
                                     DateTime CreatedUtc, int Attempts, DateTime NextTryUtc);

    /// <summary>Ältere Meldungen sind nicht mehr aktuell und werden verworfen.</summary>
    public static readonly TimeSpan RetryMaxAge = TimeSpan.FromHours(6);
    public const int RetryMaxEntries = 50;

    private readonly List<PendingPush> _queue = [];
    private bool _queueLoaded;
    private int _retrying;

    /// <summary>Datei der Warteschlange (Hub: push-queue.json im Datenordner) – übersteht Neustarts; null = nur im Speicher.</summary>
    public string? QueueFile { get; set; }

    /// <summary>Noch nicht zugestellte Meldungen.</summary>
    public IReadOnlyList<PendingPush> Pending
    {
        get { lock (_queue) { LoadQueue(); return _queue.ToList(); } }
    }

    /// <summary>Abstand bis zum nächsten Versuch: 1, 2, 4, 8, 16, dann alle 30 Minuten.</summary>
    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, Math.Max(0, attempts - 1))));

    private void Enqueue(string targetId, string title, string message, NotifyPriority priority, DateTime nowUtc)
    {
        lock (_queue)
        {
            LoadQueue();
            _queue.Add(new PendingPush(targetId, title, message, priority, nowUtc, 1, nowUtc + Backoff(1)));
            while (_queue.Count > RetryMaxEntries) _queue.RemoveAt(0);   // älteste zuerst verwerfen
            SaveQueue();
        }
    }

    /// <summary>
    /// Fällige Meldungen erneut senden (vom Hub etwa minütlich). Gelöschte oder abgeschaltete Ziele und Meldungen älter als
    /// <see cref="RetryMaxAge"/> werden verworfen. Liefert die Anzahl zugestellter Meldungen.
    /// </summary>
    public async Task<int> RetryPendingAsync(DateTime nowUtc)
    {
        if (Interlocked.Exchange(ref _retrying, 1) == 1) return 0;
        try
        {
            List<PendingPush> due;
            lock (_queue)
            {
                LoadQueue();
                if (_queue.RemoveAll(p => nowUtc - p.CreatedUtc > RetryMaxAge) > 0) SaveQueue();
                due = _queue.Where(p => p.NextTryUtc <= nowUtc).ToList();
            }
            if (due.Count == 0) return 0;

            var targets = _settings().EffectiveTargets();
            var delivered = 0;
            foreach (var p in due)
            {
                PendingPush? again = null;
                if (targets.FirstOrDefault(t => t.Id == p.TargetId && t.Enabled && Providers.Contains(t.Provider)) is { } target)
                {
                    var text = p.Message + "\n\n" + L.T("(verspätet zugestellt, ursprünglich {0})", L.Short(p.CreatedUtc.ToLocalTime()));
                    try
                    {
                        await DeliverAsync(target, p.Title, text, p.Priority);
                        delivered++;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        again = p with { Attempts = p.Attempts + 1, NextTryUtc = nowUtc + Backoff(p.Attempts + 1) };
                    }
                }
                lock (_queue)
                {
                    var i = _queue.IndexOf(p);
                    if (i < 0) continue;
                    if (again is null) _queue.RemoveAt(i);
                    else _queue[i] = again;
                }
            }
            lock (_queue) SaveQueue();
            return delivered;
        }
        finally { Volatile.Write(ref _retrying, 0); }
    }

    private void LoadQueue()
    {
        if (_queueLoaded) return;
        _queueLoaded = true;
        if (QueueFile is null || !File.Exists(QueueFile)) return;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<PendingPush>>(File.ReadAllText(QueueFile));
            if (list is not null) _queue.InsertRange(0, list);
        }
        catch { /* beschädigt: neu anfangen – es geht nur um verspätete Meldungen */ }
    }

    private void SaveQueue()
    {
        if (QueueFile is null) return;
        try
        {
            if (_queue.Count == 0) { File.Delete(QueueFile); return; }
            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(_queue));
            File.Move(tmp, QueueFile, overwrite: true);
        }
        catch { /* nicht kritisch: die Warteschlange im Speicher bleibt */ }
    }

    /// <summary>Sperre für einen Schlüssel aufheben, z. B. wenn ein Miner wieder online ist.</summary>
    public void Reset(string key)
    {
        lock (_lastSent) _lastSent.Remove(key);
    }

    /// <summary>Testnachricht an alle aktiven Ziele, ohne Sperrzeit. Liefert Fehlertext(e) oder null.</summary>
    public async Task<string?> TestAsync(NotificationSettings settings)
    {
        var targets = settings.EffectiveTargets().Where(t => t.Enabled).ToList();
        if (targets.Count == 0) return L.T("Kein Dienst ausgewählt");
        var errors = new List<string>();
        foreach (var t in targets)
            if (await TestAsync(t) is { } e) errors.Add(targets.Count > 1 ? $"{t.Title}: {e}" : e);
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    /// <summary>Testnachricht an ein einzelnes Ziel. Liefert Fehlertext oder null.</summary>
    public async Task<string?> TestAsync(PushTarget target)
    {
        try
        {
            await SendRawAsync(target, "Miner Monitor", L.T("Testnachricht – Benachrichtigungen funktionieren."), NotifyPriority.Normal);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task SendRawAsync(PushTarget s, string title, string message, NotifyPriority priority)
    {
        switch (s.Provider)
        {
            case "ntfy":
            {
                if (string.IsNullOrWhiteSpace(s.NtfyTopic)) throw new InvalidOperationException(L.T("ntfy-Topic fehlt"));

                // JSON-Veröffentlichung an die Server-Wurzel: UTF-8 in Titel und Text ohne Header-Kodierung
                var server = (string.IsNullOrWhiteSpace(s.NtfyServer) ? "https://ntfy.sh" : s.NtfyServer.Trim()).TrimEnd('/');
                var payload = new
                {
                    topic = s.NtfyTopic.Trim(),
                    title,
                    message,
                    priority = (int)priority
                };
                using var resp = await _http.PostAsJsonAsync(server, payload);
                resp.EnsureSuccessStatusCode();
                break;
            }

            case "telegram":
            {
                if (string.IsNullOrWhiteSpace(s.TelegramBotToken) || string.IsNullOrWhiteSpace(s.TelegramChatId))
                    throw new InvalidOperationException(L.T("Telegram-Token oder Chat-ID fehlt"));

                var url = $"https://api.telegram.org/bot{s.TelegramBotToken.Trim()}/sendMessage";
                var payload = new
                {
                    chat_id = s.TelegramChatId.Trim(),
                    text = $"{title}\n{message}",
                    disable_notification = priority <= NotifyPriority.Low
                };
                using var resp = await _http.PostAsJsonAsync(url, payload);
                resp.EnsureSuccessStatusCode();
                break;
            }

            case "discord":
            {
                var url = CheckUrl(s.DiscordWebhookUrl, L.T("Discord-Webhook-URL fehlt"), httpsOnly: true);
                if (url.Host is not ("discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
                    || !url.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
                    throw new InvalidOperationException(L.T("Das ist keine Discord-Webhook-URL (https://discord.com/api/webhooks/…)."));
                // Discord erlaubt 2000 Zeichen je Nachricht
                var content = $"**{title}**\n{message}";
                if (content.Length > 2000) content = content[..1999] + "…";
                using var resp = await _http.PostAsJsonAsync(url, new { content, username = "BitaxeTuner" });
                resp.EnsureSuccessStatusCode();
                break;
            }

            case "pushover":
            {
                if (string.IsNullOrWhiteSpace(s.PushoverUserKey) || string.IsNullOrWhiteSpace(s.PushoverAppToken))
                    throw new InvalidOperationException(L.T("Pushover-User-Key oder App-Token fehlt"));
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = s.PushoverAppToken.Trim(),
                    ["user"] = s.PushoverUserKey.Trim(),
                    ["title"] = title,
                    ["message"] = message,
                    // -1 leise, 0 normal, 1 hoch (2 = Notfall mit Quittierung wird bewusst nicht genutzt)
                    ["priority"] = (priority switch { NotifyPriority.Low => -1, NotifyPriority.Normal => 0, _ => 1 }).ToString(),
                });
                using var resp = await _http.PostAsync("https://api.pushover.net/1/messages.json", form);
                resp.EnsureSuccessStatusCode();
                break;
            }

            case "webhook":
            {
                var url = CheckUrl(s.WebhookUrl, L.T("Webhook-URL fehlt"), httpsOnly: false);
                var payload = new
                {
                    source = "BitaxeTuner",
                    title,
                    message,
                    priority = priority.ToString().ToLowerInvariant(),
                    priorityLevel = (int)priority,
                    time = DateTimeOffset.Now,
                };
                using var resp = await _http.PostAsJsonAsync(url, payload);
                resp.EnsureSuccessStatusCode();
                break;
            }

            default:
                throw new InvalidOperationException(L.T("Kein Dienst ausgewählt"));
        }
    }

    /// <summary>Nur vollständige http(s)-Adressen; für Discord ausschließlich https.</summary>
    private static Uri CheckUrl(string text, string missing, bool httpsOnly)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException(missing);
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (!httpsOnly && url.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException(L.T("Ungültige Adresse: {0}", text.Trim()));
        return url;
    }

    public void Dispose() => _http.Dispose();
}
