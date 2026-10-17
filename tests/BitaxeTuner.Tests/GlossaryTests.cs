using System.Text.RegularExpressions;
using BitaxeTuner.Core.Help;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Tests;

/// <summary>0.9.23 Fachbegriffe: ein Glossar für Browser, Desktop und Hilfe.</summary>
public class GlossaryTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir ?? throw new InvalidOperationException("Repository nicht gefunden"), .. parts]);
    }

    [Fact]
    public void Every_term_is_unique_short_and_translated()
    {
        var de = Glossary.All(s => s);
        Assert.True(de.Count >= 12);
        Assert.Equal(de.Count, de.Select(g => g.Key).Distinct().Count());
        foreach (var lang in new[] { "en", "es", "pt", "fr", "nl" })
        {
            var loc = Loc.ForRequest(lang);
            var tr = Glossary.All(s => loc.T(s));
            for (var i = 0; i < de.Count; i++)
                Assert.NotEqual(de[i].Text, tr[i].Text);   // Erklärung übersetzt
        }
        Assert.All(de, g => Assert.True(g.Text.Length < 260, g.Key));   // „in einem Satz“
    }

    [Fact]
    public void Help_lists_all_terms()
    {
        var section = HelpContent.Sections(server: true).Single(s => s.Id == "glossary");
        Assert.Equal(Glossary.All().Select(g => g.Term), section.Items.Select(i => i.Title));
    }

    [Fact]
    public void Browser_and_desktop_only_use_existing_terms()
    {
        var keys = Glossary.All().Select(g => g.Key).ToHashSet();
        var js = File.ReadAllText(RepoFile("src", "BitaxeTuner.Server", "wwwroot", "app.js"));
        // Aufrufe mit einer Ebene verschachtelter Klammern (t('…') im Begriff); Schlüssel sind kleingeschriebene Wörter
        var used = Regex.Matches(js, @"term(?:Label|Help)\((?:[^()]|\([^()]*\))*\)")
            .SelectMany(m => Regex.Matches(m.Value, @"'([a-z]+)'").Select(x => x.Groups[1].Value)).ToList();
        // Mining-Art-Vergleich: Zuordnung Zeile → Begriff
        used.AddRange(Regex.Matches(js, @"\{ pps: '([a-z]+)', pplns: '([a-z]+)', 'solo-btc': '([a-z]+)'").SelectMany(m => m.Groups.Values.Skip(1).Select(g => g.Value)));
        var xaml = File.ReadAllText(RepoFile("src", "BitaxeTuner.App", "MainWindow.xaml"));
        used.AddRange(Regex.Matches(xaml, "TermHint Key=\"([a-z]+)\"").Select(m => m.Groups[1].Value));
        Assert.True(used.Count >= 10);
        Assert.All(used, k => Assert.Contains(k, keys));
    }
}
