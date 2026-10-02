using System.Text.Json;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

[Collection(nameof(LanguageSwitch))]
public class EventExplanationTests
{
    private static EventEntry Entry(string category, string message) => new(DateTime.Now, "10.0.0.5", category, message);

    [Fact]
    public void Every_template_is_a_text_used_in_the_code()
    {
        // Strings.en.json enthält nur Texte, die im Code vorkommen (I18nTests prüfen „unbenutzt“)
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BitaxeTuner.Core", "I18n", "Strings.en.json")))!;
        Assert.All(EventExplanations.Templates, tpl => Assert.True(table.ContainsKey(tpl), "Vorlage nicht im Code: " + tpl));
    }

    [Fact]
    public void Schedule_limit_notice_from_the_field_is_explained_in_german_and_english()
    {
        var de = "Zeitplan: „Abend (600 MHz / 1200 mV)“ liegt außerhalb der Grenzen für Generisch / unbekannt – wird nicht gesetzt.";
        Assert.Equal("auto-limit", EventExplanations.For(Entry(EventCategories.Automation, de)).Id);
        var en = "Schedule: " + Loc.For("en").T("„{0}“ liegt außerhalb der Grenzen für {1} – wird nicht gesetzt.", "Evening (600 MHz / 1200 mV)", "Generic / unknown");
        Assert.Equal("auto-limit", EventExplanations.For(Entry(EventCategories.Automation, en)).Id);
    }

    [Fact]
    public void Specific_explanations_win_and_every_entry_gets_one()
    {
        Assert.Equal("fan-stalled", EventExplanations.For(Entry(EventCategories.Fans, "Lüfter K2 (VR Supra) steht!")).Id);
        Assert.Equal("fan-level", EventExplanations.For(Entry(EventCategories.Fans, "Lüfter K2 (VR Supra): 65 % – VR 72 °C")).Id);
        Assert.Equal("offline", EventExplanations.For(Entry(EventCategories.Connection, "offline: Zeitüberschreitung")).Id);
        Assert.Equal("bench-vr", EventExplanations.For(Entry(EventCategories.Benchmark, "VR-Temperatur 86,4 °C > 85 °C")).Id);
        var unknown = EventExplanations.For(Entry(EventCategories.Tuning, "manuell: 500 MHz / 1150 mV → 525 MHz / 1150 mV"));
        Assert.Equal("cat-tuning", unknown.Id);                                      // Rückfall je Kategorie
        Assert.False(string.IsNullOrWhiteSpace(unknown.Meaning));
        Assert.Equal("cat-other", EventExplanations.For(Entry("other", "irgendwas")).Id);
    }
}
