using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Ein Eintrag im dauerhaften Protokoll (history.db, Tabelle events). Host null = Server/allgemein.</summary>
public sealed record EventEntry(DateTime Time, string? Host, string Category, string Message);

/// <summary>
/// Kategorien des dauerhaften Protokolls (Desktop und Server gleich, Schlüssel fest – der Browser nutzt dieselben).
/// Gespeichert in history.db im Datenordner: Updates, Sicherungen und der Umzug auf den Server nehmen es mit.
/// </summary>
public static class EventCategories
{
    public const string Tuning = "tuning";
    public const string Benchmark = "benchmark";
    public const string Soak = "soak";
    public const string Automation = "automation";
    public const string Fans = "fans";
    public const string Connection = "connection";
    public const string Settings = "settings";
    public const string System = "system";
    public const string Other = "other";

    public static readonly string[] All = [Tuning, Benchmark, Soak, Automation, Fans, Connection, Settings, System, Other];

    /// <summary>Mindestens so lange bleiben Einträge (auch wenn der Verlauf kürzer eingestellt ist).</summary>
    public const int MinKeepDays = 30;

    public static string Label(string category) => category switch
    {
        Tuning => L.T("Frequenz/Spannung"),
        Benchmark => L.T("Benchmark"),
        Soak => L.T("Dauertest"),
        Automation => L.T("Automatik/Watchdog"),
        Fans => L.T("Lüfter"),
        Connection => L.T("Verbindung"),
        Settings => L.T("Einstellungen/Profil"),
        System => L.T("Server/System"),
        _ => L.T("Sonstige"),
    };
}
