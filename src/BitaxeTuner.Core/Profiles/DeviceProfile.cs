namespace BitaxeTuner.Core.Profiles;

/// <summary>
/// Beschreibt ein Miner-Modell mit sinnvollen Standard- und Sicherheitsgrenzen für den Benchmark.
/// Alle Werte lassen sich über <c>profiles.json</c> im Datenordner überschreiben oder ergänzen.
/// </summary>
public sealed class DeviceProfile
{
    public string Id { get; set; } = "generic";
    public string Name { get; set; } = "Generisch";
    public string Family { get; set; } = "Bitaxe";
    public string AsicModel { get; set; } = "";
    public int AsicCount { get; set; } = 1;
    /// <summary>Small-Cores pro ASIC (für die theoretische Hashrate: Frequenz × Cores × Chips / 1000 GH/s).</summary>
    public int SmallCoresPerAsic { get; set; }

    public int DefaultFrequencyMhz { get; set; } = 500;
    public int DefaultVoltageMv { get; set; } = 1150;
    public int MinFrequencyMhz { get; set; } = 400;
    public int MaxFrequencyMhz { get; set; } = 800;
    public int MinVoltageMv { get; set; } = 1000;
    public int MaxVoltageMv { get; set; } = 1300;

    public double MaxChipTempC { get; set; } = 66;
    public double MaxVrTempC { get; set; } = 86;
    public double MaxPowerW { get; set; } = 25;
    public double? MinInputVoltageMv { get; set; }
    public double? MaxInputVoltageMv { get; set; }

    /// <summary>Teilstrings, die in <c>deviceModel</c> vorkommen (Groß-/Kleinschreibung egal).</summary>
    public List<string> DeviceModelMatches { get; set; } = [];
    /// <summary>Board-Versionen bzw. deren Präfixe (z. B. "60" für 600–602).</summary>
    public List<string> BoardVersions { get; set; } = [];
    public string? Notes { get; set; }

    public DeviceProfile Clone()
    {
        var copy = (DeviceProfile)MemberwiseClone();
        copy.DeviceModelMatches = [.. DeviceModelMatches];
        copy.BoardVersions = [.. BoardVersions];
        return copy;
    }

    public override string ToString() => Name;
}
