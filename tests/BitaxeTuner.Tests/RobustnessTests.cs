using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>Funde E1, E2 und I1 aus dem Audit vom 02.10.2026: Verlauf, Speichern der Einstellungen, Push-Wiederholung.</summary>
public class RobustnessTests
{
    [Fact]
    public void Plug_samples_are_buffered_per_minute_but_never_missing_from_queries()
    {
        using var dir = new TempDir();
        var file = dir.File("history.db");
        var t = new DateTime(2026, 10, 1, 12, 0, 5);
        using (var h = new HistoryStore(file))
        {
            h.AddPlugSample("p", t, 10, null);
            h.AddPlugSample("p", t.AddSeconds(20), 12, null);                 // gleiche Minute: letzter Wert zählt
            Assert.Equal((12.0, 1), h.AveragePlugPower("p", t.AddMinutes(-1), t.AddMinutes(1)));
            h.AddPlugSample("p", t.AddMinutes(1), 20, 5);                    // nächste Minute, noch im Puffer
        }
        using var again = new HistoryStore(file);                             // beim Schließen geschrieben
        Assert.Equal((16.0, 2), again.AveragePlugPower("p", t.AddMinutes(-1), t.AddMinutes(2)));
    }

    [Fact]
    public void Batch_commits_together_and_rolls_back_on_error()
    {
        using var dir = new TempDir();
        using var h = new HistoryStore(dir.File("history.db"));
        var t = new DateTime(2026, 10, 1, 12, 0, 0);
        h.Batch(() => { h.AddSample("a", t, 1000, 50, 15, true); h.AddSample("b", t, 500, 55, 12, true); });
        Assert.NotNull(h.Average("a", t.AddMinutes(-1), t.AddMinutes(1)));
        Assert.NotNull(h.Average("b", t.AddMinutes(-1), t.AddMinutes(1)));

        Assert.Throws<InvalidOperationException>(() => h.Batch(() =>
        {
            h.AddSample("c", t, 1000, 50, 15, true);
            throw new InvalidOperationException("Test");
        }));
        Assert.Null(h.Average("c", t.AddMinutes(-1), t.AddMinutes(1)));
        h.AddSample("d", t, 1000, 50, 15, true);                              // danach normal weiter
        Assert.NotNull(h.Average("d", t.AddMinutes(-1), t.AddMinutes(1)));
    }

    [Fact]
    public void Failed_config_save_is_reported_instead_of_swallowed()
    {
        using var dir = new TempDir();
        var c = new AppConfig();
        string? reported = null;
        c.SaveFailed += e => reported = e;
        var blocked = dir.File("config.json");
        Directory.CreateDirectory(blocked);                                   // Ordner statt Datei: Schreiben scheitert
        c.Save(blocked);
        Assert.NotNull(reported);
        Assert.Equal(reported, c.LastSaveError);

        c.Save(dir.File("ok.json"));
        Assert.Null(c.LastSaveError);
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        public bool Down { get; set; } = true;
        public System.Net.HttpStatusCode DownStatus { get; set; } = System.Net.HttpStatusCode.ServiceUnavailable;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) return new HttpResponseMessage(DownStatus);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private static NotificationSettings Settings() => new()
    {
        Targets = [new PushTarget { Id = "w", Name = "w", Provider = "webhook", WebhookUrl = "http://push.local/x", Categories = [nameof(NotifyCategory.Offline)] }],
    };

    [Fact]
    public async Task Failed_push_is_retried_later_and_survives_a_restart()
    {
        using var dir = new TempDir();
        var queue = dir.File("push-queue.json");
        var settings = Settings();
        var net = new FlakyHandler();
        var now = DateTime.UtcNow;

        // nicht freigeben: der Handler wird für den „Neustart“ weiterverwendet
        var first = new NotificationService(() => settings, net) { QueueFile = queue };
        {
            await first.SendAsync("offline:a", "Miner offline", "Miner A antwortet nicht", category: NotifyCategory.Offline, host: "a");
            Assert.Single(first.Pending);
            Assert.Equal(0, await first.RetryPendingAsync(now));                 // noch nicht fällig
            Assert.Equal(0, await first.RetryPendingAsync(now.AddMinutes(2)));   // Dienst noch weg → später erneut
            Assert.Equal(2, first.Pending.Single().Attempts);
        }
        Assert.True(File.Exists(queue));

        net.Down = false;
        using var second = new NotificationService(() => settings, net) { QueueFile = queue };   // nach Neustart
        Assert.Equal(1, await second.RetryPendingAsync(now.AddMinutes(10)));
        Assert.Contains("Miner A antwortet nicht", net.Bodies.Single());
        Assert.Empty(second.Pending);
        Assert.False(File.Exists(queue));
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried_for_hours()
    {
        // Audit I1: 4xx (z. B. gelöschtes Topic/Webhook) ändert sich durch Wiederholen nicht
        var settings = Settings();
        var net = new FlakyHandler { DownStatus = System.Net.HttpStatusCode.NotFound };
        using var service = new NotificationService(() => settings, net);
        Assert.Equal(NotificationService.SendOutcome.Failed, await service.SendAsync("k1", "T", "M", category: NotifyCategory.Offline, host: "a"));
        Assert.Empty(service.Pending);
        net.DownStatus = System.Net.HttpStatusCode.TooManyRequests;                 // 429: vorübergehend
        Assert.Equal(NotificationService.SendOutcome.Queued, await service.SendAsync("k2", "T", "M", category: NotifyCategory.Offline, host: "a"));
        Assert.Single(service.Pending);
        Assert.Equal(NotificationService.SendOutcome.Suppressed, await service.SendAsync("k2", "T", "M", category: NotifyCategory.Offline, host: "a"));
    }

    [Fact]
    public async Task Old_or_orphaned_pushes_are_dropped()
    {
        var settings = Settings();
        var net = new FlakyHandler();
        using var service = new NotificationService(() => settings, net);
        var now = DateTime.UtcNow;
        await service.SendAsync("k1", "T", "M", category: NotifyCategory.Offline, host: "a");
        Assert.Single(service.Pending);
        net.Down = false;
        Assert.Equal(0, await service.RetryPendingAsync(now + NotificationService.RetryMaxAge + TimeSpan.FromMinutes(1)));
        Assert.Empty(service.Pending);                                             // zu alt

        net.Down = true;
        await service.SendAsync("k2", "T", "M", category: NotifyCategory.Offline, host: "a");
        settings.Targets.Clear();                                                  // Ziel gelöscht
        net.Down = false;
        Assert.Equal(0, await service.RetryPendingAsync(now.AddMinutes(5)));
        Assert.Empty(service.Pending);
        Assert.Empty(net.Bodies);
    }

    [Fact]
    public void Retry_interval_grows_up_to_thirty_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), NotificationService.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(8), NotificationService.Backoff(4));
        Assert.Equal(TimeSpan.FromMinutes(30), NotificationService.Backoff(9));
    }
}
