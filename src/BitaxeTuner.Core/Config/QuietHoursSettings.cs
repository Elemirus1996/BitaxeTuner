namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.21 Ruhezeit (z. B. 22–7 Uhr): Push-Meldungen mit normaler Priorität werden gesammelt und am Ende als eine
/// Zusammenfassung je Push-Ziel gesendet; wichtige Meldungen (hoch/dringend, Funde, Berichte) kommen sofort. Optional
/// Obergrenzen für die Zusatzlüfter (Pico) und den Server-Lüfter – nie gegen die Sicherheit: bei Volllast-Temperatur
/// gilt weiter volle Kühlung. Gespeichert in config.json („Quiet“, additiv), standardmäßig aus.
/// </summary>
public sealed class QuietHoursSettings
{
    public bool Enabled { get; set; }
    /// <summary>Beginn (volle Stunde, 0–23).</summary>
    public int FromHour { get; set; } = 22;
    /// <summary>Ende (volle Stunde, 0–23, ausschließlich).</summary>
    public int ToHour { get; set; } = 7;
    /// <summary>Push-Meldungen mit normaler Priorität sammeln.</summary>
    public bool CollectPush { get; set; } = true;
    /// <summary>Höchstwert der Zusatzlüfter in Prozent; null = keine Grenze.</summary>
    public int? CaseFanMaxPercent { get; set; }
    /// <summary>Höchste Stufe des Server-Lüfters (Raspberry Pi 5); null = keine Grenze. Ab 75 °C CPU gilt sie nicht.</summary>
    public int? ServerFanMaxLevel { get; set; }

    /// <summary>CPU-Temperatur, ab der die Grenze des Server-Lüfters nicht mehr gilt.</summary>
    public const double ServerFanSafetyCpu = 75;

    public bool IsQuiet(DateTime local)
    {
        if (!Enabled || FromHour == ToHour) return false;
        var h = local.Hour;
        return FromHour < ToHour ? h >= FromHour && h < ToHour : h >= FromHour || h < ToHour;
    }

    public void Normalize()
    {
        FromHour = Math.Clamp(FromHour, 0, 23);
        ToHour = Math.Clamp(ToHour, 0, 23);
        if (CaseFanMaxPercent is { } p) CaseFanMaxPercent = Math.Clamp(p, 20, 100);
        if (ServerFanMaxLevel is { } l) ServerFanMaxLevel = Math.Clamp(l, 1, 4);
    }
}
