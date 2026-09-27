using BitaxeTuner.Core.Config;
using System.Net.Http;
using System.Net.Http.Json;

namespace BitaxeTuner.Core.Monitoring;

public enum NotifyPriority
{
    Low = 2,
    Normal = 3,
    High = 4,
    Urgent = 5
}

/// <summary>
/// Push-Benachrichtigungen über ntfy oder Telegram.
///
/// Jede Meldung hat einen Schlüssel (z. B. "offline:192.168.1.50"). Innerhalb
/// der Sperrzeit wird derselbe Schlüssel nicht erneut gesendet, damit ein
/// wackelnder Miner nicht das Handy flutet. Fehler beim Senden werden
/// verschluckt und nur über <see cref="LastError"/> gemeldet.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<NotificationSettings> _settings;
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public string? LastError { get; private set; }

    /// <summary>Jede Meldung, die die Sperrzeit passiert hat (für Meldungsverlauf im Browser und Tests).</summary>
    public event Action<string, string, string, NotifyPriority>? Sending;

    /// <summary>Nur für Tests: statt ntfy/Telegram aufrufen.</summary>
    internal Func<string, string, NotifyPriority, Task>? TransportOverride { get; set; }

    public NotificationService(Func<NotificationSettings> settings)
    {
        _settings = settings;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
    }

    public bool Enabled => _settings().Provider is "ntfy" or "telegram";

    /// <summary>Sendet, sofern aktiviert und nicht innerhalb der Sperrzeit schon gesendet.</summary>
    public async Task SendAsync(string key, string title, string message,
                                NotifyPriority priority = NotifyPriority.Normal, TimeSpan? cooldown = null)
    {
        if (!Enabled) return;

        var wait = cooldown ?? TimeSpan.FromMinutes(30);
        lock (_lastSent)
        {
            if (_lastSent.TryGetValue(key, out var last) && DateTime.UtcNow - last < wait) return;
            _lastSent[key] = DateTime.UtcNow;
        }

        Sending?.Invoke(key, title, message, priority);
        try
        {
            if (TransportOverride is { } transport) await transport(title, message, priority);
            else await SendRawAsync(_settings(), title, message, priority);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // Sperre aufheben, damit der nächste Versuch nicht blockiert ist
            lock (_lastSent) _lastSent.Remove(key);
        }
    }

    /// <summary>Sperre für einen Schlüssel aufheben, z. B. wenn ein Miner wieder online ist.</summary>
    public void Reset(string key)
    {
        lock (_lastSent) _lastSent.Remove(key);
    }

    /// <summary>Testnachricht mit übergebenen Einstellungen, ohne Sperrzeit. Liefert Fehlertext oder null.</summary>
    public async Task<string?> TestAsync(NotificationSettings settings)
    {
        try
        {
            await SendRawAsync(settings, "Miner Monitor", "Testnachricht – Benachrichtigungen funktionieren.", NotifyPriority.Normal);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task SendRawAsync(NotificationSettings s, string title, string message, NotifyPriority priority)
    {
        switch (s.Provider)
        {
            case "ntfy":
            {
                if (string.IsNullOrWhiteSpace(s.NtfyTopic)) throw new InvalidOperationException("ntfy-Topic fehlt");

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
                    throw new InvalidOperationException("Telegram-Token oder Chat-ID fehlt");

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

            default:
                throw new InvalidOperationException("Kein Dienst ausgewählt");
        }
    }

    public void Dispose() => _http.Dispose();
}
