namespace BitaxeTuner.Core.I18n;

/// <summary>
/// Meldung für den Nutzer mit Platzhaltern: <see cref="Exception.Message"/> ist der deutsche Text (Protokoll),
/// die Oberfläche übersetzt <see cref="Text"/> mit <see cref="Args"/> in ihre Sprache.
/// Feste Meldungen ohne Platzhalter brauchen das nicht – sie werden über den Text selbst nachgeschlagen.
/// </summary>
public sealed class LocalizedException(string text, params object?[] args)
    : InvalidOperationException(Loc.For("de").T(text, args))
{
    public string Text { get; } = text;

    /// <summary>HTTP-Status für die Browser-Oberfläche (400 = ungültige Anfrage, 404 = nicht gefunden).</summary>
    public int Status { get; init; } = 400;
    public object?[] Args { get; } = args;

    public string In(Loc loc) => loc.T(Text, Args);
}
