using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11: Pool-Umschaltung – manuell je Miner/Gruppe und als freigegebene Automatik.</summary>
public sealed class PoolSwitchTests : IDisposable
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, SimulatedMinerClient> _sims = new();
    private readonly MinerHub _hub;
    private DateTime _now = new(2026, 10, 8, 12, 0, 0);

    public PoolSwitchTests()
    {
        var config = new AppConfig();
        foreach (var h in new[] { "10.0.0.41", "10.0.0.42" })
            config.Devices.Add(new DeviceConfig { Name = "Miner " + h, Host = h, Groups = ["Keller"] });
        _hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => _sims[h] = new SimulatedMinerClient(Gamma, seed: h.Length, address: h),
            Clock = () => _now,
        });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private HubDevice D(string host) => _hub.Device(host)!;

    [Fact]
    public void Indexed_firmware_swaps_only_the_pool_indexes()
    {
        const string raw = """
            {"isUsingFallbackStratum":0,"primaryPoolIndex":2,"secondaryPoolIndex":0,
             "pools":[{"id":0,"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1","stratumPassword":"*****"},
                      {"id":2,"stratumURL":"b.example","stratumPort":4444,"stratumUser":"w.2","stratumPassword":"*****"}],
             "stratumURL":"b.example","stratumPort":4444}
            """;
        var layout = PoolLayout.Parse(raw);
        Assert.True(layout.Indexed);
        Assert.Equal("b.example:4444", layout.Active!.Text);
        var plan = PoolSwitchPlan.Create(layout, layout.Pools[0]);
        Assert.Equal(0, plan.Patch["primaryPoolIndex"]);
        Assert.Equal(2, plan.Patch["secondaryPoolIndex"]);
        Assert.DoesNotContain(plan.Patch.Keys, k => k.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Null(plan.Warning);
        Assert.Contains("b.example:4444 → a.example:3333", plan.Text);
        Assert.Throws<InvalidOperationException>(() => PoolSwitchPlan.Create(layout, layout.Active!));
    }

    [Fact]
    public void Legacy_firmware_swaps_the_fields_and_warns_about_passwords()
    {
        const string raw = """
            {"isUsingFallbackStratum":0,"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1",
             "fallbackStratumURL":"b.example","fallbackStratumPort":4444,"fallbackStratumUser":"w.2"}
            """;
        var layout = PoolLayout.Parse(raw);
        Assert.False(layout.Indexed);
        var plan = PoolSwitchPlan.Create(layout, layout.Secondary!);
        Assert.Equal("b.example", plan.Patch["stratumURL"]);
        Assert.Equal("a.example", plan.Patch["fallbackStratumURL"]);
        Assert.Equal(3333, plan.Patch["fallbackStratumPort"]);
        Assert.NotNull(plan.Warning);

        // Firmware läuft schon auf dem Ersatz-Pool: zurück zum Haupt-Pool = nur Neustart
        var onFallback = PoolLayout.Parse(raw.Replace("\"isUsingFallbackStratum\":0", "\"isUsingFallbackStratum\":1"));
        Assert.Empty(PoolSwitchPlan.Create(onFallback, onFallback.Primary!).Patch);
    }

    [Fact]
    public async Task Manual_switch_writes_the_selection_restarts_and_remembers_the_home_pool()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        var layout = await _hub.PoolLayoutAsync(d);
        var backup = layout.Secondary!;

        var preview = await _hub.PoolSwitchPreviewAsync(d, backup.Key);
        Assert.Contains("pool.demo.invalid:3333 → backup.demo.invalid:4444", preview.Plan!.Text);
        Assert.Equal((0, 1), _sims[d.Host].PoolIndexes);                      // Vorschau ändert nichts

        var restarts = _sims[d.Host].RestartCount;
        var done = await _hub.SwitchPoolAsync(d, backup.Key, "Test");
        Assert.NotNull(done.Plan);
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);
        Assert.Equal(restarts + 1, _sims[d.Host].RestartCount);
        Assert.Equal("pool.demo.invalid:3333", d.Config.HomePool);
        Assert.Contains(d.LogLines, l => l.Contains("Pool umgeschaltet"));

        // „home“ führt zurück; der gemerkte Haupt-Pool bleibt
        Assert.NotNull((await _hub.SwitchPoolAsync(d, "home", "Test")).Plan);
        Assert.Equal((0, 1), _sims[d.Host].PoolIndexes);
        Assert.Equal("pool.demo.invalid:3333", d.Config.HomePool);
        Assert.NotNull((await _hub.SwitchPoolAsync(d, "home", "Test")).Skip);   // schon dort

        // Veralteter Schlüssel (Liste geändert) wird abgelehnt statt etwas Falsches zu schalten
        Assert.NotNull((await _hub.PoolSwitchPreviewAsync(d, "1|other.example:1")).Skip);
    }

    [Fact]
    public async Task Legacy_firmware_in_the_simulator_switches_as_well()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        _sims[d.Host].LegacyPools = true;
        var item = await _hub.SwitchPoolAsync(d, "backup", "Test");
        Assert.NotNull(item.Plan!.Warning);
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);
    }

    [Fact]
    public async Task Group_switch_previews_and_switches_every_member()
    {
        Assert.True(await _hub.PollNowAsync());
        var preview = await _hub.GroupPoolPreviewAsync("keller", "backup");
        Assert.Equal(2, preview.Count(p => p.Plan is not null));
        Assert.Contains("→ backup.demo.invalid:4444", MinerHub.PoolPreviewText(preview));

        var done = await _hub.SwitchGroupPoolAsync("Keller", "backup");
        Assert.All(done, i => Assert.NotNull(i.Plan));
        Assert.All(_sims.Values, s => Assert.Equal((1, 0), s.PoolIndexes));
        await Assert.ThrowsAnyAsync<Exception>(() => _hub.GroupPoolPreviewAsync("Keller", "irgendwo"));
    }

    [Fact]
    public async Task Rules_act_only_after_approval_and_any_change_needs_a_new_one()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        var rule = new PoolSwitchRule { Enabled = true, Entries = [new ScheduleEntry { Days = 127, FromHour = 10, ToHour = 14 }] };
        _hub.SavePoolRule(d, rule);
        Assert.Null(_hub.EffectivePoolRule(d));                                  // nicht freigegeben
        _hub.ApprovePoolRule(d);
        Assert.NotNull(_hub.EffectivePoolRule(d));
        var changed = d.Config.PoolAuto.Clone();
        changed.ReturnAfterMinutes = 60;
        _hub.SavePoolRule(d, changed);
        Assert.Null(_hub.EffectivePoolRule(d));                                  // Inhalt geändert → neue Freigabe

        // Gruppenregel gilt nur ohne eigene Regel
        d.Config.PoolAuto.Enabled = false;
        _hub.SaveGroupPoolRule("Keller", new PoolSwitchRule { Enabled = true, ReturnHome = true });
        Assert.Null(_hub.EffectivePoolRule(d));
        _hub.ApproveGroupPoolRule("Keller");
        Assert.Equal("Keller", _hub.EffectivePoolRule(d)!.Value.Group);
        Assert.Contains("Haupt-Pool wieder erreichbar", MinerHub.PoolRuleText(_hub.Config.GroupPoolRules[0].Rule, "Keller"));
    }

    [Fact]
    public async Task Schedule_switches_to_the_backup_and_back_afterwards()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        var rule = new PoolSwitchRule { Enabled = true, ReturnHome = false, Entries = [new ScheduleEntry { Days = 127, FromHour = 10, ToHour = 14 }] };
        _hub.PoolReachable = (_, _) => Task.FromResult(true);

        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now);                   // 12 Uhr → Ersatz-Pool
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);
        Assert.Contains(d.LogLines, l => l.Contains("Pool-Automatik") && l.Contains("Zeitplan"));

        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddMinutes(10));    // noch im Fenster: nichts
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);

        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddHours(3));       // 15 Uhr → zurück
        Assert.Equal((0, 1), _sims[d.Host].PoolIndexes);
    }

    [Fact]
    public async Task Bad_shares_switch_to_the_backup_and_return_only_when_home_answers()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        var rule = new PoolSwitchRule { Enabled = true, OnBadShares = true, BadMinutes = 15, ReturnHome = true, ReturnAfterMinutes = 30 };
        var bad = true;
        var reachable = false;
        _hub.PoolBadOverride = _ => bad;
        _hub.PoolReachable = (_, _) => Task.FromResult(reachable);

        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now);
        Assert.Equal((0, 1), _sims[d.Host].PoolIndexes);                         // erst nach 15 min
        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddMinutes(16));
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);

        bad = false;
        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddMinutes(60));    // Haupt-Pool antwortet nicht → bleibt
        Assert.Equal((1, 0), _sims[d.Host].PoolIndexes);
        reachable = true;
        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddMinutes(61));
        Assert.Equal((0, 1), _sims[d.Host].PoolIndexes);
    }

    [Fact]
    public async Task Firmware_fallback_returns_home_with_a_restart_only()
    {
        Assert.True(await _hub.PollNowAsync());
        var d = D("10.0.0.41");
        var sim = _sims[d.Host];
        sim.UsingFallbackPool = true;
        var rule = new PoolSwitchRule { Enabled = true, ReturnHome = true, ReturnAfterMinutes = 30 };
        _hub.PoolReachable = (_, _) => Task.FromResult(true);
        var restarts = sim.RestartCount;

        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now);
        Assert.Equal(restarts, sim.RestartCount);                                // Wartezeit läuft
        await _hub.EvaluatePoolRuleAsync(d, rule, null, _now.AddMinutes(31));
        Assert.Equal(restarts + 1, sim.RestartCount);
        Assert.Equal((0, 1), sim.PoolIndexes);                                   // Auswahl unverändert
        Assert.False(sim.UsingFallbackPool);
    }
}
