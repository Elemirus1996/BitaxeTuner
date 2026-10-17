using System.Net;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Chat;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24 Chat-Bots: Befehle, Telegram (Long-Polling), Discord (Einrichten, Slash-Befehle).</summary>
public class ChatBotTests
{
    private sealed class Fake(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            var (code, text) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static MinerHub Hub(TempDir dir)
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.13.1" });
        return new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
    }

    [Fact]
    public void Commands_answer_with_text_and_a_picture_and_accept_german_names()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        var now = DateTime.Now;
        var help = hub.AnswerChat("/help", now);
        Assert.Contains("/status", help.Text);
        Assert.Contains("/verlauf", help.Text);
        Assert.Null(help.Png);

        var status = hub.AnswerChat("/status@MeinBot", now);
        Assert.Contains("Miner online", status.Text);
        Assert.NotNull(status.Png);
        Assert.Equal(0x89, status.Png![0]);                                // PNG
        Assert.DoesNotContain("10.0.13.1", status.Text);                   // keine IP-Adressen

        Assert.NotNull(hub.AnswerChat("/verlauf 7d", now).Png);           // deutscher Name
        Assert.Contains("Gamma", hub.AnswerChat("/miner gam", now).Text);
        Assert.Contains("Keinen Miner", hub.AnswerChat("/miner xyz", now).Text);
        Assert.Contains("Unbekannter Befehl", hub.AnswerChat("/frequenz 600", now).Text);   // nichts zum Steuern
        Assert.Equal("history", MinerHub.NormalizeChatCommand("/VERLAUF"));
    }

    [Fact]
    public async Task Telegram_answers_only_in_its_chat_and_keeps_a_pinned_live_message()
    {
        using var dir = new TempDir();
        var updates = """
            {"ok":true,"result":[
            {"update_id":10,"message":{"chat":{"id":999},"text":"/status"}},
            {"update_id":11,"message":{"chat":{"id":4711},"text":"/status"}},
            {"update_id":12,"message":{"chat":{"id":4711},"text":"Hallo"}}]}
            """;
        var fake = new Fake((r, _) => r.RequestUri!.AbsolutePath.EndsWith("getUpdates") ? (HttpStatusCode.OK, updates)
            : r.RequestUri.AbsolutePath.EndsWith("sendMessage") ? (HttpStatusCode.OK, """{"ok":true,"result":{"message_id":77}}""")
            : (HttpStatusCode.OK, """{"ok":true,"result":true}"""));
        var asked = new List<string>();
        using var bot = new TelegramBot("123:abc", "4711", fake, text => { asked.Add(text); return Task.FromResult(new ChatReply("Antwort", [0x89, 1, 2])); },
            () => Task.FromResult("Live"), TimeSpan.FromMinutes(5), Path.Combine(dir.Path, "tg.json"));

        await bot.PollOnceAsync(0);
        Assert.Equal(["/status"], asked);                                   // fremder Chat und Text ohne „/“ ignoriert
        Assert.Contains(fake.Requests, r => r.Url.Contains("setMyCommands") && r.Body.Contains("\"history\""));
        var photo = Assert.Single(fake.Requests, r => r.Url.EndsWith("sendPhoto"));
        Assert.Contains("4711", photo.Body);
        Assert.Contains("offset=0", fake.Requests.First(r => r.Url.Contains("getUpdates")).Url);
        await bot.PollOnceAsync(0);
        Assert.Contains("offset=13", fake.Requests.Last(r => r.Url.Contains("getUpdates")).Url);   // nächste Runde ab 13

        var now = DateTime.Now;
        await bot.UpdateLiveAsync(now);
        Assert.Contains(fake.Requests, r => r.Url.EndsWith("pinChatMessage") && r.Body.Contains("77"));
        await bot.UpdateLiveAsync(now.AddMinutes(1));                       // noch nicht fällig
        await bot.UpdateLiveAsync(now.AddMinutes(6));
        Assert.Single(fake.Requests, r => r.Url.EndsWith("editMessageText"));
    }

    [Fact]
    public async Task Discord_setup_registers_translated_commands_and_builds_the_invite()
    {
        var fake = new Fake((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/oauth2/applications/@me")
            ? (HttpStatusCode.OK, """{"id":"112233","name":"App"}""") : (HttpStatusCode.OK, "[]"));
        var result = await DiscordBot.SetupAsync("tok", "445566", [0x89, 1], fake);
        Assert.Equal("112233", result.AppId);
        Assert.Contains("client_id=112233", result.InviteUrl);
        Assert.Contains("guild_id=445566", result.InviteUrl);
        var put = Assert.Single(fake.Requests, r => r.Method == "PUT");
        Assert.EndsWith("/applications/112233/guilds/445566/commands", put.Url);
        using var doc = JsonDocument.Parse(put.Body);
        var history = doc.RootElement.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "history");
        Assert.Equal("verlauf", history.GetProperty("name_localizations").GetProperty("de").GetString());
        Assert.True(history.GetProperty("description_localizations").TryGetProperty("es-ES", out _));
        Assert.All(doc.RootElement.EnumerateArray(), c => Assert.True(c.GetProperty("description").GetString()!.Length <= 100));
        Assert.Contains(fake.Requests, r => r.Method == "PATCH" && r.Url.EndsWith("/users/@me"));   // Name und Bild

        var refused = new Fake((_, _) => (HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<BitaxeTuner.Core.I18n.LocalizedException>(() => DiscordBot.SetupAsync("falsch", null, null, refused));
    }

    [Fact]
    public async Task Discord_answers_slash_commands_only_in_its_channel()
    {
        using var dir = new TempDir();
        var fake = new Fake((_, _) => (HttpStatusCode.OK, """{"id":"900"}"""));
        var asked = new List<string>();
        using var bot = new DiscordBot("tok", "555", fake, text => { asked.Add(text); return Task.FromResult(new ChatReply("Antwort", [0x89])); },
            () => Task.FromResult("Live"), null, Path.Combine(dir.Path, "dc.txt"));
        JsonElement I(string channel) => JsonDocument.Parse($$$"""
            {"type":2,"id":"1","token":"itok","application_id":"112233","channel_id":"{{{channel}}}",
             "data":{"name":"history","options":[{"name":"miner","value":"Gamma"},{"name":"period","value":"7d"}]}}
            """).RootElement;

        await bot.HandleInteractionAsync(I("666"));
        var refuse = Assert.Single(fake.Requests);
        Assert.Contains("\"flags\":64", refuse.Body);                       // nur für den Fragenden sichtbar
        Assert.Empty(asked);

        await bot.HandleInteractionAsync(I("555"));
        Assert.Equal(["/history Gamma 7d"], asked);
        Assert.Contains(fake.Requests, r => r.Url.EndsWith("/interactions/1/itok/callback") && r.Body.Contains("\"type\":5"));
        var edit = Assert.Single(fake.Requests, r => r.Method == "PATCH");
        Assert.EndsWith("/webhooks/112233/itok/messages/@original", edit.Url);
        Assert.Contains("payload_json", edit.Body);
        Assert.Contains("bitaxetuner.png", edit.Body);
    }

    // ---------- 0.9.23 Ausbau ----------

    [Fact]
    public async Task Status_and_miner_come_as_cards_and_history_knows_30_days()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        var now = DateTime.Now;
        var status = hub.AnswerChat("/status", now).Embed;
        Assert.NotNull(status);
        Assert.Equal("ok", status!.State);
        Assert.Contains(status.Fields, f => f.Name == "Hashrate");
        Assert.Contains(status.Fields, f => f.Value == "1/1");
        var miner = hub.AnswerChat("/miner gam", now).Embed;
        Assert.NotNull(miner);
        Assert.StartsWith("Gamma", miner!.Title);
        Assert.DoesNotContain(miner.Fields, f => f.Value.Contains("10.0.13.1"));   // keine IP-Adressen
        Assert.Contains("30", hub.AnswerChat("/history 30d", now).Text);
        Assert.Equal(["Gamma"], hub.ChatMinerNames());
        Assert.Contains("1/1", hub.ChatPresenceText(now));
        using var presence = JsonDocument.Parse(JsonSerializer.Serialize(DiscordBot.PresencePayload("1/1 online")));
        Assert.Equal(3, presence.RootElement.GetProperty("op").GetInt32());
        Assert.Equal(4, presence.RootElement.GetProperty("d").GetProperty("activities")[0].GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task Discord_commands_offer_autocomplete_and_period_choices()
    {
        var fake = new Fake((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/oauth2/applications/@me")
            ? (HttpStatusCode.OK, """{"id":"112233"}""") : (HttpStatusCode.OK, "[]"));
        await DiscordBot.SetupAsync("tok", "445566", null, fake);
        using var doc = JsonDocument.Parse(Assert.Single(fake.Requests, r => r.Method == "PUT").Body);
        var miner = doc.RootElement.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "miner");
        Assert.True(miner.GetProperty("options")[0].GetProperty("autocomplete").GetBoolean());
        var history = doc.RootElement.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "history");
        var period = history.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "period");
        Assert.Equal(["24h", "7d", "30d"], period.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("value").GetString()));
        Assert.False(period.TryGetProperty("autocomplete", out _));
    }

    [Fact]
    public async Task Discord_autocompletes_names_answers_privately_and_with_cards_and_buttons()
    {
        using var dir = new TempDir();
        var fake = new Fake((_, _) => (HttpStatusCode.OK, """{"id":"900"}"""));
        var asked = new List<string>();
        using var bot = new DiscordBot("tok", "555", fake, text =>
            {
                asked.Add(text);
                return Task.FromResult(new ChatReply("Antwort", [0x89]) { Embed = new ChatEmbed("Gamma – online", "warn", [new ChatField("Hashrate", "1,1 TH/s")]) });
            },
            () => Task.FromResult("Live"), null, Path.Combine(dir.Path, "dc.txt"))
        {
            MinerNames = () => Task.FromResult<IReadOnlyList<string>>(["Gamma", "Supra Keller"]),
            PrivateCommands = new HashSet<string> { "pool" },
        };
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

        // Autovervollständigung: nur im eigenen Kanal Namen verraten
        await bot.HandleInteractionAsync(J("""{"type":4,"id":"1","token":"t1","application_id":"9","channel_id":"555","data":{"name":"miner","options":[{"name":"name","value":"kel","focused":true}]}}"""));
        var auto = fake.Requests.Last();
        Assert.Contains("\"type\":8", auto.Body);
        Assert.Contains("Supra Keller", auto.Body);
        Assert.DoesNotContain("Gamma", auto.Body);
        await bot.HandleInteractionAsync(J("""{"type":4,"id":"2","token":"t2","application_id":"9","channel_id":"666","data":{"name":"miner","options":[{"name":"name","value":"","focused":true}]}}"""));
        Assert.DoesNotContain("Gamma", fake.Requests.Last().Body);
        Assert.Empty(asked);

        // privater Befehl: nur für den Fragenden; Antwort als farbige Karte mit Bild
        await bot.HandleInteractionAsync(J("""{"type":2,"id":"3","token":"t3","application_id":"9","channel_id":"555","data":{"name":"pool"}}"""));
        Assert.Contains(fake.Requests, r => r.Url.EndsWith("/interactions/3/t3/callback") && r.Body.Contains("\"flags\":64"));
        var edit = fake.Requests.Last(r => r.Method == "PATCH");
        Assert.Contains("\"embeds\"", edit.Body);
        Assert.Contains(DiscordBot.ColorOf("warn").ToString(), edit.Body);
        Assert.Contains("attachment://bitaxetuner.png", edit.Body);
        // öffentlicher Befehl
        await bot.HandleInteractionAsync(J("""{"type":2,"id":"4","token":"t4","application_id":"9","channel_id":"555","data":{"name":"status"}}"""));
        Assert.DoesNotContain("flags", fake.Requests.Single(r => r.Url.EndsWith("/interactions/4/t4/callback")).Body);

        // Knopf unter einer Warnung → Verlauf, nur für den, der drückt
        await bot.HandleInteractionAsync(J("""{"type":3,"id":"5","token":"t5","application_id":"9","channel_id":"555","data":{"custom_id":"bt:h:Gamma"}}"""));
        Assert.Equal(["/pool", "/status", "/history Gamma"], asked);
        Assert.Contains("\"flags\":64", fake.Requests.Single(r => r.Url.EndsWith("/interactions/5/t5/callback")).Body);
        await bot.HandleInteractionAsync(J("""{"type":3,"id":"6","token":"t6","application_id":"9","channel_id":"555","data":{"custom_id":"fremd"}}"""));
        Assert.Equal(3, asked.Count);

        await bot.PostAlertAsync("Gamma offline", "Gamma");
        var alert = fake.Requests.Last();
        Assert.EndsWith("/channels/555/messages", alert.Url);
        Assert.Contains("bt:m:Gamma", alert.Body);
        Assert.Contains("bt:h:Gamma", alert.Body);
    }

    [Fact]
    public async Task Telegram_buttons_under_alerts_answer_only_in_its_chat()
    {
        using var dir = new TempDir();
        var updates = """
            {"ok":true,"result":[
            {"update_id":20,"callback_query":{"id":"cb1","data":"m:Gamma","message":{"chat":{"id":4711}}}},
            {"update_id":21,"callback_query":{"id":"cb2","data":"m:Gamma","message":{"chat":{"id":999}}}}]}
            """;
        var fake = new Fake((r, _) => r.RequestUri!.AbsolutePath.EndsWith("getUpdates") ? (HttpStatusCode.OK, updates)
            : (HttpStatusCode.OK, """{"ok":true,"result":{"message_id":5}}"""));
        var asked = new List<string>();
        using var bot = new TelegramBot("123:abc", "4711", fake, text => { asked.Add(text); return Task.FromResult(new ChatReply("Antwort")); },
            () => Task.FromResult("Live"), null, Path.Combine(dir.Path, "tg.json"));
        await bot.PollOnceAsync(0);
        Assert.Equal(["/miner Gamma"], asked);
        Assert.Equal(2, fake.Requests.Count(r => r.Url.EndsWith("answerCallbackQuery")));   // Knopf immer quittieren
        Assert.Contains("callback_query", fake.Requests.First(r => r.Url.Contains("getUpdates")).Url);

        await bot.PostAlertAsync("Gamma offline", "Gamma");
        var alert = fake.Requests.Last();
        Assert.Contains("inline_keyboard", alert.Body);
        Assert.Contains("h:Gamma", alert.Body);
        Assert.Equal("/status", TelegramBot.ButtonCommand("s"));
    }

    [Fact]
    public async Task Alerts_go_to_the_chat_only_when_switched_on_and_not_too_often()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        hub.OnChatAlert("offline:10.0.13.1", "Gamma offline", "10.0.13.1");
        Assert.Empty(hub.ChatAlertsPosted);                                // standardmäßig aus
        hub.Config.ChatBot.AlertButtons = true;
        hub.OnChatAlert("offline:10.0.13.1", "Gamma offline", "10.0.13.1");
        hub.OnChatAlert("offline:10.0.13.1", "Gamma offline", "10.0.13.1"); // gleiche Warnung: nicht gleich nochmal
        hub.OnChatAlert("pool-payout:x", "Auszahlung", null);               // keine Warnung mit Knöpfen
        hub.OnChatAlert("fanstall:2", "Lüfter K2 steht", null);
        Assert.Equal([("Gamma offline", (string?)"Gamma"), ("Lüfter K2 steht", null)], hub.ChatAlertsPosted);
        var settings = new ChatBotSettings { PrivateCommands = ["pool", "frequenz", "pool"] };
        settings.Normalize();
        Assert.Equal(["pool"], settings.PrivateCommands);
    }
}
