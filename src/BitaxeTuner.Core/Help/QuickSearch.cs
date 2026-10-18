using System.Globalization;
using System.Text;

namespace BitaxeTuner.Core.Help;

/// <summary>0.9.24 Schnellsuche (Strg+K): ein Treffer – Art (Seite, Miner, Einstellung, Aktion), Titel, Zusatz.</summary>
public sealed record QuickSearchItem(string Kind, string Label, string Hint, object? Tag = null)
{
    public string KindAndHint => Hint.Length > 0 ? $"{Kind} · {Hint}" : Kind;
}

/// <summary>
/// Trefferlogik der Schnellsuche – gleich wie im Browser (paletteFind in app.js): alle Wörter müssen vorkommen
/// (ohne Groß-/Kleinschreibung und Akzente), Titel vorn vor Titel irgendwo vor Zusatz; höchstens 14 Treffer.
/// </summary>
public static class QuickSearch
{
    public const int MaxHits = 14;

    /// <summary>Abschnitte im Einstellungsfenster der Desktop-App (Stil „Section“ in SettingsWindow.xaml, deutsche Schlüssel).</summary>
    public static readonly string[] DesktopSettingsSections =
    [
        "Miner", "Strom und Abfrage", "Anzeige und Automatik", "Programm und Tuning", "Datenordner", "Sicherung", "Strompreis",
        "Handy-Ansicht (nur ansehen)", "Ruhezeit", "Log-Alarme", "Pool- und Share-Überwachung", "Tagesbericht",
        "Push-Benachrichtigungen", "Smart Plugs", "PV-Anlage", "Pool-Konto", "Weitere Pool-Konten", "Chat-Bots (Telegram, Discord)",
    ];

    public static string Fold(string? s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(c);
        return b.ToString();
    }

    /// <summary>Treffer zur Eingabe; leer: alle außer Einstellungen (die Liste wäre sonst zu lang).</summary>
    public static List<QuickSearchItem> Find(IEnumerable<QuickSearchItem> items, string? query, string settingKind)
    {
        var words = Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return items.Where(x => x.Kind != settingKind).Take(MaxHits).ToList();
        return items.Select(x =>
            {
                var label = Fold(x.Label);
                var all = $"{label} {Fold(x.Hint)} {Fold(x.Kind)}";
                if (!words.All(all.Contains)) return (Item: x, Score: -1);
                return (Item: x, Score: label.StartsWith(words[0], StringComparison.Ordinal) ? 0 : label.Contains(words[0]) ? 1 : 2);
            })
            .Where(r => r.Score >= 0).OrderBy(r => r.Score).Take(MaxHits).Select(r => r.Item).ToList();
    }
}
