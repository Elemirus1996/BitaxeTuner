namespace BitaxeTuner.Core.Config;

/// <summary>
/// Smart Plugs mit Leistungsmessung (Shelly) für echte Verbrauchswerte an der Steckdose.
/// Nur lesen – BitaxeTuner schaltet die Plugs nie. Gespeichert in config.json („Plugs“), Passwörter in secrets.json.
/// </summary>
public sealed class SmartPlugSettings
{
    public List<SmartPlugConfig> Items { get; set; } = [];

    /// <summary>Kosten und Tagesbericht mit Steckdosenwerten statt AxeOS-Leistung rechnen, soweit Plugs messen.</summary>
    public bool UseForCosts { get; set; } = true;

    /// <summary>Abfrage-Abstand in Sekunden (5–300).</summary>
    public int IntervalSeconds { get; set; } = 10;
}

public sealed class SmartPlugConfig
{
    /// <summary>Feste Kennung (Verlauf in history.db, Passwort in secrets.json), bleibt bei Umbenennen gleich.</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Smart Plug";

    /// <summary>IP oder Hostname im Heimnetz (optional mit Port); "sim" = simulierter Plug.</summary>
    public string Host { get; set; } = "";

    /// <summary>Messkanal (Mehrfach-Geräte wie Shelly Plus 2PM); 0 bei Steckdosen.</summary>
    public int Channel { get; set; }

    /// <summary>Benutzer bei geschütztem Gerät (Gen2+: immer „admin“). Passwort in secrets.json.</summary>
    public string User { get; set; } = "admin";

    /// <summary>"miners" (speist die Miner in <see cref="Miners"/>), "other" (Zusatzlüfter, Pi …) oder "total" (Gesamtmessung).</summary>
    public string Role { get; set; } = "miners";

    /// <summary>Hosts der Miner an diesem Plug (Rolle "miners").</summary>
    public List<string> Miners { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public static readonly string[] Roles = ["miners", "other", "total"];

    public static string SecretKey(string id) => $"plug.{id}.password";
}
