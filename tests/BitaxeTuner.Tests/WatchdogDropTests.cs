using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>0.9.12: Watchdog startet auch neu, wenn die Hashrate deutlich unter dem Normalwert liegt.</summary>
[Collection(nameof(LanguageSwitch))]
public class WatchdogDropTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static WatchdogSettings On() => new() { DropEnabled = true, DropPercent = 20, DropMinutes = 15, CooldownMinutes = 60 };

    [Fact]
    public void Restarts_only_after_the_drop_lasted_the_whole_time()
    {
        var w = new Watchdog();
        var s = On();
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0));             // 30 % darunter – Zeit beginnt
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(10)));
        Assert.False(w.ShouldRebootForDrop("a", true, 850, 1000, s, utcNow: T0.AddMinutes(12)));   // kurz normal → von vorn
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(13)));
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(27)));
        Assert.True(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(28)));

        // Sperrzeit: nicht sofort wieder
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(29)));
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(45)));
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(60)));
        Assert.True(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(90)));
    }

    [Theory]
    [InlineData(false, true, 700, 1000.0, false)]    // aus
    [InlineData(true, false, 700, 1000.0, false)]    // offline – nicht per API neu startbar
    [InlineData(true, true, 0.5, 1000.0, false)]     // keine Hashrate: das regelt die Null-Regel
    [InlineData(true, true, 820, 1000.0, false)]     // nur 18 % darunter
    [InlineData(true, true, 700, null, false)]       // kein Normalwert bekannt
    public void Never_without_reason(bool enabled, bool online, double gh, double? normal, bool maintenance)
    {
        var w = new Watchdog();
        var s = On();
        s.DropEnabled = enabled;
        for (var m = 0; m <= 120; m += 5)
            Assert.False(w.ShouldRebootForDrop("a", online, gh, normal, s, maintenance, T0.AddMinutes(m)));
    }

    [Fact]
    public void Maintenance_resets_the_drop_time()
    {
        var w = new Watchdog();
        var s = On();
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0));
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, inMaintenance: true, utcNow: T0.AddMinutes(10)));
        Assert.False(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(16)));        // beginnt neu
        Assert.True(w.ShouldRebootForDrop("a", true, 700, 1000, s, utcNow: T0.AddMinutes(31)));
    }

    [Fact]
    public void Settings_are_limited_and_the_log_entry_is_explained()
    {
        var s = new WatchdogSettings { DropPercent = 99, DropMinutes = 1, ZeroHashMinutes = 1, CooldownMinutes = 1 };
        s.Normalize();
        Assert.Equal((90, 5, 3, 10), (s.DropPercent, s.DropMinutes, s.ZeroHashMinutes, s.CooldownMinutes));

        var de = L.T("Watchdog: {0} – {1}", L.T("{0} min lang mehr als {1} % unter dem Normalwert ({2} statt {3})", 15, 20, "700 GH/s", "1,00 TH/s"), "Neustart ausgelöst");
        Assert.Equal("watchdog", EventExplanations.For(new EventEntry(DateTime.Now, "10.0.0.5", EventCategories.Automation, de)).Id);
        var old = "Watchdog: 10 min ohne Hashrate – Neustart ausgelöst";                        // Eintrag vor 0.9.12
        Assert.Equal("watchdog", EventExplanations.For(new EventEntry(DateTime.Now, "10.0.0.5", EventCategories.Automation, old)).Id);
    }
}
