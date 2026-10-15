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

    public static readonly string[] Sources = ["none", "plug", "homeassistant"];
}
