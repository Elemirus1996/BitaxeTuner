using System.Net;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24: Mehrere Server in einer Desktop-App.</summary>
public class MultiServerTests
{
    private static AppConfig TwoServers()
    {
        var c = new AppConfig();
        c.Server = new ServerConnectionSettings { Enabled = true, Url = "https://10.0.24.10:8484/", Token = "btk_erster-test", BackupPickup = true };
        MultiServer.Add(c, new ServerConnectionSettings { Name = "Werkstatt", Url = "https://10.0.24.20:8484/", Token = "btk_zweiter-test" });
        return c;
    }

    [Fact]
    public void Existing_connection_stays_first_and_switching_swaps_entries()
    {
        var c = TwoServers();
        Assert.Equal(["10.0.24.10", "Werkstatt"], MultiServer.All(c).Select(MultiServer.NameOf));
        Assert.False(c.ExtraServers[0].Enabled);

        MultiServer.Activate(c, 1);
        Assert.Equal("btk_zweiter-test", c.Server.Token);
        Assert.True(c.Server.Enabled);
        Assert.Equal("btk_erster-test", c.ExtraServers[0].Token);
        Assert.True(c.ExtraServers[0].BackupPickup);                 // Einstellungen je Server reisen mit
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => MultiServer.Add(c, new ServerConnectionSettings { Url = "https://10.0.24.20:8484" }));

        MultiServer.Add(c, new ServerConnectionSettings { Url = "https://10.0.24.10:8484", Token = "btk_neu-test" });   // gleiche Adresse: ersetzt
        Assert.Equal("btk_neu-test", Assert.Single(c.ExtraServers).Token);
        MultiServer.Remove(c, 1);
        Assert.Empty(c.ExtraServers);
    }

    [Fact]
    public void Tokens_of_extra_servers_go_to_secrets_json()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        TwoServers().Save(file);
        Assert.DoesNotContain("btk_zweiter-test", File.ReadAllText(file));
        var back = AppConfig.Load(file);
        Assert.Equal("btk_zweiter-test", back.ExtraServers[0].Token);
        Assert.Equal("Werkstatt", back.ExtraServers[0].Name);
        Assert.Empty(back.UnresolvedSecrets);
    }

    [Fact]
    public void Old_config_without_extra_servers_loads_unchanged()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(file, """{ "Server": { "Enabled": true, "Url": "https://10.0.24.10:8484/" } }""");
        var c = AppConfig.Load(file);
        Assert.Empty(c.ExtraServers);
        Assert.Single(MultiServer.All(c));
    }

    private const string Status = """
        { "running": true, "money": { "code": "EUR" },
          "totals": { "hashrate": 2400, "power": 31.5, "online": 2, "count": 3, "costPerDay": 0.25 } }
        """;

    [Fact]
    public void Totals_add_up_over_reachable_servers()
    {
        var c = TwoServers();
        using var doc = JsonDocument.Parse(Status);
        var a = MultiServer.Tile(0, c.Server, doc.RootElement);
        var b = a with { Index = 1, HashGh = 1600, PowerW = 20, Online = 1, Count = 1, CostPerDay = 0.15 };
        var down = MultiServer.Unreachable(2, c.ExtraServers[0], "Zeitüberschreitung");
        var sum = MultiServer.Sum([a, b, down]);
        Assert.Equal((3, 2, 3, 4), (sum.Servers, sum.Reachable, sum.Online, sum.Count));
        Assert.Equal(4000, sum.HashGh);
        Assert.Equal(51.5, sum.PowerW);
        Assert.Equal(0.40, sum.CostPerDay!.Value, 6);
        Assert.Equal("EUR", sum.Currency);
        Assert.Null(MultiServer.Sum([a, b with { Currency = "CHF" }]).CostPerDay);   // verschiedene Währungen: keine Summe
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    [Fact]
    public async Task One_unreachable_server_does_not_hide_the_others()
    {
        var c = TwoServers();
        var tiles = await MultiServer.FetchAllAsync(c, s => s.Token == "btk_erster-test"
            ? new ServerClient(new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Status) })) { BaseAddress = new Uri(s.Url) }, s.Token)
            : new ServerClient(new HttpClient(new Fake(_ => throw new HttpRequestException("keine Verbindung"))) { BaseAddress = new Uri(s.Url) }, s.Token));
        Assert.True(tiles[0].Reachable);
        Assert.Equal(2400, tiles[0].HashGh);
        Assert.False(tiles[1].Reachable);
        Assert.Equal("Werkstatt", tiles[1].Name);
    }
}
