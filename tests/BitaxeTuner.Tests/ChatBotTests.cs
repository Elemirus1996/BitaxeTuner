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
}
