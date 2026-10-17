using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Chat;

/// <summary>Antwort eines Chat-Befehls: Text und optional ein Bild (PNG).</summary>
public sealed record ChatReply(string Text, byte[]? Png = null)
{
    /// <summary>0.9.23: farbige Karte mit Feldern (Discord-Embed); Telegram zeigt den Text.</summary>
    public ChatEmbed? Embed { get; init; }
}

/// <summary>0.9.23: Karte für Discord – Titel, Zustand („ok“, „warn“, „bad“ → grün/gelb/rot) und Felder.</summary>
public sealed record ChatEmbed(string Title, string State, IReadOnlyList<ChatField> Fields);

public sealed record ChatField(string Name, string Value);

/// <summary>0.9.23: Knopf unter einer Warnung – nur zum Ansehen („/miner Gamma“, „/history Gamma“, „/status“).</summary>
public sealed record ChatButton(string Label, string Command);

/// <summary>Ergebnis des Discord-Einrichtens.</summary>
public sealed record DiscordSetupResult(string AppId, string BotName, string InviteUrl, int Commands);

/// <summary>
/// 0.9.24 Telegram-Bot (nur lesend): holt Nachrichten per Long-Polling (getUpdates, nur ausgehend, keine Portfreigabe) und
/// antwortet ausschließlich im eingetragenen Chat. Optional eine angepinnte Live-Statusnachricht (editMessageText).
/// Nachrichten aus anderen Chats werden ignoriert.
/// </summary>
public sealed class TelegramBot : IDisposable
{
    private readonly string _token, _chatId, _stateFile;
    private readonly HttpClient _http;
    private readonly Func<string, Task<ChatReply>> _answer;
    private readonly Func<Task<string>> _live;
    private readonly TimeSpan? _liveEvery;
    private readonly CancellationTokenSource _cts = new();
    private long _offset;
    private long? _liveMessageId;
    private DateTime _liveAt = DateTime.MinValue;
    private bool _commandsSet;

    public string? Status { get; private set; }

