namespace BitaxeTuner.Core.Config;

/// <summary>
/// Home Assistant / MQTT: Messwerte veröffentlichen (mit automatischer Geräteerkennung in Home Assistant).
/// Gespeichert in config.json („Mqtt“); das Passwort liegt getrennt in secrets.json.
/// Frequenz und Spannung lassen sich über MQTT nie ändern.
/// </summary>
public sealed class MqttSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string User { get; set; } = "";
    public bool Tls { get; set; }

    /// <summary>Oberstes Topic, z. B. bitaxetuner/…</summary>
    public string BaseTopic { get; set; } = "bitaxetuner";

    /// <summary>Home-Assistant-Geräteerkennung.</summary>
    public bool Discovery { get; set; } = true;
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Werte alle … Sekunden senden.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Lüfter-Modus (Automatik / 100 % / Aus) aus Home Assistant schalten lassen.</summary>
    public bool AllowFanControl { get; set; }
}
