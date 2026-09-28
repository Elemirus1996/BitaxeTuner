using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace BitaxeTuner.Core.I18n;

/// <summary>
/// Sprache und Zahlen-/Datumsformat. Der deutsche Originaltext ist der Schlüssel; <c>Strings.en.json</c> ordnet ihm
/// den englischen Text zu. Fehlt eine Übersetzung, erscheint der deutsche Text (nie ein leerer).
/// <para>Desktop und Server setzen beim Start <see cref="Current"/> aus der Einstellung „Sprache“;
/// Antworten an einen Browser nutzen <see cref="For(string?)"/> mit der Sprache des Nutzers.</para>
/// </summary>
public sealed class Loc
{
    public static readonly IReadOnlyList<string> Languages = ["de", "en"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(() => LoadTable("en"));
    private static volatile Loc _current = new("de", CultureInfo.GetCultureInfo("de-DE"));
    private static string? _forced;

    private Loc(string language, CultureInfo culture)
    {
        Language = language;
        Culture = culture;
    }

    /// <summary>"de" oder "en".</summary>
    public string Language { get; }

    /// <summary>Zahlen- und Datumsformat.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Sprache dieses Programms (Desktop bzw. Server); Standard Deutsch.</summary>
    public static Loc Current => _current;

    /// <summary>Beim Start und nach Änderung der Einstellung („auto“, „de“, „en“).</summary>
    public static void Configure(string? setting) => _current = For(setting);

    /// <summary>Nur für Tests: „auto“ und der Start-Standard ergeben diese Sprache, Formate unabhängig vom System.</summary>
    internal static void Force(string? language)
    {
        _forced = language;
        _current = For(language);
    }

    /// <summary>„auto“ (bzw. leer) folgt der Systemsprache: Deutsch bei deutschem System, sonst Englisch.</summary>
    public static string Resolve(string? setting)
    {
        var s = (setting ?? "").Trim().ToLowerInvariant();
        if (s.Length >= 2 && Languages.Contains(s[..2])) return s[..2];
        if (_forced is { } f) return f;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
    }

    /// <summary>Sprache für eine Einstellung bzw. einen Browser (z. B. „en-US,en;q=0.9“).</summary>
    public static Loc For(string? setting)
    {
        var language = Resolve(setting);
        return new Loc(language, CultureFor(language));
    }

    /// <summary>
    /// Format folgt dem System, solange dessen Sprache zur Oberfläche passt; sonst (z. B. Raspberry Pi mit neutraler
    /// Systemeinstellung) das übliche Format der Sprache: Deutsch → de-DE, Englisch → en-GB (24 h, TT/MM/JJJJ).
    /// </summary>
    public static CultureInfo CultureFor(string language)
    {
        var system = CultureInfo.CurrentCulture;
        if (_forced is null && !Equals(system, CultureInfo.InvariantCulture) && system.TwoLetterISOLanguageName == language)
            return system;
        return CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-GB");
    }

    /// <summary>
    /// Sprache einer Anfrage (Accept-Language, die Browser-Oberfläche schickt ihre gewählte Sprache);
    /// ohne bzw. mit nicht unterstützter Sprache die des Programms.
    /// </summary>
    public static Loc ForRequest(string? acceptLanguage)
    {
        var s = (acceptLanguage ?? "").Trim().ToLowerInvariant();
        return s.Length >= 2 && Languages.Contains(s[..2]) ? For(s[..2]) : Current;
    }

    /// <summary>Text in dieser Sprache.</summary>
    public string T(string german) =>
        Language == "de" || !English.Value.TryGetValue(german, out var text) || text.Length == 0 ? german : text;

    /// <summary>Text mit Platzhaltern {0}, {1:0.0} …, formatiert in <see cref="Culture"/>.</summary>
    public string T(string german, params object?[] args) => string.Format(Culture, T(german), args);

    /// <summary>Übersetzungstabelle einer Sprache (für die Browser-Oberfläche); Deutsch ist leer.</summary>
    public static IReadOnlyDictionary<string, string> Table(string language) =>
        Resolve(language) == "en" ? English.Value : new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> LoadTable(string language)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BitaxeTuner.Core.I18n.Strings.{language}.json");
        if (s is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
    }
}

/// <summary>Kurzform für die Sprache dieses Programms (Text mit Platzhaltern {0}, {1} …).</summary>
public static class L
{
    public static string T(string german) => Loc.Current.T(german);
    public static string T(string german, params object?[] args) => Loc.Current.T(german, args);
    public static CultureInfo Culture => Loc.Current.Culture;

    /// <summary>
    /// Markiert einen Text als übersetzbar, ohne ihn zu übersetzen (wie gettext „N_“): für Meldungen, die erst bei
    /// der Ausgabe in die Sprache des Empfängers übersetzt werden (z. B. Fehlermeldungen an den Browser).
    /// </summary>
    public static string N(string german) => german;
}
