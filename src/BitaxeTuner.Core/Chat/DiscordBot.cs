using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Chat;

/// <summary>
/// 0.9.24 Discord-Bot (nur lesend) als fertige Vorlage in BitaxeTuner: Der Nutzer legt im Developer Portal nur eine leere
/// Anwendung an und kopiert das Bot-Token. <see cref="SetupAsync"/> trägt die Slash-Befehle (mit Übersetzungen) ein, setzt
/// Name/Bild und liefert den Einladungslink. Danach verbindet sich der Bot ausgehend mit dem Gateway, beantwortet Slash-Befehle
/// nur im gewählten Kanal und pflegt optional eine angepinnte Live-Nachricht. Kein zentraler Bot, kein eigener Server.
/// </summary>
public sealed class DiscordBot : IDisposable
{
    public const string Api = "https://discord.com/api/v10";
    public const string Gateway = "wss://gateway.discord.gg/?v=10&encoding=json";
    /// <summary>Kanal sehen, Nachrichten senden, Links einbetten, Dateien anhängen, Verlauf lesen, Nachrichten verwalten (anpinnen).</summary>
    public const long Permissions = 1024 + 2048 + 8192 + 16384 + 32768 + 65536;

    private static readonly Dictionary<string, string> DiscordLocales = new() { ["de"] = "de", ["es"] = "es-ES", ["pt"] = "pt-BR", ["fr"] = "fr", ["nl"] = "nl" };

    private readonly string _token, _channelId, _stateFile;
    private readonly HttpClient _http;
    private readonly Func<string, Task<ChatReply>> _answer;
    private readonly Func<Task<string>> _live;
    private readonly TimeSpan? _liveEvery;
    private readonly CancellationTokenSource _cts = new();
    private string? _liveMessageId;
    private DateTime _liveAt = DateTime.MinValue;

    public string? Status { get; private set; }

    /// <summary>0.9.23: Minernamen für die Autovervollständigung von /miner und /history.</summary>
    public Func<Task<IReadOnlyList<string>>>? MinerNames { get; init; }
    /// <summary>0.9.23: Text für den Bot-Status unter dem Namen (null = keiner).</summary>
    public Func<Task<string>>? Presence { get; init; }
    /// <summary>0.9.23: Befehle, deren Antwort nur der Fragende sieht.</summary>
    public IReadOnlySet<string> PrivateCommands { get; init; } = new HashSet<string>();
    /// <summary>0.9.26: Antwort mit Wissen über den Fragenden (Steuern). Ohne: nur lesend wie bisher.</summary>
    public Func<string, ChatActor, Task<ChatReply>>? AnswerAs { get; init; }
    /// <summary>0.9.26: Rolle (Name oder Id), deren Mitglieder steuern dürfen.</summary>
    public string AdminRole { get; init; } = "BitaxeTuner-Admin";

    private (DateTime At, string Guild, HashSet<string> Ids)? _adminRoles;

    public DiscordBot(string token, string channelId, HttpMessageHandler? handler, Func<string, Task<ChatReply>> answer, Func<Task<string>> live,
        TimeSpan? liveEvery, string stateFile)
    {
        _token = token;
        _channelId = channelId;
        _answer = answer;
        _live = live;
        _liveEvery = liveEvery;
        _stateFile = stateFile;
        _http = Client(token, handler);
        try { if (File.Exists(stateFile)) _liveMessageId = File.ReadAllText(stateFile).Trim() is { Length: > 0 } id ? id : null; }
        catch { /* neu anfangen */ }
    }

