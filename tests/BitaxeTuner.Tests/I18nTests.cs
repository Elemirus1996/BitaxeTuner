using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Tests;

/// <summary>Tests laufen immer auf Deutsch – unabhängig von der Systemsprache des Rechners (z. B. CI auf Englisch).</summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void ForceGerman() => Loc.Force("de");
}

/// <summary>Zweisprachigkeit: Übersetzungstabelle vollständig, Platzhalter stimmen, Rückfall auf Deutsch.</summary>
public class I18nTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Projektordner nicht gefunden.");
    }

    private static Dictionary<string, string> English() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "src", "BitaxeTuner.Core", "I18n", "Strings.en.json")))!;

    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

    /// <summary>Alle übersetzbaren Texte im Quellcode: L.T("…")/loc.T("…") in C#, {l:T '…'} in XAML, t('…') in JavaScript.</summary>
    internal static SortedSet<string> SourceTexts()
    {
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        var src = Path.Combine(Root, "src");
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\bT\(\s*""((?:[^""\\]|\\.)*)"""))
                texts.Add(Unescape(m.Groups[1].Value));
        foreach (var f in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{l:T\s+'((?:[^'\\]|\\.)*)'"))
                texts.Add(System.Net.WebUtility.HtmlDecode(Unescape(m.Groups[1].Value)));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(src, "BitaxeTuner.Server", "wwwroot"), "*.js"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\bt\(\s*'((?:[^'\\]|\\.)*)'"))
                texts.Add(Unescape(m.Groups[1].Value));
        return texts;
    }

    [Fact]
    public void Every_text_in_the_code_has_an_english_translation()
    {
        var en = English();
        var missing = SourceTexts().Where(t => !en.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} Text(e) ohne englische Übersetzung (src/BitaxeTuner.Core/I18n/Strings.en.json):\n" +
            string.Join("\n", missing.Take(40).Select(t => "  " + JsonSerializer.Serialize(t))));
    }

    [Fact]
    public void Translations_keep_all_placeholders_and_are_not_empty()
    {
        var bad = new List<string>();
        foreach (var (de, en) in English())
        {
            static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)(?:[:,][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order());
            if (string.IsNullOrWhiteSpace(en)) bad.Add($"leer: {de}");
            else if (Holes(de) != Holes(en)) bad.Add($"Platzhalter: {de} → {en}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(30)));
    }

    [Fact]
    public void Unused_translations_are_reported()
    {
        var used = SourceTexts();
        var unused = English().Keys.Where(k => !used.Contains(k)).ToList();
        Assert.True(unused.Count == 0, $"{unused.Count} Übersetzung(en) werden nicht mehr verwendet:\n" + string.Join("\n", unused.Take(30)));
    }

    [Fact]
    public void Falls_back_to_german_and_formats_with_the_language_culture()
    {
        var en = Loc.For("en");
        var de = Loc.For("de");
        Assert.Equal("de", Loc.Current.Language);                      // Tests: immer Deutsch
        Assert.Equal("Das gibt es nicht übersetzt", en.T("Das gibt es nicht übersetzt"));
        Assert.Equal("1,5", de.T("{0:0.0}", 1.5));
        Assert.Equal("1.5", en.T("{0:0.0}", 1.5));
        Assert.Equal("en", Loc.For("en-US,en;q=0.9").Language);
        Assert.Equal("de", Loc.For("de-AT").Language);
    }
}
