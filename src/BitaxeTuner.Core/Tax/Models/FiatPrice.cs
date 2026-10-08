namespace BitaxeTuner.Core.Tax.Models;

/// <summary>0.9.11: Kurs eines Zuflusses in einer anderen Währung als Euro, mit Herkunft wie beim Euro-Kurs.</summary>
public sealed class FiatPrice
{
    /// <summary>Kurs je Coin.</summary>
    public decimal Price { get; set; }

    /// <summary>Zeitpunkt des verwendeten Kurspunkts (UTC), null bei manueller Eingabe.</summary>
    public DateTime? AtUtc { get; set; }

    public string Source { get; set; } = string.Empty;

    public static bool IsManualSource(string source) =>
        source.StartsWith("manuell", StringComparison.OrdinalIgnoreCase) || source.StartsWith("manual", StringComparison.OrdinalIgnoreCase);
}
