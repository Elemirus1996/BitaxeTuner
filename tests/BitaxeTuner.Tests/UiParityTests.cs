using System.Text.RegularExpressions;

namespace BitaxeTuner.Tests;

/// <summary>
/// 0.9.24 Abgleich Browser ↔ Desktop: gleiche Begriffe, gleiche Reihenfolge, gleiche Farben. Liest app.js/app.css und die
/// XAML-Dateien der Desktop-App. Neue Einstellungskarten müssen hier eingeordnet werden – entweder mit ihrem Gegenstück
/// in der anderen Oberfläche oder ausdrücklich als „nur Browser“/„nur Desktop“ mit Grund.
/// </summary>
public class UiParityTests
{
    private static readonly string Root = RepoRoot();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    /// <summary>Desktop-Abschnitt → Browser-Karte(n) mit gleichem Inhalt.</summary>
    private static readonly (string Desktop, string[] Browser)[] SettingsMap =
    [
        ("Miner", ["Geräte"]),
        ("Programm und Tuning", ["Darstellung"]),
        ("Push-Benachrichtigungen", ["Push-Benachrichtigungen"]),
        ("Ruhezeit", ["Ruhezeit"]),
        ("Log-Alarme", ["Berichte und Log-Alarme"]),
        ("Tagesbericht", ["Berichte und Log-Alarme"]),
        ("Strom und Abfrage", ["Strompreis", "Abfrage und Speicher", "Wallet-Abfrage"]),
        ("Strompreis-Quelle", ["Strompreis-Quelle"]),
        ("Smart Plugs", ["Smart Plugs"]),
        ("PV-Anlage", ["PV-Anlage"]),
        ("Anzeige und Automatik", ["Überwachung", "Grenzwerte und Tuning"]),
        ("Pool- und Share-Überwachung", ["Überwachung"]),
        ("Pool-Konto", ["Pool-Konto"]),
        ("Weitere Pool-Konten", ["Weitere Pool-Konten"]),
        ("Chat-Bots (Telegram, Discord)", ["Chat-Bots (Telegram, Discord)"]),
        ("Handy-Ansicht (nur ansehen)", ["Ansicht-PIN"]),
        ("Datenordner", []),           // nur Desktop: Datenordner der App umziehen (der Server hat einen festen Ordner)
        ("Sicherung", ["Sicherung"]),
    ];

    /// <summary>Nur im Browser – mit Grund (Server-Funktionen oder eigene Fenster der Desktop-App).</summary>
    private static readonly Dictionary<string, string> BrowserOnly = new()
    {
        ["Einstellungen übertragen"] = "Desktop: eigenes Fenster (CopySettingsWindow)",
        ["Firmware aktualisieren"] = "Desktop: eigenes Fenster (FirmwareWindow)",
        ["Geräteprofile"] = "Desktop: eigenes Fenster (ProfilesWindow)",
        ["Übersicht gestalten"] = "nur Browser: Übersicht mit Panels",
        ["Home Assistant / MQTT"] = "Server-Dienst",
        ["Prometheus / Grafana"] = "Server-Dienst",
        ["Desktop-App verbinden (API-Token)"] = "Server: Zugang für die Desktop-App",
        ["Ansicht-Zugänge"] = "Server: Zugänge",
        ["Kiosk / Wand-Tablet"] = "Server: Kiosk",
        ["Verbindung"] = "Server: HTTPS/DuckDNS",
        ["Admin-Passwort ändern"] = "Server: Anmeldung",
        ["Systemzustand"] = "Server: Raspberry/Docker",
        ["Server-Update"] = "Server: Update",
    };

    private static List<string> BrowserCards()
    {
        var js = Read("src", "BitaxeTuner.Server", "wwwroot", "app.js");
        var start = js.IndexOf("const SETTINGS_SECTIONS = () => [", StringComparison.Ordinal);
        var block = js[start..js.IndexOf("];", start)];
        // je Bereich: ['id', t('Name'), 'symbol', [t('Karte'), …]]
        return Regex.Matches(block, @"\[t\('[^']*'\)(?:, t\('[^']*'\))*\]").SelectMany(m =>
            Regex.Matches(m.Value, @"t\('([^']*)'\)").Select(x => x.Groups[1].Value)).ToList();
    }

