using System.Text.RegularExpressions;
using BitaxeTuner.Core.Help;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24: Schnellsuche (Strg+K) – Trefferlogik wie im Browser.</summary>
public class QuickSearchTests
{
    private static readonly List<QuickSearchItem> Items =
    [
        new("Seite", "Übersicht", "Überwachen"),
        new("Miner", "Gamma Flur", "10.0.24.5"),
        new("Einstellung", "Sicherung", "System"),
        new("Einstellung", "Push-Benachrichtigungen", "Benachrichtigungen"),
        new("Aktion", "Jetzt sichern", "Sicherung"),
    ];

    [Fact]
    public void All_words_must_match_ignoring_case_and_accents()
    {
        Assert.Equal(["Übersicht"], QuickSearch.Find(Items, "uber", "Einstellung").Select(x => x.Label));
        Assert.Equal(["Gamma Flur"], QuickSearch.Find(Items, "flur gamma", "Einstellung").Select(x => x.Label));
        Assert.Equal(["Gamma Flur"], QuickSearch.Find(Items, "10.0.24", "Einstellung").Select(x => x.Label));
        Assert.Empty(QuickSearch.Find(Items, "gamma büro", "Einstellung"));
    }

    [Fact]
    public void Title_start_ranks_before_title_inside_before_hint()
    {
        // „sicher“: „Sicherung“ (Titel vorn) vor „Jetzt sichern“ (im Titel)
        Assert.Equal(["Sicherung", "Jetzt sichern"], QuickSearch.Find(Items, "sicher", "Einstellung").Select(x => x.Label));
        Assert.Equal("Push-Benachrichtigungen", QuickSearch.Find(Items, "benachricht", "Einstellung")[0].Label);
    }

    [Fact]
    public void Empty_query_lists_everything_but_settings()
    {
        Assert.DoesNotContain(QuickSearch.Find(Items, " ", "Einstellung"), x => x.Kind == "Einstellung");
        Assert.Equal(3, QuickSearch.Find(Items, "", "Einstellung").Count);
    }

    [Fact]
    public void Desktop_section_list_matches_the_settings_window()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props"))) root = Path.GetDirectoryName(root)!;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "BitaxeTuner.App", "SettingsWindow.xaml"));
        var titles = Regex.Matches(xaml, @"<TextBlock[^>]*StaticResource Section\}[^>]*>|<TextBlock[^>]*Text=""\{l:T '([^']*)'\}""[^>]*StaticResource Section\}[^>]*>")
            .Select(m => Regex.Match(m.Value, @"l:T '([^']*)'").Groups[1].Value).ToList();
        Assert.Equal(QuickSearch.DesktopSettingsSections, titles);
    }
}
