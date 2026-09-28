using System.Globalization;

namespace BitaxeTuner.Core.I18n;

/// <summary>
/// Übersetzbarer Text als Platzhalter-Wert in einem anderen Text: wird beim Formatieren in derselben Sprache
/// übersetzt wie der umgebende Text (erkannt an dessen Kultur), z. B.
/// eine Fehlermeldung „{0}: unbekannter Modus.“ mit einer <see cref="LocText"/> „Gehäuse“ als {0}.
/// </summary>
public sealed class LocText(string german, params object?[] args) : IFormattable
{
    public string German { get; } = german;
    public object?[] Args { get; } = args;

    public string ToString(string? format, IFormatProvider? provider)
    {
        var loc = Loc.For(provider is CultureInfo c && Loc.Languages.Contains(c.TwoLetterISOLanguageName) ? c.TwoLetterISOLanguageName : Loc.Current.Language);
        return Args.Length == 0 ? loc.T(German) : loc.T(German, Args);
    }

    public override string ToString() => ToString(null, null);
}