    private static List<string> DesktopSections() =>
        Regex.Matches(Read("src", "BitaxeTuner.App", "SettingsWindow.xaml"), @"Text=""\{l:T '([^']*)'\}"" Style=""\{StaticResource Section\}""")
            .Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Every_settings_card_has_its_counterpart_or_a_reason()
    {
        var browser = BrowserCards();
        var desktop = DesktopSections();
        Assert.Equal(SettingsMap.Select(x => x.Desktop).Order(), desktop.Order());
        foreach (var (d, cards) in SettingsMap)
            foreach (var c in cards) Assert.True(browser.Contains(c), $"Desktop „{d}“ verweist auf unbekannte Browser-Karte „{c}“");
        var mapped = SettingsMap.SelectMany(x => x.Browser).ToHashSet();
        foreach (var c in browser)
            Assert.True(mapped.Contains(c) ^ BrowserOnly.ContainsKey(c), $"Browser-Karte „{c}“: fehlt im Abgleich (oder steht doppelt)");
        Assert.Empty(BrowserOnly.Keys.Except(browser));
    }

    [Fact]
    public void Desktop_settings_follow_the_browser_order()
    {
        var browser = BrowserCards();
        var desktop = DesktopSections();
        var map = SettingsMap.ToDictionary(x => x.Desktop, x => x.Browser);
        // Position = erste zugeordnete Browser-Karte; „nur Desktop“ steht vor seinem Nachfolger
        var pos = new List<int>();
        for (var i = desktop.Count - 1; i >= 0; i--)
            pos.Insert(0, map[desktop[i]] is [var first, ..] ? browser.IndexOf(first) : (pos.Count > 0 ? pos[0] : int.MaxValue));
        for (var i = 1; i < pos.Count; i++)
            Assert.True(pos[i] >= pos[i - 1], $"Desktop „{desktop[i]}“ steht vor „{desktop[i - 1]}“ – im Browser ist es umgekehrt");
    }

    [Fact]
    public void Shared_pages_use_the_same_names()
    {
        var js = Read("src", "BitaxeTuner.Server", "wwwroot", "app.js");
        var vm = Read("src", "BitaxeTuner.App", "ViewModels", "MainViewModel.cs");
        foreach (var (kind, name) in new[] { ("Aggregate", "Übersicht"), ("Compare", "Vergleich"), ("Tax", "Steuer") })
        {
            Assert.Contains($"NavKind.{kind}, L.T(\"{name}\")", vm);
            Assert.Contains($"t('{name}')", js);
        }
    }

    [Theory]
    [InlineData("Dark.xaml", ":root")]
    [InlineData("Light.xaml", ":root[data-theme=\"light\"]")]
    public void Both_apps_use_the_same_colors(string xaml, string selector)
    {
        var css = Read("src", "BitaxeTuner.Server", "wwwroot", "app.css");
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        var vars = Regex.Matches(css[start..css.IndexOf('}', start)], @"--([a-z-]+):\s*(#[0-9A-Fa-f]{6})")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
        var brushes = Regex.Matches(Read("src", "BitaxeTuner.App", "Themes", xaml), @"x:Key=""([A-Za-z]+)Brush"" Color=""(#[0-9A-Fa-f]{6})""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
        var pairs = new Dictionary<string, string>
        {
            ["Window"] = "bg", ["Card"] = "card", ["CardBorder"] = "border", ["Field"] = "field", ["Hover"] = "hover", ["Selected"] = "selected",
            ["Text"] = "text", ["MutedText"] = "muted", ["SecondaryText"] = "secondary", ["FaintText"] = "faint",
            ["Accent"] = "accent", ["AccentDark"] = "accent-dark", ["AccentInk"] = "accent-ink",
            ["Danger"] = "danger", ["Ok"] = "ok", ["Warn"] = "warn", ["Info"] = "info", ["Idle"] = "idle", ["Temp"] = "temp", ["Marker"] = "marker",
        };
        foreach (var (brush, v) in pairs)
            Assert.True(brushes[brush] == vars[v], $"{xaml}: {brush}Brush {brushes[brush]} ≠ --{v} {vars[v]}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException();
    }
}
