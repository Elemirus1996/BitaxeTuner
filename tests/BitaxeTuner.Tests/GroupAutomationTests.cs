using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>Gruppen-Automatik (0.9.7): eine Regel für eine Gruppe, jeder Miner mit eigener Voreinstellung gleichen Namens.</summary>
public sealed class GroupAutomationTests : IDisposable
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private static MinerInfo I(int f, int mv) => new() { FrequencyMhz = f, CoreVoltageMv = mv, ChipTempC = 55, VrTempC = 60 };

    private static PresetScheduleRule Always(string preset) => new()
    {
        Enabled = true, Mode = "time", Entries = [new ScheduleEntry { Days = 127, FromHour = 0, ToHour = 24, Preset = preset }],
    };

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Each_miner_uses_its_own_preset_of_the_same_name()
    {
        var engine = new AutomationEngine(null);
        var t = new DateTime(2026, 10, 3, 12, 0, 0);
        var a = new DeviceConfig { Name = "A", Host = "10.0.0.1", Presets = [new("Nacht", 500, 1150)] };
        var b = new DeviceConfig { Name = "B", Host = "10.0.0.2", Presets = [new("nacht", 525, 1160)] };
        var c = new DeviceConfig { Name = "C", Host = "10.0.0.3", Presets = [new("Tag", 600, 1200)] };
        var rule = (Always("Nacht"), "Keller");

        Assert.Equal((500, 1150), Target(engine.Evaluate(a, I(600, 1200), Gamma, t, false, false, groupSchedule: rule).Action));
        Assert.Equal((525, 1160), Target(engine.Evaluate(b, I(600, 1200), Gamma, t, false, false, groupSchedule: rule).Action));
        var skipped = engine.Evaluate(c, I(600, 1200), Gamma, t, false, false, groupSchedule: rule);
        Assert.Null(skipped.Action);
        Assert.Contains("übersprungen", skipped.Notices.Single().Message);
        Assert.Contains("Keller", engine.Evaluate(a, I(500, 1150), Gamma, t.AddMinutes(1), false, false, groupSchedule: rule).Status);
    }

    [Fact]
    public void Own_rule_wins_and_out_of_limit_presets_are_not_set()
    {
        var engine = new AutomationEngine(null);
        var t = new DateTime(2026, 10, 3, 12, 0, 0);
        var own = new DeviceConfig { Name = "A", Host = "10.0.0.1", Presets = [new("Nacht", 500, 1150), new("Tag", 600, 1200)] };
        own.Schedule = Always("Tag");                                  // eigene Regel eingeschaltet, aber nicht freigegeben
        Assert.Null(engine.Evaluate(own, I(500, 1150), Gamma, t, false, false, groupSchedule: (Always("Nacht"), "Keller")).Action);

        var hot = new DeviceConfig { Name = "B", Host = "10.0.0.2", Presets = [new("Nacht", 2000, 1400)] };
        var r = engine.Evaluate(hot, I(600, 1200), Gamma, t, false, false, groupSchedule: (Always("Nacht"), "Keller"));
        Assert.Null(r.Action);
        Assert.Contains("außerhalb der Grenzen", r.Notices.Single().Message);
    }

    private static (int, int)? Target(AutomationAction? a) => a is null ? null : (a.FrequencyMhz, a.CoreVoltageMv);

    private MinerHub Hub(AppConfig config) => new(config, new MinerHubOptions
    {
        DataDirectory = _dir.Path,
        OnlineChecks = false,
        ClientFactory = h => new SimulatedMinerClient(Gamma, 1, h),
        FanDeviceFactory = _ => new SimulatedFanDevice(),
    });

    private static AppConfig Config() => new()
    {
        Devices =
        [
            new DeviceConfig { Name = "A", Host = "10.0.9.1", Groups = ["Keller"], Presets = [new("Nacht", 500, 1150)] },
            new DeviceConfig { Name = "B", Host = "10.0.9.2", Groups = ["keller"], Presets = [new("Nacht", 525, 1160)] },
            new DeviceConfig { Name = "C", Host = "10.0.9.3", Groups = ["Büro"] },
        ],
    };

    [Fact]
    public void Approval_covers_rule_and_members()
    {
        var config = Config();
        using var hub = Hub(config);
        var rule = hub.SaveGroupSchedule("Keller", Always("Nacht"));
        Assert.False(hub.IsGroupScheduleApproved(rule));
        Assert.Contains("525 MHz", hub.GroupScheduleApprovalText("Keller"));          // je Miner seine eigene Voreinstellung
        hub.ApproveGroupSchedule("Keller");
        Assert.True(hub.IsGroupScheduleApproved(rule));

        config.Devices[2].Groups.Add("Keller");                                     // neues Mitglied → neue Freigabe nötig
        Assert.False(hub.IsGroupScheduleApproved(rule));
        config.Devices[2].Groups.Remove("Keller");
        Assert.True(hub.IsGroupScheduleApproved(rule));

        // Audit N-S4: geänderte Werte einer verwendeten Voreinstellung → neue Freigabe nötig
        var preset = config.Devices[0].Presets.First(p => p.Name == "Nacht");
        var old = preset.CoreVoltageMv;
        config.Devices[0].Presets[config.Devices[0].Presets.IndexOf(preset)] = preset with { CoreVoltageMv = old + 50 };
        Assert.False(hub.IsGroupScheduleApproved(rule));
        config.Devices[0].Presets[config.Devices[0].Presets.FindIndex(p => p.Name == "Nacht")] = preset;
        Assert.True(hub.IsGroupScheduleApproved(rule));
        Assert.Contains("Temperaturschutz", hub.GroupScheduleApprovalText("Keller"));
        Assert.DoesNotContain("bleibt aktiv", hub.GroupScheduleApprovalText("Keller"));   // hier nirgends freigegeben

        hub.SaveGroupSchedule("Keller", Always("Tag"));                             // geänderte Regel → neue Freigabe nötig
        Assert.False(hub.IsGroupScheduleApproved(hub.GroupRule("Keller")!));
        Assert.Equal(["Nacht"], hub.GroupPresetNames("Keller"));
    }

    [Fact]
    public async Task Switch_now_previews_old_and_new_and_skips_miners_without_the_preset()
    {
        var config = Config();
        config.Devices[2].Groups.Add("Keller");
        using var hub = Hub(config);
        await hub.PollNowAsync();
        var preview = await hub.PreviewGroupPresetAsync("Keller", "Nacht");
        Assert.Equal(3, preview.Count);
        Assert.Equal(500, preview[0].Target!.FrequencyMhz);
        Assert.NotNull(preview[0].FrequencyMhz);                                      // aktueller Wert vom Miner
        Assert.Contains("keine Voreinstellung", preview[2].Skip);

        var results = await hub.ApplyGroupPresetAsync("Keller", "Nacht");
        Assert.StartsWith("gesetzt", results[0].Result);
        Assert.StartsWith("gesetzt", results[1].Result);
        Assert.StartsWith("übersprungen", results[2].Result);
        Assert.Equal(500, (await hub.Devices[0].Connection.GetInfoAsync()).FrequencyMhz);
    }
}
