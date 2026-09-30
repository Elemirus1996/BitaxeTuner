using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Art einer Miner-Logzeile für die Schnellfilter (Desktop und Browser gleich).</summary>
public enum LogCategory
{
    Shares,
    Pool,
    Asic,
    Thermal,
    System,
    Other,
}

/// <summary>
/// Ordnet Logzeilen über Modul (Tag) und Textmuster einer <see cref="LogCategory"/> zu – ESP-Miner/AxeOS und NerdQAxe
/// nutzen dieselben ESP-IDF-Module (asic_result, stratum_task, fan_controller, power_management, wifi …).
/// Reihenfolge der Prüfung zählt: Share-Ergebnisse kommen auch aus stratum_task, gehören aber zu „Shares“.
/// </summary>
public static class LogCategories
{
    public static IReadOnlyList<LogCategory> All { get; } = Enum.GetValues<LogCategory>();

    public static LogCategory Of(string tag, string message)
    {
        var t = tag.ToLowerInvariant();
        var m = message.ToLowerInvariant();
        if (t.Contains("asic_result") || m.Contains("accepted") || m.Contains("rejected") || (m.Contains("nonce") && m.Contains("diff")))
            return LogCategory.Shares;
        if (t.StartsWith("stratum") || m.Contains("mining.") || m.Contains("stratum") || m.Contains("set_difficulty"))
            return LogCategory.Pool;
        if (Any(t, "fan", "power", "thermal", "temp", "vcore", "tps", "emc", "ds44", "ina2", "adc", "pid", "vr_"))
            return LogCategory.Thermal;
        if (Any(t, "bm13", "asic", "create_jobs", "hashrate", "job", "serial", "mining"))
            return LogCategory.Asic;
        if (Any(t, "wifi", "http", "system", "main", "esp", "nvs", "ota", "self_test", "connect", "wpa", "net", "dhcp",
                "websocket", "lwip", "theme", "screen", "display", "lvgl", "i2c", "button", "input", "boot"))
            return LogCategory.System;
        return LogCategory.Other;
    }

    public static string Label(LogCategory c) => c switch
    {
        LogCategory.Shares => L.T("Shares"),
        LogCategory.Pool => L.T("Pool/Stratum"),
        LogCategory.Asic => L.T("ASIC/Jobs"),
        LogCategory.Thermal => L.T("Temperatur/Lüfter/Strom"),
        LogCategory.System => L.T("System/WLAN"),
        _ => L.T("Sonstige"),
    };

    private static bool Any(string s, params string[] parts) => parts.Any(s.Contains);
}
