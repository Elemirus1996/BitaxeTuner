using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Integrations;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

/// <summary>Stellt die Programmsprache um – darf nicht parallel zu anderen Tests laufen (die erwarten Deutsch).</summary>
[CollectionDefinition(nameof(LanguageSwitch), DisableParallelization = true)]
public sealed class LanguageSwitch;

/// <summary>Server-Ausgaben (Tagesbericht, Home Assistant) in der eingestellten Sprache.</summary>
[Collection(nameof(LanguageSwitch))]
public class EnglishOutputTests
{
    private static T InEnglish<T>(Func<T> action)
    {
        Loc.Configure("en");
        try { return action(); }
        finally { Loc.Force("de"); }
    }

    [Fact]
    public void Daily_report_follows_the_server_language_and_format()
    {
        using var dir = new TempDir();
        using var h = new HistoryStore(dir.File("history.db"));
        var now = new DateTime(2026, 9, 25, 20, 0, 0);
        for (var m = 1; m <= 60; m++) h.AddSample("a", now.AddMinutes(-m), 1200, 55, 20, true);
        h.AddTuningEvent(new TuningEvent("a", now.AddHours(-2), TuningSource.Manual, 525, 1150, 550, 1170, null));

        var (title, text) = InEnglish(() => DailyReport.Build(h, new AppConfig { ElectricityCtPerKwh = 30 }, [("Gamma", "a"), ("Gone", "b")], now));

        Assert.Equal("Daily report 25/09/2026", title);     // en-GB
        Assert.Contains("1.20 TH/s", text);
        Assert.Contains("0.48 kWh ≈ 0.14 €", text);
        Assert.Contains("1 tuning change(s)", text);
        Assert.Contains("Gone: no data", text);
        Assert.DoesNotContain("Tagesbericht", text);
    }

    [Fact]
    public void Home_assistant_fan_modes_are_translated_but_accept_both_languages()
    {
        Assert.Equal(["Automatic", "100 %", "Off"], InEnglish(() => MqttBridge.FanModes));
        Assert.Equal(["Automatik", "100 %", "Aus"], MqttBridge.FanModes);
        // Nach einem Sprachwechsel schickt Home Assistant evtl. noch die alte Option
        Assert.Equal(FanOverride.Off, MqttBridge.ParseFanMode("Aus"));
        Assert.Equal(FanOverride.Off, MqttBridge.ParseFanMode("Off"));
        Assert.Equal(FanOverride.None, MqttBridge.ParseFanMode("Automatic"));
        Assert.Equal(FanOverride.Full, MqttBridge.ParseFanMode("100 %"));
        Assert.Null(MqttBridge.ParseFanMode("egal"));
    }

    [Fact]
    public void Schedule_days_are_shown_in_the_app_language()
    {
        var e = new ScheduleEntry { DaysText = "Sa,So" };
        Assert.Equal("Sa,Su", InEnglish(() => e.DaysText));
        Assert.Equal("daily", InEnglish(() => new ScheduleEntry { Days = 127 }.DaysText));
    }

    [Fact]
    public void Onboarding_and_whats_new_for_browsers_stay_german_keys_on_an_english_server()
    {
        var c = new AppConfig();
        var (english, keys, features) = InEnglish(() => (
            Onboarding.Steps(c, server: true)[0].Title,
            Onboarding.Steps(c, server: true, Loc.For("de")),
            WhatsNew.All(server: true, Loc.For("de"))));
        Assert.Equal("Add miners", english);                         // Desktop/Server-Sprache
        Assert.Equal("Miner hinzufügen", keys[0].Title);              // an den Browser: Schlüssel, er übersetzt selbst
        Assert.Equal("Sicherung", keys.Single(s => s.Id == "backup").Section);
        Assert.Contains(features, f => f.Title == "Sicherung einfacher" && f.Section == "Sicherung");
    }
}
