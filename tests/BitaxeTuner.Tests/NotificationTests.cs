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
            return new HttpResponseMessage(HttpStatusCode.NoContent);
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
}
