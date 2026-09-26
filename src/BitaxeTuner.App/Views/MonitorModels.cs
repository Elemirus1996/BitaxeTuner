using System.Windows.Media;

namespace BitaxeTuner.App.Views;

// Anzeigezeilen der Netzwerkansicht (aus BitaxeMonitor/Models.cs). Die übrigen Modelle
// (DeviceConfig, MinerState, Sample, Difficulty) liegen jetzt in BitaxeTuner.Core.

/// <summary>Zeile in der Blockliste.</summary>
public sealed class BlockRow
{
    public string Height { get; set; } = "";
    public string Time { get; set; } = "";
    public string Pool { get; set; } = "";
    public string Details { get; set; } = "";
    public Brush PoolBrush { get; set; } = Brushes.White;
}

/// <summary>Zeile im Pool-Ranking.</summary>
public sealed class PoolRow
{
    public string Rank { get; set; } = "";
    public string Name { get; set; } = "";
    public string Blocks { get; set; } = "";
    public string Share { get; set; } = "";
    public double BarWidth { get; set; }
    public Brush BarBrush { get; set; } = Brushes.Gray;
}