    private static HttpClient Client(string token, HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/Elemirus1996/BitaxeTuner, 1)");
        return http;
    }

    // ---------- Einrichten ----------

    /// <summary>Token prüfen, Slash-Befehle eintragen (Server oder global), Name/Bild setzen, Einladungslink bauen.</summary>
    public static async Task<DiscordSetupResult> SetupAsync(string token, string? guildId, byte[]? avatarPng, HttpMessageHandler? handler, CancellationToken ct = default)
    {
        using var http = Client(token, handler);
        using var app = await http.GetAsync($"{Api}/oauth2/applications/@me", ct);
        if (app.StatusCode == HttpStatusCode.Unauthorized) throw new LocalizedException("Discord lehnt das Bot-Token ab.");
        app.EnsureSuccessStatusCode();
        using var appDoc = JsonDocument.Parse(await app.Content.ReadAsStringAsync(ct));
        var appId = appDoc.RootElement.GetProperty("id").GetString()!;

        var commands = MinerHub.ChatCommandNames.Select(c => CommandJson(c.Name, c.De))
            .Concat(MinerHub.ChatControlNames.Select(c => ControlCommandJson(c.Name, c.De))).ToList();
        var guild = new string((guildId ?? "").Where(char.IsAsciiDigit).ToArray());
        var url = guild.Length > 0 ? $"{Api}/applications/{appId}/guilds/{guild}/commands" : $"{Api}/applications/{appId}/commands";
        using (var put = await http.PutAsJsonAsync(url, commands, ct))
        {
            if (put.StatusCode == HttpStatusCode.Forbidden) throw new LocalizedException("Discord: Der Bot ist noch nicht auf diesem Server – zuerst über den Einladungslink hinzufügen, dann erneut einrichten.");
            put.EnsureSuccessStatusCode();
        }
        var name = "BitaxeTuner";
        if (avatarPng is { Length: > 0 })
        {
            // Name/Bild: Discord begrenzt Namensänderungen stark – Fehler hier sind kein Abbruchgrund
            try
            {
                using var patch = await http.PatchAsJsonAsync($"{Api}/users/@me", new { username = name, avatar = "data:image/png;base64," + Convert.ToBase64String(avatarPng) }, ct);
            }
            catch (HttpRequestException) { }
        }
        var invite = $"https://discord.com/oauth2/authorize?client_id={appId}&scope=bot%20applications.commands&permissions={Permissions}" +
                     (guild.Length > 0 ? $"&guild_id={guild}&disable_guild_select=true" : "");
        return new DiscordSetupResult(appId, name, invite, commands.Count);
    }

    private static object CommandJson(string name, string german)
    {
        // 0.9.23: Minernamen per Autovervollständigung, Zeitraum als Auswahl
        var options = name switch
        {
            "miner" => new object[] { Option("name", L.N("Name des Miners"), required: true, autocomplete: true) },
            "history" => new object[] { Option("miner", L.N("Miner (leer = alle)"), required: false, autocomplete: true),
                Option("period", L.N("Zeitraum"), required: false, choices: ["24h", "7d", "30d"]) },
            _ => [],
        };
        var nameLoc = german != name ? new Dictionary<string, string> { ["de"] = german } : null;
        var descLoc = DiscordLocales.ToDictionary(kv => kv.Value, kv => Clip(MinerHub.ChatCommandDescription(name, Loc.For(kv.Key))));
        return new
        {
            name,
            name_localizations = nameLoc,
            description = Clip(MinerHub.ChatCommandDescription(name, Loc.For("en"))),
            description_localizations = descLoc,
            type = 1,
            options,
        };
    }

    /// <summary>
    /// 0.9.26: Steuer-Befehle – für Mitglieder ohne Rechte ausgeblendet (default_member_permissions „0“: nur Admins, freigeben
    /// unter Servereinstellungen → Integrationen); BitaxeTuner prüft zusätzlich die Rolle „BitaxeTuner-Admin“.
    /// </summary>
    private static object ControlCommandJson(string name, string german)
    {
        var options = name switch
        {
            "restart" => new object[] { Option("miner", L.N("Name des Miners"), required: true, autocomplete: true) },
            "preset" => new object[] { Option("miner", L.N("Name des Miners"), required: true, autocomplete: true), Option("preset", L.N("Name der Voreinstellung"), required: true) },
            "maintenance" => new object[] { Option("miner", L.N("Name des Miners"), required: true, autocomplete: true),
                Option("state", L.N("an oder aus"), required: true, choices: ["an", "aus"]), Option("hours", L.N("Dauer in Stunden (leer = bis zum Ausschalten)"), required: false),
                Option("reason", L.N("Grund"), required: false) },
            _ => [],
        };
        var nameLoc = german != name ? new Dictionary<string, string> { ["de"] = german } : null;
        return new
        {
            name,
            name_localizations = nameLoc,
            description = Clip(MinerHub.ChatControlDescription(name, Loc.For("en"))),
            description_localizations = DiscordLocales.ToDictionary(kv => kv.Value, kv => Clip(MinerHub.ChatControlDescription(name, Loc.For(kv.Key)))),
            type = 1,
            options,
            default_member_permissions = "0",
            dm_permission = false,
        };
    }

    private static Dictionary<string, object> Option(string name, string germanDescription, bool required, bool autocomplete = false, string[]? choices = null)
    {
        var o = new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = Clip(Loc.For("en").T(germanDescription)),
            ["description_localizations"] = DiscordLocales.ToDictionary(kv => kv.Value, kv => Clip(Loc.For(kv.Key).T(germanDescription))),
            ["type"] = 3,
            ["required"] = required,
        };
        if (autocomplete) o["autocomplete"] = true;
        if (choices is not null) o["choices"] = choices.Select(c => new { name = c, value = c }).ToArray();
        return o;
    }

    private static string Clip(string s) => s.Length <= 100 ? s : s[..99] + "…";

    // ---------- Betrieb ----------

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunGatewayAsync(ct);
                wait = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (UnauthorizedAccessException ex) { Status = ex.Message; return; }
            catch (Exception ex) { Status = L.T("Discord: {0}", ex.Message); }
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromSeconds(Math.Min(120, wait.TotalSeconds * 2));
        }
    }

    private async Task RunGatewayAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(Gateway), ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long? seq = null;
        Task? heartbeat = null;
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(object payload)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await sendLock.WaitAsync(linked.Token);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, linked.Token); }
            finally { sendLock.Release(); }
        }
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (json, close) = await ReceiveAsync(ws, linked.Token);
                if (close is { } code)
                {
                    if (code is 4004) throw new UnauthorizedAccessException(L.T("Discord lehnt das Bot-Token ab."));
                    throw new IOException(L.T("Verbindung beendet ({0})", code));
                }
                if (json is null) continue;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) seq = s.GetInt64();
                switch (root.GetProperty("op").GetInt32())
                {
                    case 10:   // HELLO
                        var interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        heartbeat = Task.Run(async () =>
                        {
                            while (!linked.IsCancellationRequested)
                            {
                                await Task.Delay(interval, linked.Token);
                                await Send(new { op = 1, d = seq });
                            }
                        }, linked.Token);
                        await Send(new { op = 2, d = new { token = _token, intents = 0, properties = new { os = Environment.OSVersion.Platform.ToString(), browser = "BitaxeTuner", device = "BitaxeTuner" } } });
                        break;
                    case 1:    // Herzschlag angefordert
                        await Send(new { op = 1, d = seq });
                        break;
                    case 7:    // neu verbinden
                    case 9:    // Sitzung ungültig
                        return;
                    case 0 when root.GetProperty("t").GetString() == "READY":
                        Status = L.T("verbunden");
                        _ = Task.Run(() => LiveLoopAsync(linked.Token), linked.Token);
                        if (Presence is { } presence) _ = Task.Run(() => PresenceLoopAsync(presence, Send, linked.Token), linked.Token);
                        break;
                    case 0 when root.GetProperty("t").GetString() == "INTERACTION_CREATE":
                        var d = root.GetProperty("d").Clone();
                        _ = Task.Run(() => HandleInteractionAsync(d, linked.Token), linked.Token);
                        break;
                }
            }
        }
        finally
        {
            linked.Cancel();
            if (heartbeat is not null) try { await heartbeat; } catch { /* beendet */ }
        }
    }

    private static async Task<(string? Json, int? Close)> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return (null, (int?)ws.CloseStatus ?? 1000);
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return (Encoding.UTF8.GetString(ms.ToArray()), null);
        }
    }

    /// <summary>
    /// Slash-Befehl (2), Knopf (3) oder Autovervollständigung (4): erst „denkt nach …“, dann Karte/Text und ggf. Bild.
    /// Nur im gewählten Kanal; Knöpfe und als privat gewählte Befehle sieht nur der Fragende.
    /// </summary>
    internal async Task HandleInteractionAsync(JsonElement d, CancellationToken ct = default)
    {
        var type = d.GetProperty("type").GetInt32();
        if (type is not (2 or 3 or 4 or 5)) return;
        var id = d.GetProperty("id").GetString();
        var token = d.GetProperty("token").GetString();
        var appId = d.GetProperty("application_id").GetString();
        var channel = d.TryGetProperty("channel_id", out var ch) ? ch.GetString() : null;
        var callback = $"{Api}/interactions/{id}/{token}/callback";
        var data = d.GetProperty("data");
        if (type == 4)
        {
            await AutocompleteAsync(callback, data, channel == _channelId, ct);
            return;
        }
        if (channel != _channelId)
        {
            using var _ = await _http.PostAsJsonAsync(callback, new { type = 4, data = new { content = L.T("BitaxeTuner antwortet nur im eingestellten Kanal."), flags = 64 } }, ct);
            return;
        }
        string command;
        bool ephemeral;
        var customId = data.TryGetProperty("custom_id", out var cid) ? cid.GetString() : null;
        if (type == 3 && customId is { } mm && mm.StartsWith("bt:mm:", StringComparison.Ordinal))
        {
            // 0.9.26 Eingabemaske „Wartungsmodus“ (Stunden, Grund)
            using var modal = await _http.PostAsJsonAsync(callback, MaintenanceModal(mm[6..]), ct);
            return;
        }
        if (type == 5)
        {
            if (customId is not { } mf || !mf.StartsWith("bt:mmf:", StringComparison.Ordinal)) return;
            command = $"/maintenance {mf[7..]} | an | {ModalValue(data, "hours")} | {ModalValue(data, "reason")}";
            ephemeral = true;
        }
        else if (type == 3)
        {
            if (ButtonCommand(customId) is not { } fromButton) return;
            command = fromButton;
            ephemeral = true;
        }
        else
        {
            var name = data.GetProperty("name").GetString() ?? "";
            var control = MinerHub.ChatControlNames.Any(c => c.Name == name);
            var args = data.TryGetProperty("options", out var opts)
                ? string.Join(control ? " | " : " ", opts.EnumerateArray().Select(o => o.GetProperty("value").ToString()))
                : "";
            command = "/" + name + (args.Length > 0 ? " " + args : "");
            ephemeral = PrivateCommands.Contains(name) || control;
        }
        object defer = ephemeral ? new { type = 5, data = new { flags = 64 } } : new { type = 5 };
        using (var deferred = await _http.PostAsJsonAsync(callback, defer, ct)) deferred.EnsureSuccessStatusCode();
        var reply = AnswerAs is { } answerAs ? await answerAs(command, await ActorOfAsync(d, ct)) : await _answer(command);
        var edit = $"{Api}/webhooks/{appId}/{token}/messages/@original";
        using var form = new MultipartFormDataContent();
        var content = reply.Text.Length > 1900 ? reply.Text[..1900] + "…" : reply.Text;
        var message = new Dictionary<string, object> { ["content"] = reply.Embed is null ? content : "" };
        if (reply.Embed is { } embed) message["embeds"] = new[] { EmbedJson(embed, reply.Png is not null) };
        if (reply.Png is not null) message["attachments"] = new[] { new { id = 0, filename = "bitaxetuner.png" } };
        if (reply.Buttons is { Count: > 0 } buttons) message["components"] = new[] { new { type = 1, components = ButtonsJson(buttons) } };
        var payload = JsonSerializer.Serialize(message);
        form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        if (reply.Png is { } png)
        {
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files[0]", "bitaxetuner.png");
        }
        using var resp = await _http.PatchAsync(edit, form, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Farbe nach Zustand: grün, gelb, rot.</summary>
    internal static int ColorOf(string state) => state switch { "bad" => 0xE5484D, "warn" => 0xF5A623, _ => 0x30A46C };

    private static Dictionary<string, object> EmbedJson(ChatEmbed e, bool image)
    {
        var o = new Dictionary<string, object>
        {
            ["title"] = Clip256(e.Title),
            ["color"] = ColorOf(e.State),
            ["fields"] = e.Fields.Take(25).Select(f => new { name = Clip256(f.Name), value = f.Value.Length == 0 ? "–" : f.Value, inline = true }).ToArray(),
            ["footer"] = new { text = "BitaxeTuner" },
        };
        if (image) o["image"] = new { url = "attachment://bitaxetuner.png" };
        return o;
    }

    private static string Clip256(string s) => s.Length <= 256 ? s : s[..255] + "…";

    /// <summary>„bt:m:Gamma“ → „/miner Gamma“, „bt:h:Gamma“ → „/history Gamma“, „bt:s“ → „/status“; 0.9.26 auch Steuer-Knöpfe.</summary>
    internal static string? ButtonCommand(string? customId) =>
        customId is { } c && c.StartsWith("bt:", StringComparison.Ordinal) ? MinerHub.ButtonToCommand(c[3..]) : null;

    /// <summary>0.9.26: Knöpfe (höchstens 5); Bestätigen rot, Abbrechen grau, sonst blau.</summary>
    internal static object[] ButtonsJson(IReadOnlyList<ChatButton> buttons) => buttons.Take(5).Select(b => (object)new
    {
        type = 2,
        style = b.Command.StartsWith("x:", StringComparison.Ordinal) ? 4 : b.Command.StartsWith("n:", StringComparison.Ordinal) ? 2 : 1,
        label = Clip(b.Label)[..Math.Min(80, Clip(b.Label).Length)],
        custom_id = ("bt:" + b.Command) is var id && id.Length > 100 ? id[..100] : "bt:" + b.Command,
    }).ToArray();

    /// <summary>0.9.26: Eingabemaske „Wartungsmodus“ (Antworttyp 9).</summary>
    internal static object MaintenanceModal(string miner) => new
    {
        type = 9,
        data = new
        {
            custom_id = ("bt:mmf:" + miner) is var id && id.Length > 100 ? id[..100] : "bt:mmf:" + miner,
            title = Clip(L.T("Wartungsmodus: {0}", miner))[..Math.Min(45, Clip(L.T("Wartungsmodus: {0}", miner)).Length)],
            components = new object[]
            {
                new { type = 1, components = new[] { new { type = 4, custom_id = "hours", label = L.T("Dauer in Stunden (0 = bis zum Ausschalten)"), style = 1, value = "2", required = true, max_length = 4 } } },
                new { type = 1, components = new[] { new { type = 4, custom_id = "reason", label = L.T("Grund (optional)"), style = 1, value = "", required = false, max_length = 100 } } },
            },
        },
    };

    private static string ModalValue(JsonElement data, string id)
    {
        if (!data.TryGetProperty("components", out var rows)) return "";
        foreach (var row in rows.EnumerateArray())
            if (row.TryGetProperty("components", out var cs))
                foreach (var c in cs.EnumerateArray())
                    if (c.TryGetProperty("custom_id", out var cid) && cid.GetString() == id)
                        return (c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "").Replace("|", "/").Trim();
        return "";
    }

    /// <summary>0.9.26: Wer drückt/fragt – darf steuern, wenn er eine Rolle mit dem Namen bzw. der Id <see cref="AdminRole"/> hat.</summary>
    internal async Task<ChatActor> ActorOfAsync(JsonElement d, CancellationToken ct)
    {
        if (!d.TryGetProperty("member", out var member) || !member.TryGetProperty("user", out var user)) return ChatActor.Anonymous with { Platform = "Discord" };
        var id = user.GetProperty("id").GetString() ?? "";
        var name = "@" + (user.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String ? gn.GetString() : user.TryGetProperty("username", out var un) ? un.GetString() : id);
        var roles = member.TryGetProperty("roles", out var r) ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : [];
        var guild = d.TryGetProperty("guild_id", out var g) ? g.GetString() ?? "" : "";
        var admin = roles.Contains(AdminRole) || (guild.Length > 0 && roles.Overlaps(await AdminRoleIdsAsync(guild, ct)));
        return new ChatActor("Discord", id, name, admin);
    }

    private async Task<HashSet<string>> AdminRoleIdsAsync(string guild, CancellationToken ct)
    {
        if (_adminRoles is { } c && c.Guild == guild && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10)) return c.Ids;
        var ids = new HashSet<string>();
        try
        {
            using var resp = await _http.GetAsync($"{Api}/guilds/{guild}/roles", ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                foreach (var role in doc.RootElement.EnumerateArray())
                    if (string.Equals(role.GetProperty("name").GetString(), AdminRole, StringComparison.OrdinalIgnoreCase))
                        ids.Add(role.GetProperty("id").GetString() ?? "");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { /* ohne Rollenliste darf niemand steuern */ }
        _adminRoles = (DateTime.UtcNow, guild, ids);
        return ids;
    }

    private async Task AutocompleteAsync(string callback, JsonElement data, bool ourChannel, CancellationToken ct)
    {
        var typed = "";
        if (data.TryGetProperty("options", out var opts))
            foreach (var o in opts.EnumerateArray())
                if (o.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True) typed = o.GetProperty("value").ToString();
        // Minernamen nur im eingestellten Kanal verraten
        var names = ourChannel && MinerNames is { } list ? await list() : [];
        var choices = names.Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase)).Take(25)
            .Select(n => new { name = Clip(n), value = Clip(n) }).ToArray();
        using var _ = await _http.PostAsJsonAsync(callback, new { type = 8, data = new { choices } }, ct);
    }

    /// <summary>0.9.23: Bot-Status (Presence) jede Minute, nur bei Änderung senden.</summary>
    private static async Task PresenceLoopAsync(Func<Task<string>> presence, Func<object, Task> send, CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var text = Clip(await presence());
                if (text != last)
                {
                    await send(PresencePayload(text));
                    last = text;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* nächste Runde */ }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    internal static object PresencePayload(string text) =>
        new { op = 3, d = new { since = (long?)null, activities = new[] { new { name = "Custom Status", type = 4, state = text } }, status = "online", afk = false } };

    /// <summary>0.9.23: Warnung in den Kanal – rote Karte mit Knöpfen [Details] [Verlauf] (nur zum Ansehen).</summary>
    public async Task PostAlertAsync(string title, string? miner, CancellationToken ct = default) => await PostAlertAsync(title, miner, null, ct);

    /// <summary>0.9.26: mit zusätzlichen Steuer-Knöpfen (z. B. [Neu starten], [Erledigt]) – Rechte prüft BitaxeTuner beim Drücken.</summary>
    public async Task PostAlertAsync(string title, string? miner, IReadOnlyList<ChatButton>? extra, CancellationToken ct = default)
    {
        var view = miner is { Length: > 0 }
            ? new[] { new ChatButton(L.T("Details"), "m:" + Clip90(miner)), new ChatButton(L.T("Verlauf"), "h:" + Clip90(miner)) }
            : new[] { new ChatButton(L.T("Übersicht"), "s") };
        var buttons = view.Select(b => (object)new { type = 2, style = 2, label = b.Label, custom_id = "bt:" + b.Command })
            .Concat(extra is null ? [] : ButtonsJson(extra)).Take(5).ToArray();
        var body = new
        {
            embeds = new[] { new { title = Clip256("⚠ " + title), color = ColorOf("bad"), footer = new { text = "BitaxeTuner" } } },
            components = new[] { new { type = 1, components = buttons } },
        };
        using var resp = await _http.PostAsJsonAsync($"{Api}/channels/{_channelId}/messages", body, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string Clip90(string s) => s.Length <= 90 ? s : s[..90];

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await UpdateLiveAsync(DateTime.Now, ct); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { Status = L.T("Discord: {0}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>Angepinnte Live-Nachricht im Kanal anlegen bzw. aktualisieren, wenn fällig.</summary>
    internal async Task UpdateLiveAsync(DateTime now, CancellationToken ct = default)
    {
        if (_liveEvery is not { } every || now - _liveAt < every) return;
        _liveAt = now;
        var content = await _live();
        if (_liveMessageId is { } mid)
        {
            using var resp = await _http.PatchAsJsonAsync($"{Api}/channels/{_channelId}/messages/{mid}", new { content }, ct);
            if (resp.IsSuccessStatusCode) return;
            _liveMessageId = null;   // gelöscht → neu anlegen
        }
        using (var resp = await _http.PostAsJsonAsync($"{Api}/channels/{_channelId}/messages", new { content }, ct))
        {
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _liveMessageId = doc.RootElement.GetProperty("id").GetString();
        }
        using (await _http.PutAsync($"{Api}/channels/{_channelId}/pins/{_liveMessageId}", null, ct)) { }
        try { File.WriteAllText(_stateFile, _liveMessageId); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
