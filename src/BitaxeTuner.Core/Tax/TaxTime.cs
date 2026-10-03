namespace BitaxeTuner.Core.Tax;

/// <summary>
/// Zeitzone für Haltefrist, Jahresgrenzen und Datumsspalten des Steuer-Bereichs (Audit F5): fest „Europe/Berlin“
/// (einstellbar), unabhängig davon, ob der Rechner – z. B. ein Linux-Server – auf UTC läuft.
/// </summary>
public static class TaxTime
{
    public const string DefaultZone = "Europe/Berlin";
    private static TimeZoneInfo _zone = Resolve(DefaultZone);

    public static TimeZoneInfo Zone => _zone;

    /// <summary>Zeitzone setzen (IANA- oder Windows-Kennung); unbekannt → Europe/Berlin, notfalls Rechnerzeit.</summary>
    public static void Configure(string? id) => _zone = Resolve(string.IsNullOrWhiteSpace(id) ? DefaultZone : id.Trim());

    public static bool IsKnown(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    private static TimeZoneInfo Resolve(string id)
    {
        foreach (var candidate in new[] { id, DefaultZone, "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>UTC → Steuer-Ortszeit.</summary>
    public static DateTime ToTax(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    /// <summary>Steuer-Ortszeit → UTC.</summary>
    public static DateTime FromTax(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
}
