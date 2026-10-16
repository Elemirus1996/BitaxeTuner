using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.21 Ruhezeit: Push sammeln und zusammenfassen, Lüfter begrenzen – nie gegen die Sicherheit.</summary>
public class QuietHoursTests
{
    [Theory]
    [InlineData(22, 7, 23, true)]
    [InlineData(22, 7, 3, true)]
    [InlineData(22, 7, 7, false)]
    [InlineData(22, 7, 12, false)]
    [InlineData(1, 6, 2, true)]
    [InlineData(1, 6, 23, false)]
    public void Window_works_over_midnight(int from, int to, int hour, bool quiet) =>
        Assert.Equal(quiet, new QuietHoursSettings { Enabled = true, FromHour = from, ToHour = to }.IsQuiet(new DateTime(2026, 10, 17, hour, 30, 0)));

    [Fact]
    public async Task Normal_pushes_wait_for_one_summary_important_ones_come_at_once()
    {
        using var dir = new TempDir();
        var config = new AppConfig();
        config.Quiet = new QuietHoursSettings { Enabled = true, FromHour = 22, ToHour = 7 };
        config.Notifications.Targets =
        [
            new PushTarget { Id = "privat", Provider = "ntfy", NtfyTopic = "a" },
            new PushTarget { Id = "immer", Provider = "ntfy", NtfyTopic = "b", IgnoreQuiet = true },
        ];
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        var clock = new DateTime(2026, 10, 17, 23, 15, 0);
        hub.Notify.Clock = () => clock;
        var delivered = new List<(string Target, string Key, string Text)>();
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        hub.Notify.Delivered += (target, key, text) => delivered.Add((target, key, text));

        await hub.Notify.SendAsync("pool:1", "Pool", "Worker ohne Shares", NotifyPriority.Normal, null, NotifyCategory.Pool);
        await hub.Notify.SendAsync("maint:1", "Watchdog", "Neustart", NotifyPriority.Normal, null, NotifyCategory.Maintenance);
        await hub.Notify.SendAsync("offline:1", "Offline", "Gamma offline", NotifyPriority.High, null, NotifyCategory.Offline);
        await hub.Notify.SendAsync("find:1", "Zufluss", "0,001 BTC", NotifyPriority.Normal, null, NotifyCategory.Finds);

        // „privat“ bekommt nur das Wichtige sofort, „immer“ alles
        Assert.Equal(["offline:1", "find:1"], delivered.Where(d => d.Target == "privat").Select(d => d.Key));
        Assert.Equal(4, delivered.Count(d => d.Target == "immer"));
        Assert.Equal(2, hub.Notify.Digest.Count);
        Assert.True(File.Exists(Path.Combine(dir.Path, "quiet-digest.json")));   // übersteht einen Neustart

        // Noch Ruhezeit: nichts
        Assert.Equal(0, await hub.Notify.FlushDigestAsync(new DateTime(2026, 10, 18, 6, 59, 0)));
        // Danach: eine Zusammenfassung, nur einmal
        Assert.Equal(1, await hub.Notify.FlushDigestAsync(new DateTime(2026, 10, 18, 7, 0, 0)));
        Assert.Equal(0, await hub.Notify.FlushDigestAsync(new DateTime(2026, 10, 18, 7, 1, 0)));
        var summary = Assert.Single(delivered, d => d.Key == "quiet-digest");
        Assert.Equal("privat", summary.Target);
        Assert.Contains("Worker ohne Shares", summary.Text);
        Assert.Contains("Neustart", summary.Text);
        Assert.Empty(hub.Notify.Digest);

        // Sammeln abgeschaltet: wieder sofort
        config.Quiet.CollectPush = false;
        await hub.Notify.SendAsync("pool:2", "Pool", "x", NotifyPriority.Normal, null, NotifyCategory.Pool);
        Assert.Contains(delivered, d => d.Target == "privat" && d.Key == "pool:2");
    }

    [Fact]
    public void Case_fans_are_capped_in_quiet_hours_but_never_at_full_load_temperature()
    {
        var at23 = new DateTime(2026, 10, 17, 23, 0, 0);
        var s = new FanSettings { Enabled = true };
        s.Channel(4).Role = "case";
        s.Case.Curve = new FanCurve { StartTemp = 45, StartPercent = 30, FullTemp = 70, MinPercent = 25, Hysteresis = 0 };
        MinerTemps M(double vr) => new("10.0.0.1", "A", true, vr, 55, at23.AddSeconds(-2));

        var free = new FanController().Compute(s, [M(66)], at23)[3];
        var quiet = new FanController { QuietMaxPercent = 45 }.Compute(s, [M(66)], at23)[3];
        Assert.True(free.Percent > 45);
        Assert.Equal(45, quiet.Percent);
        Assert.Contains("Ruhezeit", quiet.Reason);
        Assert.Equal(100, new FanController { QuietMaxPercent = 45 }.Compute(s, [M(72)], at23)[3].Percent);   // Sicherheit geht vor
    }
}
