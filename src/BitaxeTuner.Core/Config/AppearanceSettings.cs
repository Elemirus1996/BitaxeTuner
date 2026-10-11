namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.14: Darstellung der Oberfläche als Standard für alle Browser und die Desktop-App (jeder Browser kann eigene Werte
/// setzen, die Vorrang haben). Additiv; ohne Eintrag gilt der bisherige Look mit dezentem Verlauf.
/// </summary>
public sealed class AppearanceSettings
{
    public static readonly string[] Backgrounds = ["solid", "gradient", "aurora", "hex", "circuit", "dots"];

    /// <summary>Hintergrund: solid, gradient, aurora (bewegter Verlauf), hex, circuit, dots. Eigene Bilder gibt es nur je Browser.</summary>
    public string Background { get; set; } = "gradient";

    /// <summary>Abdunkeln des Hintergrunds in Prozent (0–80).</summary>
    public int Veil { get; set; }

    public bool Animations { get; set; } = true;

    /// <summary>Live-Zahlen gleiten zum neuen Wert.</summary>
    public bool SmoothNumbers { get; set; } = true;

    /// <summary>Karten heben sich beim Zeigen.</summary>
    public bool HoverLift { get; set; } = true;

    /// <summary>Konfetti bei einem Blockfund.</summary>
    public bool BlockParty { get; set; } = true;

    public void Normalize()
    {
        if (!Backgrounds.Contains(Background)) Background = "gradient";
        Veil = Math.Clamp(Veil, 0, 80);
    }
}
