using System.Net;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Plugs;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>Smart Plugs (Shelly): Antworten der Gerätegenerationen, Energiebilanz, Verlauf, Hub.</summary>
public class SmartPlugTests
{
    /// <summary>Antwortet je Pfad mit festem JSON; zählt Anfragen.</summary>
    private sealed class FakeShelly(Dictionary<string, string> routes, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Paths.Add(path);
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Plugs werden nur gelesen");
            if (path != "shelly" && status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(routes.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Gen2Shelly = """{"id":"shellyplusplugs-a1","model":"SNPL-00112EU","gen":2,"app":"PlugS","auth_en":false}""";
    private const string Gen2Status = """{"switch:0":{"id":0,"output":true,"apower":21.4,"voltage":231.2,"current":0.12,"aenergy":{"total":1543.25}}}""";

    [Fact]
    public async Task Gen2_plug_is_identified_and_read_via_rpc()
    {
        var fake = new FakeShelly(new() { ["shelly"] = Gen2Shelly, ["rpc/Shelly.GetStatus"] = Gen2Status });
        using var client = new ShellyClient("192.168.1.60", handler: fake);
        var id = await client.IdentifyAsync();
        Assert.Equal(2, id.Generation);
        Assert.Equal("SNPL-00112EU", id.Model);
        Assert.False(id.AuthRequired);

        var r = await client.ReadAsync(0);
        Assert.Equal(21.4, r.PowerW);
        Assert.Equal(1543.25, r.EnergyWh);
        Assert.Equal(231.2, r.Voltage);
        Assert.All(fake.Paths, p => Assert.DoesNotContain("Set", p)); // nie schalten
    }

    [Fact]
    public async Task Gen1_plug_converts_watt_minutes_and_em_uses_wh()
    {
        var fake = new FakeShelly(new()
        {
            ["shelly"] = """{"type":"SHPLG-S","mac":"A1","auth":true,"fw":"20230913"}""",
            ["status"] = """{"relays":[{"ison":true}],"meters":[{"power":18.5,"is_valid":true,"total":6000}]}""",
        });
        using var client = new ShellyClient("shelly-plug.local", "admin", "pw", fake);
        Assert.True((await client.IdentifyAsync()).AuthRequired);
        var r = await client.ReadAsync(0);
        Assert.Equal(18.5, r.PowerW);
        Assert.Equal(100, r.EnergyWh); // 6000 Wattminuten

        var em = ShellyClient.ParseGen1(JsonDocument.Parse("""{"emeters":[{"power":40.5,"voltage":229.9,"total":2500.5}]}""").RootElement, 0);
        Assert.Equal(2500.5, em.EnergyWh);
        Assert.Equal(229.9, em.Voltage);
    }

    [Fact]
    public void Gen2_pm_and_em_devices_and_missing_channel()
    {
        var pm = ShellyClient.ParseGen2(JsonDocument.Parse("""{"pm1:0":{"apower":7.5,"aenergy":{"total":10}}}""").RootElement, 0);
        Assert.Equal(7.5, pm.PowerW);
        var em = ShellyClient.ParseGen2(JsonDocument.Parse("""{"em1:1":{"act_power":55.1,"voltage":230},"em1data:1":{"total_act_energy":99.5}}""").RootElement, 1);
        Assert.Equal(55.1, em.PowerW);
        Assert.Equal(99.5, em.EnergyWh);
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() =>
            ShellyClient.ParseGen2(JsonDocument.Parse(Gen2Status).RootElement, 1));
    }

    [Fact]
    public async Task Wrong_password_gives_clear_message()
    {
        var fake = new FakeShelly(new() { ["shelly"] = Gen2Shelly.Replace("false", "true") }, HttpStatusCode.Unauthorized);
        using var client = new ShellyClient("192.168.1.60", "admin", "falsch", fake);
        var ex = await Assert.ThrowsAsync<BitaxeTuner.Core.I18n.LocalizedException>(() => client.ReadAsync(0));
        Assert.Contains("Passwort", ex.Message);
    }

    [Theory]
    [InlineData("192.168.1.60")]
    [InlineData("shelly-plug.local")]
    [InlineData("10.0.0.5:8080")]
    [InlineData("[fd00::5]")]
    public void Accepts_plain_hosts(string host) => Assert.Equal("http", ShellyClient.BaseUri(host).Scheme);

    [Theory]
    [InlineData("http://192.168.1.60")]
    [InlineData("192.168.1.60/rpc/Switch.Set?on=false")]
    [InlineData("a b")]
    [InlineData("")]
    public void Rejects_urls_and_paths(string host) =>
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => ShellyClient.BaseUri(host));

    // ---------- Energiebilanz ----------

    private static readonly Dictionary<string, double> Miners = new() { ["a"] = 15, ["b"] = 25, ["c"] = 10 };

    private static SmartPlugSettings Plugs(params SmartPlugConfig[] items) => new() { Items = [.. items] };

    [Fact]
    public void Without_plugs_or_measurements_axeos_values_are_used()
    {
        var e = EnergyBalance.Compute(Plugs(), Miners, _ => 99);
        Assert.False(e.FromPlugs);
        Assert.Equal(50, e.TotalPowerW);
        Assert.Null(e.OverheadW);

        var offline = EnergyBalance.Compute(Plugs(new SmartPlugConfig { Role = "miners", Miners = ["a"] }), Miners, _ => null);
        Assert.False(offline.FromPlugs);
    }

    [Fact]
    public void Plug_for_two_miners_is_split_by_axeos_share_and_overhead_is_shown()
    {
        var plug = new SmartPlugConfig { Role = "miners", Miners = ["a", "b"] };
        var fans = new SmartPlugConfig { Role = "other" };
        var e = EnergyBalance.Compute(Plugs(plug, fans), Miners, p => p == plug ? 48 : 4);
        Assert.True(e.FromPlugs);
        Assert.Equal(48 + 10 + 4, e.TotalPowerW); // a+b gemessen, c aus AxeOS, Lüfter extra
        Assert.Equal(12, e.OverheadW);
        Assert.Equal(18, e.WallPowerOf("a"));     // 48 × 15/40
        Assert.Equal(30, e.WallPowerOf("b"));
        Assert.Null(e.WallPowerOf("c"));
        Assert.Contains("A", e.Covered);          // Hosts ohne Groß-/Kleinschreibung
    }

    [Fact]
    public void Total_plug_replaces_the_sum_and_setting_can_switch_it_off()
    {
        var total = new SmartPlugConfig { Role = "total" };
        var e = EnergyBalance.Compute(Plugs(total), Miners, _ => 70);
        Assert.Equal(70, e.TotalPowerW);
        Assert.Equal(20, e.OverheadW);

        var off = Plugs(total);
        off.UseForCosts = false;
        Assert.Equal(50, EnergyBalance.Compute(off, Miners, _ => 70).TotalPowerW);
    }

    [Fact]
    public void History_average_needs_half_of_the_period()
    {
        using var dir = new TempDir();
        using var history = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        var now = new DateTime(2026, 9, 29, 12, 0, 0);
        for (var m = 0; m < 60; m++) history.AddPlugSample("p1", now.AddMinutes(-m), 40, m);
        Assert.Equal(40, history.AveragePlugPower("p1", now.AddHours(-1), now)!.Value.PowerW);
        Assert.InRange(history.QueryPlug("p1", now.AddHours(-1), now, maxPoints: 1).Count, 1, 2); // Fenster an festen Zeitgrenzen

        var plugs = Plugs(new SmartPlugConfig { Id = "p1", Role = "total" });
        var minerAvg = new Dictionary<string, double> { ["a"] = 30 };
        Assert.True(EnergyBalance.FromHistory(history, plugs, minerAvg, now.AddHours(-1), now).FromPlugs);
        Assert.False(EnergyBalance.FromHistory(history, plugs, minerAvg, now.AddHours(-24), now).FromPlugs); // nur 1 von 24 h
        Assert.True(history.CountRows().ContainsKey("plug_samples"));
    }

    [Fact]
    public void Older_config_without_plugs_loads()
    {
        var c = JsonSerializer.Deserialize<AppConfig>("""{"Devices":[]}""")!;
        Assert.Empty(c.Plugs.Items);
        Assert.True(c.Plugs.UseForCosts);
    }

    // ---------- Frühwarnung ----------

    [Theory]
    [InlineData(115, 100, 108, 100, true)]   // 15 % statt 8 %, +7 W → Meldung
    [InlineData(111, 100, 108, 100, false)]  // nur +3 Prozentpunkte
    [InlineData(24, 20, 20.5, 20, true)]     // kleiner Miner: 20 % statt 2,5 %, +3,5 W
    [InlineData(22, 20, 20.4, 20, false)]    // +8 Prozentpunkte, aber nur +1,6 W
    [InlineData(4, 3, 3, 3, false)]          // zu wenig Last für eine Aussage
    public void Overhead_warning_needs_percent_and_watt_increase(double pr, double ar, double pb, double ab, bool expected) =>
        Assert.Equal(expected, PlugHealth.Check(pr, ar, pb, ab) is not null);

    private sealed class FailingPlug : IPlugClient
    {
        public bool Fail { get; set; } = true;
        public Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default) => Task.FromResult(new PlugIdentity(2, "Test", false));
        public Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default) =>
            Fail ? throw new HttpRequestException("keine Verbindung") : Task.FromResult(new PlugReading(30, 1, null, null));
        public void Dispose() { }
    }

    [Fact]
    public async Task Offline_plug_is_reported_after_five_minutes_and_recovery_once()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 9, 29, 12, 0, 0);
        var config = new AppConfig();
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "test";
        config.Plugs.Items.Add(new SmartPlugConfig { Id = "p1", Name = "Regal", Host = "192.168.1.60", Role = "other" });
        var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, Clock = () => now });
        var plug = new FailingPlug();
        hub.PlugClientFactory = _ => plug;
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        try
        {
            await hub.PlugTickAsync();
            now = now.AddMinutes(4);
            await hub.PlugTickAsync();
            Assert.Empty(sent);                                  // WLAN-Aussetzer: noch keine Meldung

            now = now.AddMinutes(2);
            await hub.PlugTickAsync();
            await hub.PlugTickAsync();
            Assert.Equal(["plug-offline:p1"], sent);             // genau einmal

            plug.Fail = false;
            now = now.AddMinutes(1);
            await hub.PlugTickAsync();
            await hub.PlugTickAsync();
            Assert.Equal(["plug-offline:p1", "plug-online:p1"], sent);

            hub.Config.Notifications.OnPlugs = false;
            plug.Fail = true;
            now = now.AddMinutes(10);
            await hub.PlugTickAsync();
            now = now.AddMinutes(10);
            await hub.PlugTickAsync();
            Assert.Equal(2, sent.Count);                         // abgeschaltet
        }
        finally
        {
            hub.Dispose();
        }
    }

    // ---------- Hub ----------

    [Fact]
    public async Task Hub_reads_simulated_plug_and_reports_overhead_and_errors()
    {
        using var dir = new TempDir();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.5.1" });
        config.Plugs.Items.Add(new SmartPlugConfig { Id = "sim1", Name = "Steckdose Gamma", Host = "sim", Role = "miners", Miners = ["10.0.5.1"] });
        config.Plugs.Items.Add(new SmartPlugConfig { Id = "bad1", Name = "Kaputt", Host = "192.168.1.61" });
        var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
        });
        hub.PlugClientFactory = p => p.Id == "bad1"
            ? new ShellyClient(p.Host, handler: new FakeShelly(new() { ["shelly"] = Gen2Shelly }, HttpStatusCode.Unauthorized))
            : null;
        try
        {
            await hub.PollNowAsync();
            await hub.PlugTickAsync();

            var status = hub.PlugStatuses();
            var sim = status.Single(s => s.Id == "sim1");
            Assert.True(sim.Online);
            Assert.True(sim.PowerW > sim.MinerPowerW);
            Assert.True(sim.OverheadW > 0);
            var bad = status.Single(s => s.Id == "bad1");
            Assert.False(bad.Online);
            Assert.Contains("Passwort", bad.Error);

            var energy = hub.CurrentEnergy();
            Assert.True(energy.FromPlugs);
            Assert.Equal(sim.PowerW!.Value, energy.TotalPowerW, 3);
            Assert.NotNull(hub.History!.AveragePlugPower("sim1", DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(1)));
        }
        finally
        {
            hub.Dispose();
        }
    }
}
