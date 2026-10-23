using System.Globalization;
using System.Text.RegularExpressions;

namespace BitaxeTuner.Tests;

/// <summary>
/// 0.9.24 Barrierefreiheit: Textfarben der Browser-Oberfläche erreichen auf Karten und Hintergrund mindestens 4,5:1
/// (WCAG AA) – im dunklen und hellen Design, mit und ohne „Hoher Kontrast“.
/// </summary>
public class ContrastTests
{
    private static readonly string Css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BitaxeTuner.Server", "wwwroot", "app.css"));

    private static Dictionary<string, string> Vars(string selector)
    {
        var start = Css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, selector);
        var block = Css[start..Css.IndexOf('}', start)];
        return Regex.Matches(block, @"--([a-z-]+):\s*(#[0-9A-Fa-f]{6})").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static double Lum(string hex)
    {
        double C(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
    }

    private static double Ratio(string a, string b)
    {
        var (x, y) = (Lum(a), Lum(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Theory]
    [InlineData(":root", null)]
    [InlineData(":root[data-theme=\"light\"]", null)]
    [InlineData(":root[data-theme=\"light\"]", ":root.hc[data-theme=\"light\"]")]
    [InlineData(":root", ":root.hc")]
    public void Text_colors_reach_wcag_aa(string theme, string? highContrast)
    {
        var v = Vars(theme);
        if (highContrast is not null) foreach (var (k, c) in Vars(highContrast)) v[k] = c;
        var dark = Lum(v["card"]) < 0.2;
        // Statusfarben als Text: im dunklen Design auch auf Feldern, im hellen auf Karten (Felder dort fast weiß)
        foreach (var bg in new[] { "card", "bg" })
            foreach (var fg in new[] { "text", "muted", "secondary", "faint", "accent-text" }.Concat(dark ? [] : new[] { "ok", "warn", "danger", "accent-dark" }))
                Assert.True(Ratio(v[fg], v[bg]) >= 4.5, $"{theme} {highContrast}: --{fg} {v[fg]} auf --{bg} {v[bg]} = {Ratio(v[fg], v[bg]):0.00}");
        Assert.True(Ratio(v["accent-ink"], v["accent"]) >= 4.5, $"{theme}: Schrift auf Akzent-Knopf {Ratio(v["accent-ink"], v["accent"]):0.00}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException();
    }
}