    public TelegramBot(string token, string chatId, HttpMessageHandler? handler, Func<string, Task<ChatReply>> answer, Func<Task<string>> live,
        TimeSpan? liveEvery, string stateFile)
    {
        _token = token;
        _chatId = chatId;
        _answer = answer;
        _live = live;
        _liveEvery = liveEvery;
        _stateFile = stateFile;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(40);
        try
        {
            if (File.Exists(stateFile) && JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(stateFile)) is { } st)
            {
                _offset = st.GetValueOrDefault("offset");
                _liveMessageId = st.TryGetValue("live", out var m) && m > 0 ? m : null;
            }
        }
        catch { /* neu anfangen */ }
    }

    private string Url(string method) => $"https://api.telegram.org/bot{_token}/{method}";

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(25, ct);
                await UpdateLiveAsync(DateTime.Now, ct);
                wait = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (UnauthorizedAccessException ex) { Status = ex.Message; return; }
            catch (Exception ex)
            {
                Status = L.T("Telegram: {0}", ex.Message);
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromSeconds(Math.Min(120, wait.TotalSeconds * 2));
            }
        }
    }

    /// <summary>Einmal neue Nachrichten holen und beantworten (Tests: Wartezeit 0).</summary>
    internal async Task PollOnceAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        if (!_commandsSet) await SetCommandsAsync(ct);
        // 0.9.23: auch Knopfdrücke (callback_query) unter Warnungen
        using var resp = await _http.GetAsync(Url("getUpdates") + $"?offset={_offset}&timeout={timeoutSeconds}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(L.T("Telegram lehnt das Bot-Token ab."));
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException(L.T("Der Bot wird schon woanders abgefragt (Webhook oder zweites Programm) – dort beenden."));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Status = L.T("verbunden");
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            _offset = Math.Max(_offset, u.GetProperty("update_id").GetInt64() + 1);
            if (u.TryGetProperty("callback_query", out var cb))
            {
                await HandleButtonAsync(cb, ct);
                continue;
            }
            if (!u.TryGetProperty("message", out var msg) || !msg.TryGetProperty("text", out var t)) continue;
            var chat = msg.GetProperty("chat").GetProperty("id").GetRawText();
            var text = t.GetString() ?? "";
            if (chat != _chatId || !text.StartsWith('/')) continue;   // fremde Chats: keine Antwort
            var reply = await _answer(text);
            await SendAsync(reply, ct);
        }
        SaveState();
    }

    /// <summary>„m:Gamma“ → „/miner Gamma“, „h:Gamma“ → „/history Gamma“, „s“ → „/status“.</summary>
    internal static string? ButtonCommand(string? data) => data switch
    {
        "s" => "/status",
        { } d when d.StartsWith("m:", StringComparison.Ordinal) => "/miner " + d[2..],
        { } d when d.StartsWith("h:", StringComparison.Ordinal) => "/history " + d[2..],
        _ => null,
    };

    private async Task HandleButtonAsync(JsonElement cb, CancellationToken ct)
    {
        var id = cb.GetProperty("id").GetString();
        using (await _http.PostAsJsonAsync(Url("answerCallbackQuery"), new { callback_query_id = id }, ct)) { }
        var chat = cb.TryGetProperty("message", out var m) ? m.GetProperty("chat").GetProperty("id").GetRawText() : null;
        if (chat != _chatId) return;   // fremde Chats: keine Antwort
        if (ButtonCommand(cb.TryGetProperty("data", out var d) ? d.GetString() : null) is not { } command) return;
        await SendAsync(await _answer(command), ct);
    }

    /// <summary>0.9.23: Warnung in den Chat – mit Knöpfen [Details] [Verlauf] (nur zum Ansehen).</summary>
    public async Task PostAlertAsync(string title, string? miner, CancellationToken ct = default)
    {
        // callback_data höchstens 64 Byte
        string Data(string prefix) => prefix + Clip(miner!, 60);
        var keyboard = miner is { Length: > 0 }
            ? new[] { new[] { new { text = L.T("Details"), callback_data = Data("m:") }, new { text = L.T("Verlauf"), callback_data = Data("h:") } } }
            : new[] { new[] { new { text = L.T("Übersicht"), callback_data = "s" } } };
        using var resp = await _http.PostAsJsonAsync(Url("sendMessage"), new { chat_id = _chatId, text = "⚠ " + title, reply_markup = new { inline_keyboard = keyboard } }, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string Clip(string s, int maxBytes)
    {
        while (System.Text.Encoding.UTF8.GetByteCount(s) > maxBytes) s = s[..^1];
        return s;
    }

    private async Task SetCommandsAsync(CancellationToken ct)
    {
        var commands = MinerHub.ChatCommandNames.Select(c => new { command = c.Name, description = MinerHub.ChatCommandDescription(c.Name) }).ToList();
        using var resp = await _http.PostAsJsonAsync(Url("setMyCommands"), new { commands }, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(L.T("Telegram lehnt das Bot-Token ab."));
        _commandsSet = true;
    }

    private async Task SendAsync(ChatReply reply, CancellationToken ct)
    {
        if (reply.Png is { } png)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(_chatId), "chat_id" },
                { new StringContent(reply.Text.Length > 1000 ? reply.Text[..1000] + "…" : reply.Text), "caption" },
            };
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "photo", "bitaxetuner.png");
            using var resp = await _http.PostAsync(Url("sendPhoto"), form, ct);
            resp.EnsureSuccessStatusCode();
        }
        else
        {
            using var resp = await _http.PostAsJsonAsync(Url("sendMessage"), new { chat_id = _chatId, text = reply.Text }, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Angepinnte Live-Nachricht anlegen bzw. aktualisieren, wenn fällig.</summary>
    internal async Task UpdateLiveAsync(DateTime now, CancellationToken ct = default)
    {
        if (_liveEvery is not { } every || now - _liveAt < every) return;
        _liveAt = now;
        var text = await _live();
        if (_liveMessageId is { } id)
        {
            using var resp = await _http.PostAsJsonAsync(Url("editMessageText"), new { chat_id = _chatId, message_id = id, text }, ct);
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Contains("not modified", StringComparison.OrdinalIgnoreCase)) return;
            _liveMessageId = null;   // gelöscht → neu anlegen
        }
        using (var resp = await _http.PostAsJsonAsync(Url("sendMessage"), new { chat_id = _chatId, text, disable_notification = true }, ct))
        {
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _liveMessageId = doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
        }
        using (await _http.PostAsJsonAsync(Url("pinChatMessage"), new { chat_id = _chatId, message_id = _liveMessageId, disable_notification = true }, ct)) { }
        SaveState();
    }

    private void SaveState()
    {
        try { File.WriteAllText(_stateFile, JsonSerializer.Serialize(new Dictionary<string, long> { ["offset"] = _offset, ["live"] = _liveMessageId ?? 0 })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
