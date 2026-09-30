using System.Net;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>Versand an Discord, Pushover und eigenen Webhook – ohne Netz, die Anfragen werden mitgeschnitten.</summary>
public class NotificationTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            // „kaputt.example“ simuliert einen ausgefallenen Dienst
            return new HttpResponseMessage(request.RequestUri!.Host == "kaputt.example" ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent);
        }
    }

    private static (NotificationService Service, Recorder Recorder) Create(NotificationSettings settings)
    {
        var recorder = new Recorder();
        return (new NotificationService(() => settings, recorder), recorder);
    }

    [Fact]
    public async Task Discord_posts_title_and_message_to_the_webhook()
    {
        var (service, rec) = Create(new NotificationSettings { Provider = "discord", DiscordWebhookUrl = "https://discord.com/api/webhooks/123/abc" });
        Assert.True(service.Enabled);
        await service.SendAsync("k", "Gamma", "offline", NotifyPriority.High);

        var (req, body) = Assert.Single(rec.Sent);
        Assert.Equal("https://discord.com/api/webhooks/123/abc", req.RequestUri!.ToString());
        Assert.Equal("**Gamma**\noffline", JsonDocument.Parse(body).RootElement.GetProperty("content").GetString());
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task Discord_rejects_other_hosts_and_long_texts_are_shortened()
    {
        var (service, rec) = Create(new NotificationSettings { Provider = "discord", DiscordWebhookUrl = "https://example.com/api/webhooks/1/x" });
        Assert.Contains("Discord", await service.TestAsync(new NotificationSettings { Provider = "discord", DiscordWebhookUrl = "https://example.com/api/webhooks/1/x" }));
        Assert.NotNull(await service.TestAsync(new NotificationSettings { Provider = "discord", DiscordWebhookUrl = "http://discord.com/api/webhooks/1/x" }));
        Assert.Empty(rec.Sent);

        var ok = new NotificationSettings { Provider = "discord", DiscordWebhookUrl = "https://discord.com/api/webhooks/1/x" };
        Assert.Null(await service.TestAsync(ok));
        (service, rec) = Create(ok);
        await service.SendAsync("k", "T", new string('x', 3000));
        var content = JsonDocument.Parse(rec.Sent[0].Body).RootElement.GetProperty("content").GetString()!;
        Assert.Equal(2000, content.Length);
    }

    [Fact]
    public async Task Pushover_sends_form_with_keys_and_mapped_priority()
    {
        var (service, rec) = Create(new NotificationSettings { Provider = "pushover", PushoverUserKey = "u123", PushoverAppToken = "a456" });
        await service.SendAsync("k", "Gamma", "zu heiß", NotifyPriority.Urgent);
        await service.SendAsync("k2", "Gamma", "Rekord", NotifyPriority.Low);

        Assert.Equal("https://api.pushover.net/1/messages.json", rec.Sent[0].Request.RequestUri!.ToString());
        var form = System.Web.HttpUtility.ParseQueryString(rec.Sent[0].Body);
        Assert.Equal("a456", form["token"]);
        Assert.Equal("u123", form["user"]);
        Assert.Equal("zu heiß", form["message"]);
        Assert.Equal("1", form["priority"]);   // nie 2 (Notfall mit Quittierungspflicht)
        Assert.Equal("-1", System.Web.HttpUtility.ParseQueryString(rec.Sent[1].Body)["priority"]);

        Assert.NotNull(await service.TestAsync(new NotificationSettings { Provider = "pushover", PushoverUserKey = "u" }));
    }

    [Fact]
    public async Task Webhook_posts_json_and_allows_local_http()
    {
        var (service, rec) = Create(new NotificationSettings { Provider = "webhook", WebhookUrl = "http://homeassistant.local:8123/api/webhook/bitaxe" });
        await service.SendAsync("k", "Gamma", "offline", NotifyPriority.High);

        var (req, body) = Assert.Single(rec.Sent);
        Assert.Equal(HttpMethod.Post, req.Method);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("BitaxeTuner", json.GetProperty("source").GetString());
        Assert.Equal("Gamma", json.GetProperty("title").GetString());
        Assert.Equal("offline", json.GetProperty("message").GetString());
        Assert.Equal("high", json.GetProperty("priority").GetString());
        Assert.Equal(4, json.GetProperty("priorityLevel").GetInt32());

        Assert.NotNull(await service.TestAsync(new NotificationSettings { Provider = "webhook", WebhookUrl = "ftp://x/y" }));
        Assert.NotNull(await service.TestAsync(new NotificationSettings { Provider = "webhook", WebhookUrl = "" }));
    }

    [Fact]
    public void Older_config_without_new_fields_still_loads()
    {
        var s = JsonSerializer.Deserialize<NotificationSettings>("""{"Provider":"telegram","TelegramBotToken":"t","TelegramChatId":"1"}""")!;
        Assert.Equal("", s.DiscordWebhookUrl);
        Assert.Equal("", s.WebhookUrl);
        Assert.True(new NotificationService(() => s).Enabled);
        Assert.False(new NotificationService(() => new NotificationSettings()).Enabled);
    }

    // ---------- 0.8.0: mehrere Ziele ----------

    private static PushTarget Hook(string id, string url, params NotifyCategory[] cats) => new()
    {
        Id = id, Name = id, Provider = "webhook", WebhookUrl = url, Categories = cats.Select(c => c.ToString()).ToList(),
    };

    private static List<string> Hosts(Recorder rec) => rec.Sent.Select(x => x.Request.RequestUri!.Host).ToList();

    [Fact]
    public async Task Each_target_gets_only_its_categories_and_miners()
    {
        var privat = Hook("privat", "http://privat.local/hook", Enum.GetValues<NotifyCategory>());
        var community = Hook("community", "http://community.local/hook", NotifyCategory.Finds, NotifyCategory.DailyReport);
        community.Miners = ["10.0.0.2"];
        var settings = new NotificationSettings { Targets = [privat, community] };
        var (service, rec) = Create(settings);

        await service.SendAsync("offline:a", "A offline", "…", category: NotifyCategory.Offline, host: "10.0.0.1");
        Assert.Equal(["privat.local"], Hosts(rec));

        rec.Sent.Clear();
        await service.SendAsync("block:a", "Block A", "…", category: NotifyCategory.Finds, host: "10.0.0.1");
        Assert.Equal(["privat.local"], Hosts(rec));                   // Community nur für Miner 10.0.0.2

        rec.Sent.Clear();
        await service.SendAsync("block:b", "Block B", "…", category: NotifyCategory.Finds, host: "10.0.0.2");
        Assert.Equal(["privat.local", "community.local"], Hosts(rec));

        rec.Sent.Clear();
        await service.SendAsync("report", "Tagesbericht", "…", category: NotifyCategory.DailyReport);
        Assert.Equal(["privat.local", "community.local"], Hosts(rec)); // ohne Miner-Bezug an alle, die den Bereich wollen

        rec.Sent.Clear();
        privat.Enabled = false;
        await service.SendAsync("offline:c", "C offline", "…", category: NotifyCategory.Offline, host: "10.0.0.1");
        Assert.Empty(rec.Sent);
        Assert.False(settings.Wants(NotifyCategory.Offline));
        Assert.True(settings.Wants(NotifyCategory.Finds));
    }

    [Fact]
    public async Task A_failing_target_does_not_block_the_others()
    {
        var settings = new NotificationSettings { Targets = [Hook("kaputt", "http://kaputt.example/x", NotifyCategory.Offline), Hook("gut", "http://gut.local/x", NotifyCategory.Offline)] };
        var (service, rec) = Create(settings);
        await service.SendAsync("offline:a", "A", "…", category: NotifyCategory.Offline, host: "h");
        Assert.Equal(["kaputt.example", "gut.local"], Hosts(rec));
        Assert.Contains("kaputt", service.LastError);

        rec.Sent.Clear();
        await service.SendAsync("offline:a", "A", "…", category: NotifyCategory.Offline, host: "h");
        Assert.Empty(rec.Sent);                                        // einer kam an → Sperrzeit gilt

        Assert.Contains("kaputt", await service.TestAsync(settings));
        Assert.Null(await service.TestAsync(settings.Targets[1]));
    }

    [Fact]
    public void Single_setting_becomes_one_target_and_first_target_is_mirrored_back()
    {
        var old = JsonSerializer.Deserialize<NotificationSettings>("""{"Provider":"ntfy","NtfyTopic":"t","OnOffline":false,"OnRecord":true}""")!;
        var t = Assert.Single(old.EffectiveTargets());
        Assert.Equal("ntfy", t.Provider);
        Assert.False(t.Wants(NotifyCategory.Offline));
        Assert.True(t.Wants(NotifyCategory.Record));
        Assert.True(t.Wants(NotifyCategory.DailyReport));
        Assert.False(old.Wants(NotifyCategory.Offline));

        var s = new NotificationSettings
        {
            Targets = [new PushTarget { Provider = "discord", DiscordWebhookUrl = "https://discord.com/api/webhooks/1/x", Categories = ["Finds"] }],
        };
        s.SyncLegacyFromTargets();
        Assert.Equal("discord", s.Provider);
        Assert.Equal("https://discord.com/api/webhooks/1/x", s.DiscordWebhookUrl);
        Assert.True(s.OnFinds);
        Assert.False(s.OnOffline);

        var clone = s.Clone();
        clone.Targets[0].Categories.Add("Offline");
        Assert.DoesNotContain("Offline", s.Targets[0].Categories);    // tiefe Kopie
    }
}
