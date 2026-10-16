namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.20: Lüfter des Servers (Raspberry Pi 5, Lüfteranschluss am Board). Gespeichert in config.json („PiFan“, additiv).
/// <list type="bullet">
/// <item>„auto“ (Standard): Die Pi-Firmware regelt wie ab Werk – BitaxeTuner zeigt nur Drehzahl und CPU-Temperatur.</item>
/// <item>„curve“: eine von drei Kurven nach CPU-Temperatur (<see cref="Preset"/>: quiet, balanced, cool).</item>
/// <item>„miners“: nach der wärmsten Temperatur der gewählten Miner (<see cref="Curve"/>), mindestens aber so viel, wie die
/// CPU nach der Kurve „balanced“ braucht.</item>
/// </list>
/// Gesteuert wird in Stufen (0 = aus … 4 = voll) über den Root-Dienst bitaxetuner-pifan; meldet sich BitaxeTuner 3 Minuten
/// nicht, gibt ein Wächter die Regelung an die Firmware zurück.
/// </summary>
public sealed class PiFanSettings
{
    public static readonly string[] Modes = ["auto", "curve", "miners"];
    public static readonly string[] Presets = ["quiet", "balanced", "cool"];

    public string Mode { get; set; } = "auto";
    public string Preset { get; set; } = "balanced";

    /// <summary>„miners“: "vr" oder "asic".</summary>
    public string Sensor { get; set; } = "vr";

    /// <summary>„miners“: berücksichtigte Miner (Hosts); leer = alle.</summary>
    public List<string> Miners { get; set; } = [];

    /// <summary>„miners“: Prozent nach Miner-Temperatur (wie beim Gehäuselüfter), wird auf Stufen 0–4 abgebildet.</summary>
    public FanCurve Curve { get; set; } = new() { StartTemp = 50, StartPercent = 30, FullTemp = 70, MinPercent = 0 };
}
