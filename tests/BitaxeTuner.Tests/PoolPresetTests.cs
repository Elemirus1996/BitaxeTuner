using System.Text.Json.Nodes;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.14 Pool-Steuerung: Vorlagen speichern und auf Miner übertragen (Vorschau, Pool-Liste bzw. alte Felder).</summary>
public sealed class PoolPresetTests : IDisposable
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, SimulatedMinerClient> _sims = new();
    private readonly MinerHub _hub;

    public PoolPresetTests()
    {
        var config = new AppConfig();
        foreach (var h in new[] { "10.0.0.51", "10.0.0.52" })
            config.Devices.Add(new DeviceConfig { Name = "Miner" + h[^2..], Host = h });
        _hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => _sims[h] = new SimulatedMinerClient(Gamma, seed: h.Length, address: h),
        });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private void SavePresets() => _hub.SavePoolPresets(
    [
        new PoolPreset { Id = "solo", Name = "Solo", Url = "stratum+tcp://solo.example.org:3333", User = "bc1qwallet", Worker = "name" },
        new PoolPreset { Id = "pps", Name = "PPS", Url = "pps.example.org", Port = 4000, User = "konto.alt", Worker = "keep", Password = "d=2000" },
    ], "Test");

    [Fact]
    public void Presets_are_normalized_and_invalid_ones_rejected()
    {
        SavePresets();
        var solo = _hub.PoolPresetById("solo")!;
        Assert.Equal("solo.example.org", solo.Url);
        Assert.Equal(3333, solo.Port);
        Assert.Equal("bc1qwallet.Miner51", solo.UserFor("Miner51", "x.y"));
        Assert.Equal("konto.worker7", _hub.PoolPresetById("pps")!.UserFor("egal", "alt.worker7"));
        Assert.Throws<LocalizedException>(() => _hub.SavePoolPresets([new PoolPreset { Name = "leer", Url = "a.example" }], "Test"));
        Assert.Equal(2, _hub.Config.PoolPresets.Count);   // ungültige Liste ersetzt nichts
    }

    [Fact]
    public async Task Indexed_firmware_gets_the_pool_list_and_remembers_the_home_pool()
    {
        Assert.True(await _hub.PollNowAsync());
        SavePresets();
        var d = _hub.Device("10.0.0.51")!;
        var a = new PoolAssignment(d.Host, "solo", "pps");

        var preview = await _hub.PoolAssignPreviewAsync(d, a);
        Assert.Null(preview.Skip);
        Assert.Contains("pool.demo.invalid:3333 → solo.example.org:3333", preview.Text);
        Assert.Contains("backup.demo.invalid:4444 → pps.example.org:4000", preview.Text);
        Assert.Null(_sims[d.Host].LastPatch);                        // Vorschau schreibt nichts

        var restarts = _sims[d.Host].RestartCount;
        var done = await _hub.ApplyPoolAssignmentsAsync([a], "Test");
        Assert.Null(done.Single().Skip);
        var patch = _sims[d.Host].LastPatch!;
        Assert.False(patch.ContainsKey("stratumURL"));               // Gamma: nur die Pool-Liste
        var pools = Assert.IsType<JsonArray>(patch["pools"]);
        Assert.Equal("d=2000", (string)pools.OfType<JsonObject>().Single(p => (string)p["stratumURL"]! == "pps.example.org")["stratumPassword"]!);
        Assert.Equal(("solo.example.org", 3333, "bc1qwallet.Miner51"), _sims[d.Host].PoolEntries[0]);
        Assert.Equal(("pps.example.org", 4000, "konto.worker"), _sims[d.Host].PoolEntries[1]);
        Assert.Equal(restarts + 1, _sims[d.Host].RestartCount);
        Assert.Equal("solo.example.org:3333", d.Config.HomePool);
        Assert.Contains(d.LogLines, l => l.Contains("Pools übertragen"));

        // Zweites Mal: nichts mehr zu tun
        Assert.NotNull((await _hub.PoolAssignPreviewAsync(d, a)).Skip);
    }

    [Fact]
    public async Task Deviation_from_the_preset_is_reported_after_two_checks()
    {
        // 0.9.16: Vorlage wird gemerkt; weicht der Miner ab, kommt nach zwei Prüfungen Protokoll und Push
        _hub.Config.Notifications.Provider = "ntfy";
        _hub.Config.Notifications.NtfyTopic = "t";
        _hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        _hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        Assert.True(await _hub.PollNowAsync());
        SavePresets();
        var d = _hub.Device("10.0.0.51")!;
        await _hub.ApplyPoolAssignmentsAsync([new PoolAssignment(d.Host, "solo", "pps")], "Test");
        Assert.Equal(("solo", "pps"), (d.Config.PoolPresetPrimary, d.Config.PoolPresetFallback));
        Assert.True(await _hub.PollNowAsync());
        var now = DateTime.Now;
        await _hub.CheckPoolDeviationsForTestAsync(now);
        Assert.True(_hub.PoolDeviationOf(d) is null, string.Join(" / ", _hub.PoolDeviationOf(d)?.Differences ?? []));   // passt
        Assert.Null(_hub.PoolDeviationOf(_hub.Device("10.0.0.52")!));    // keine Vorlage gemerkt

        // Vorlage zeigt jetzt auf einen anderen Pool (wie eine Änderung direkt in AxeOS)
        var presets = _hub.Config.PoolPresets.Select(p => p.Clone()).ToList();
        presets.Single(p => p.Id == "solo").Url = "neu.example.org";
        _hub.SavePoolPresets(presets, "Test");
        var dev = _hub.PoolDeviationOf(d)!;
        Assert.Contains(dev.Differences, x => x.Contains("solo.example.org:3333") && x.Contains("neu.example.org:3333"));

        sent.Clear();
        await _hub.CheckPoolDeviationsForTestAsync(now);
        Assert.Empty(sent);                                              // einmal kann ein Umschalten sein
        await _hub.CheckPoolDeviationsForTestAsync(now.AddMinutes(10));
        Assert.Equal([$"pool-deviation:{d.Host}"], sent);
        Assert.Contains(d.LogLines, l => l.Contains("weichen von der Vorlage ab"));

        _hub.ForgetPoolPresets(d, "Test");                               // „so lassen“
        Assert.Null(_hub.PoolDeviationOf(d));
    }

    [Fact]
    public async Task Legacy_firmware_gets_the_old_fields_and_same_preset_twice_is_refused()
    {
        Assert.True(await _hub.PollNowAsync());
        SavePresets();
        var d = _hub.Device("10.0.0.52")!;
        _sims[d.Host].LegacyPools = true;
        Assert.NotNull((await _hub.PoolAssignPreviewAsync(d, new PoolAssignment(d.Host, "solo", "solo"))).Skip);

        var done = await _hub.ApplyPoolAssignmentsAsync([new PoolAssignment(d.Host, "solo", null)], "Test");
        Assert.Null(done.Single().Skip);
        var patch = _sims[d.Host].LastPatch!;
        Assert.Equal("solo.example.org", patch["stratumURL"]);
        Assert.False(patch.ContainsKey("fallbackStratumURL"));      // Ersatz-Pool bleibt
        Assert.Equal("solo.example.org", _sims[d.Host].PoolEntries[0].Url);
    }

    [Fact]
    public async Task Decode_coinbase_alone_is_previewed_and_written()
    {
        Assert.True(await _hub.PollNowAsync());
        SavePresets();
        var d = _hub.Device("10.0.0.51")!;
        var a = new PoolAssignment(d.Host, "solo", null);
        await _hub.ApplyPoolAssignmentsAsync([a], "Test");                 // Pool ist jetzt gesetzt

        var presets = _hub.Config.PoolPresets.Select(p => p.Clone()).ToList();
        presets.Single(p => p.Id == "solo").DecodeCoinbase = false;
        _hub.SavePoolPresets(presets, "Test");
        var preview = await _hub.PoolAssignPreviewAsync(d, a);
        Assert.Null(preview.Skip);
        var change = Assert.Single(preview.Changes);                         // nur Coinbase, Adresse bleibt gleich
        Assert.Contains("Coinbase", change.Label);
        await _hub.ApplyPoolAssignmentsAsync([a], "Test");
        var entry = Assert.IsType<JsonArray>(_sims[d.Host].LastPatch!["pools"]).OfType<JsonObject>().Single();
        Assert.False((bool)entry["stratumDecodeCoinbase"]!);
    }
}
