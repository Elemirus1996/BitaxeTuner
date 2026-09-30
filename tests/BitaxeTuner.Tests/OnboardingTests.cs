using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Tests;

/// <summary>Einführung „Erste Schritte“: wer sie sieht, wie sie sich abhakt, Aus- und Einblenden.</summary>
public class OnboardingTests
{
    [Fact]
    public void New_installation_starts_it_and_it_stays_after_adding_miners()
    {
        var c = new AppConfig();
        Assert.True(Onboarding.ShouldShow(c));                 // neu: noch kein Miner
        c.Devices.Add(new DeviceConfig { Host = "10.0.0.5" });
        Assert.True(Onboarding.ShouldShow(c));                 // bleibt bis zum Ausblenden

        var steps = Onboarding.Steps(c, server: true);
        Assert.Equal(["miners", "price", "push", "backup"], steps.Select(s => s.Id));
        Assert.True(steps[0].Done);
        Assert.False(steps[1].Done);

        c.ElectricityCtPerKwh = 27.5;
        c.Notifications.Targets.Add(new PushTarget { Provider = "ntfy", NtfyTopic = "x" });
        c.Backup.Folder.Enabled = true;
        Assert.All(Onboarding.Steps(c, server: true), s => Assert.True(s.Done));

        Onboarding.SetVisible(c, false);
        Assert.False(Onboarding.ShouldShow(c));
    }

    [Fact]
    public void Existing_installation_with_miners_only_on_request()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("""{"Devices":[{"Host":"10.0.0.5"}]}""")!;
        Assert.False(Onboarding.ShouldShow(c));                // Update: nicht ungefragt
        Onboarding.SetVisible(c, true);
        Assert.True(Onboarding.ShouldShow(c));                 // auf Wunsch wieder da

        var desktop = Onboarding.Steps(c, server: false);
        Assert.Equal("server", desktop[^1].Id);
        Assert.Equal("mode", desktop[^1].Action);
        Assert.Null(desktop[^1].Section);
    }
}
