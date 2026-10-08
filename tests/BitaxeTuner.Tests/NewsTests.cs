using System.Net;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11: E-Paper-Seite „Neuigkeiten“ aus der news.json der GitHub Action.</summary>
public sealed class NewsTests : IDisposable
{
    private const string Sample = """
        {"version":1,"updated":"2026-10-08T12:00:00Z","items":[
          {"id":"solo-btc-966351","date":"2026-09-10T08:00:00Z","kind":"solo","coin":"BTC",
           "title":{"de":"Solo-Block BTC #966351 – Braiins Solo","en":"Solo block BTC #966351 – Braiins Solo"},
           "text":{"de":"Belohnung 3,147 BTC","en":"Reward 3.147 BTC"},"url":"https://mempool.space/block/abc"},
          {"id":"fw-x","date":"2026-09-20T08:00:00Z","kind":"firmware","title":{"de":"Firmware ESP-Miner v2.15.3"},"text":{},"url":"javascript:alert(1)"},
          {"id":"kaputt","kind":"miner","title":{"de":"ohne Datum"}},
          {"id":"bt","date":"2026-10-05T08:00:00Z","kind":"bitaxetuner","title":{"en":"BitaxeTuner v0.9.10 released"},"url":"http://example.com"}
        ]}
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Feed(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void Feed_is_parsed_per_language_and_skips_broken_entries_and_unsafe_links()
    {
        var (updated, items) = NewsFeed.Parse(Sample, "en");
        Assert.Equal(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), updated);
        Assert.Equal(3, items.Count);                                                // ohne Datum übersprungen
        Assert.Equal("Solo block BTC #966351 – Braiins Solo", items[0].Title);
        Assert.Equal("Firmware ESP-Miner v2.15.3", items[1].Title);                  // nur Deutsch vorhanden → Rückfall
        Assert.Equal("", items[1].Url);                                              // javascript: verworfen
        Assert.Equal("", items[2].Url);                                              // nur https
        Assert.Equal("Belohnung 3,147 BTC", NewsFeed.Parse(Sample, "de").Items[0].Text);
        Assert.Empty(NewsFeed.Parse("kein json", "de").Items);
    }

    [Fact]
    public async Task Feed_is_fetched_at_most_every_three_hours_and_cached_for_offline_starts()
    {
        var handler = new Feed(Sample);
        var now = DateTime.UtcNow;
        using (var feed = new NewsFeed(_dir.Path, handler))
        {
            await feed.RefreshAsync(now);
            await feed.RefreshAsync(now.AddHours(1));                                // zu früh
            Assert.Equal(1, handler.Calls);
            Assert.Equal(["bt", "solo-btc-966351"], feed.Items(["solo", "bitaxetuner"], 5, "de").Select(i => i.Id));
            await feed.RefreshAsync(now.AddHours(4));
            Assert.Equal(2, handler.Calls);
        }

        // Neustart ohne Internet: Zwischenspeicher bleibt, Fehler wird gemerkt
        using var offline = new NewsFeed(_dir.Path, new Feed("", HttpStatusCode.ServiceUnavailable));
        await offline.RefreshAsync(now, force: true);
        Assert.NotNull(offline.LastError);
        Assert.Equal(3, offline.Items(null, 10, "de").Count);
    }

    [Fact]
    public async Task News_page_renders_and_follows_the_chosen_kinds()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "news-cache.json"), Sample);
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.9.1" });
        config.Display.Enabled = true;
        config.Display.Pages.News = true;
        config.Display.NewsKinds = ["solo"];
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            FanDeviceFactory = _ => new SimulatedFanDevice(),
        });
        await hub.PollNowAsync();
        var m = hub.PreviewScene(DisplayScene.News, DateTime.Now);
        Assert.Equal(DisplayScene.News, m.Scene);
        Assert.Equal(["solo-btc-966351"], m.News!.Select(i => i.Id));
        Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(m).Length);
        Assert.Equal(PicoFanDevice.ImageBytes, StatusRenderer.Render(m with { News = [] }).Length);   // leer: Hinweis statt Liste
    }
}
