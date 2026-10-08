namespace BitaxeTuner.Core.Config;

/// <summary>Eine wählbare Landeswährung: ISO-Code (auch für CoinGecko), Zeichen und Name der Untereinheit für Strompreise.</summary>
/// <remarks>Name ist der deutsche Text; zur Anzeige mit L.T übersetzen.</remarks>
public sealed record CurrencyInfo(string Code, string Symbol, string Name, string Cent)
{
    /// <summary>Einheit des Strompreises, z. B. „ct/kWh“ oder „p/kWh“.</summary>
    public string CentPerKwh => Cent + "/kWh";

    /// <summary>Für CoinGecko (<c>vs_currency</c>).</summary>
    public string GeckoCode => Code.ToLowerInvariant();

    public bool IsEuro => Code == Currencies.Euro;
}

/// <summary>
/// 0.9.11 „Eine Währung für alles“: Kurse, Erträge, Stromkosten, Berichte, E-Paper und Steuer-Übersicht in einer Währung.
/// Strompreise bleiben intern in Hundertsteln (ct, Cent, Pence, Rappen …).
/// </summary>
public static class Currencies
{
    public const string Euro = "EUR";

    public static readonly IReadOnlyList<CurrencyInfo> All =
    [
        new("EUR", "€", I18n.L.N("Euro"), "ct"),
        new("USD", "$", I18n.L.N("US-Dollar"), "¢"),
        new("GBP", "£", I18n.L.N("Britisches Pfund"), "p"),
        new("CHF", "CHF", I18n.L.N("Schweizer Franken"), "Rp."),
        new("CAD", "CA$", I18n.L.N("Kanadischer Dollar"), "¢"),
        new("AUD", "A$", I18n.L.N("Australischer Dollar"), "¢"),
        new("PLN", "zł", I18n.L.N("Polnischer Złoty"), "gr"),
        new("CZK", "Kč", I18n.L.N("Tschechische Krone"), "hal."),
        new("SEK", "kr", I18n.L.N("Schwedische Krone"), "öre"),
        new("NOK", "kr", I18n.L.N("Norwegische Krone"), "øre"),
        new("DKK", "kr.", I18n.L.N("Dänische Krone"), "øre"),
    ];

    /// <summary>Währung zum Code; unbekannt → Euro.</summary>
    public static CurrencyInfo Get(string? code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool IsKnown(string? code) =>
        All.Any(c => string.Equals(c.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Eingestellte Währung. Ältere Einstellungen kennen nur das frei eingegebene Zeichen (<see cref="AppConfig.Currency"/>) –
    /// daraus wird die Währung abgeleitet, ohne es zu ändern.
    /// </summary>
    public static CurrencyInfo Of(AppConfig config)
    {
        if (IsKnown(config.CurrencyCode)) return Get(config.CurrencyCode);
        var s = (config.Currency ?? "").Trim();
        return All.FirstOrDefault(c => string.Equals(c.Code, s, StringComparison.OrdinalIgnoreCase))
               ?? s switch
               {
                   "$" or "US$" => Get("USD"),
                   "£" => Get("GBP"),
                   "Fr." or "Fr" or "SFr." => Get("CHF"),
                   "zł" => Get("PLN"),
                   "Kč" => Get("CZK"),
                   _ => All[0],
               };
    }

    /// <summary>Währung setzen; das Anzeige-Zeichen folgt.</summary>
    public static void Set(AppConfig config, string? code)
    {
        var c = Get(code);
        config.CurrencyCode = c.Code;
        config.Currency = c.Symbol;
    }

    /// <summary>Strompreis-Einheit im Text an die Währung anpassen („ct/kWh“ → z. B. „p/kWh“); bei Euro unverändert.</summary>
    public static string Cents(this string text, AppConfig config)
    {
        var c = Of(config);
        return c.IsEuro ? text : text.Replace("ct/kWh", c.CentPerKwh);
    }

    /// <summary>Betrag mit nachgestelltem Zeichen, z. B. „12,34 €“ oder „12.34 $“ (einheitlich für alle Währungen).</summary>
    public static string Format(double value, CurrencyInfo c, string format = "0.00", IFormatProvider? culture = null) =>
        value.ToString(format, culture ?? I18n.L.Culture) + " " + c.Symbol;
}
