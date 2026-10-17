namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.17: Woher die aktuelle PV-Leistung kommt (für die Automatik-Art „PV-Leistung“). Additiv in config.json („Solar“).
/// <list type="bullet">
/// <item>"plug": eine eingetragene Messsteckdose bzw. ein Messgerät (Shelly inkl. Shelly EM/Pro 3EM, Tasmota, FRITZ!DECT) –
/// am besten mit der Rolle „PV-Erzeugung“, dann zählt sie nicht zu den Stromkosten.</item>
/// <item>"homeassistant": ein Sensor aus Home Assistant (W oder kW) – darüber auch Anker Solix (HA-Integration),
/// ohne dass BitaxeTuner Anker-Zugangsdaten braucht.</item>
/// </list>
/// Das Home-Assistant-Token (Profil → Sicherheit → langlebiges Zugriffstoken) liegt nur in secrets.json
/// (<see cref="Host.MinerHub.SolarTokenKey"/>), nie in config.json.
/// </summary>
public sealed class SolarSettings
{
    /// <summary>"none", "plug" oder "homeassistant".</summary>
    public string Source { get; set; } = "none";

    /// <summary>Kennung der Messsteckdose (<see cref="SmartPlugConfig.Id"/>) bei Quelle „plug“.</summary>
    public string PlugId { get; set; } = "";

    /// <summary>Adresse von Home Assistant, z. B. http://homeassistant.local:8123</summary>
    public string HaUrl { get; set; } = "";

    /// <summary>Sensor mit der PV-Leistung, z. B. sensor.pv_leistung (W oder kW).</summary>
    public string HaEntity { get; set; } = "";

    /// <summary>0.9.21: PV-Prognose (Open-Meteo) nutzen. Additiv, standardmäßig aus.</summary>
    public bool ForecastEnabled { get; set; }

    /// <summary>Standort der Anlage; gesendet wird nur auf 2 Nachkommastellen gerundet (etwa 1 km).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Anlagengröße in kWp.</summary>
    public double PeakKw { get; set; }

    /// <summary>Neigung der Module in Grad (0 = waagerecht).</summary>
    public int TiltDeg { get; set; } = 30;

    /// <summary>Ausrichtung in Grad: 0 = Süden, −90 = Osten, 90 = Westen.</summary>
    public int AzimuthDeg { get; set; }

    /// <summary>Kurze Wolken überbrücken: Messwert niedrig, Prognose hoch → höchstens 20 min nicht herunterschalten.</summary>
    public bool SmoothClouds { get; set; } = true;

    public static readonly string[] Sources = ["none", "plug", "homeassistant"];
}
