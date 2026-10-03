using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Benchmark;

public enum FanModeDuringBenchmark
{
    /// <summary>Lüftereinstellung des Geräts nicht verändern.</summary>
    KeepCurrent,
    /// <summary>Lüfter während des Tests auf 100 % – misst die Grenze des Chips, nicht der Kühlung.</summary>
    Full,
}

public enum RestoreMode
{
    /// <summary>Nach dem Lauf die ursprünglichen Einstellungen wiederherstellen.</summary>
    Original,
    /// <summary>Nach dem Lauf die beste gefundene Einstellung (gemäß Ranking) anwenden.</summary>
    Best,
}

/// <summary>Alle einstellbaren Parameter eines Benchmark-Laufs. Bewusst veränderbar, damit die UI direkt daran binden kann.</summary>
public sealed class BenchmarkSettings
{
    public int StartFrequencyMhz { get; set; } = 500;
    public int MaxFrequencyMhz { get; set; } = 800;
    public int FrequencyStepMhz { get; set; } = 25;

    public int MinVoltageMv { get; set; } = 1000;
    public int StartVoltageMv { get; set; } = 1150;
    public int MaxVoltageMv { get; set; } = 1300;
    public int VoltageStepMv { get; set; } = 20;

    /// <summary>Wartezeit nach dem Anwenden, bevor gemessen wird.</summary>
    public int WarmupSeconds { get; set; } = 90;
    /// <summary>Messdauer pro Kombination.</summary>
    public int MeasureSeconds { get; set; } = 600;
    public int SampleIntervalSeconds { get; set; } = 15;
    public int MinSamples { get; set; } = 7;

    public double MaxChipTempC { get; set; } = 66;
    public double MaxVrTempC { get; set; } = 86;
    public double MaxPowerW { get; set; } = 30;
    public double? MinInputVoltageMv { get; set; }
    public double? MaxInputVoltageMv { get; set; }

    /// <summary>Gemessene Hashrate muss mindestens diesen Anteil der theoretischen erreichen (0.94 = 94 %).</summary>
    public double StabilityThreshold { get; set; } = 0.94;
    /// <summary>Maximal erlaubte Fehlerrate in % (nur wenn die Firmware <c>errorPercentage</c> liefert).</summary>
    public double MaxErrorPercent { get; set; } = 3;

    /// <summary>Nach jeder Änderung neu starten – liefert saubere Messwerte (AxeOS mittelt die Hashrate über die Laufzeit).</summary>
    public bool RestartAfterApply { get; set; } = true;
    public FanModeDuringBenchmark FanMode { get; set; } = FanModeDuringBenchmark.KeepCurrent;
    public RestoreMode RestoreMode { get; set; } = RestoreMode.Best;
    public RankingMode RestoreRanking { get; set; } = RankingMode.Balanced;
    /// <summary>Gewichtung Hashrate vs. Effizienz für <see cref="RankingMode.Balanced"/> (1 = nur Hashrate).</summary>
    public double BalancedHashrateWeight { get; set; } = 0.5;

    /// <summary>Wenn eine Frequenz stabil läuft: zuerst versuchen, ob sie auch mit weniger Spannung stabil ist.</summary>
    public bool TryLowerVoltage { get; set; }

    public static BenchmarkSettings FromProfile(DeviceProfile p) => new()
    {
        StartFrequencyMhz = p.DefaultFrequencyMhz,
        MaxFrequencyMhz = p.MaxFrequencyMhz,
        MinVoltageMv = p.MinVoltageMv,
        StartVoltageMv = p.DefaultVoltageMv,
        MaxVoltageMv = p.MaxVoltageMv,
        MaxChipTempC = p.MaxChipTempC,
        MaxVrTempC = p.MaxVrTempC,
        MaxPowerW = p.MaxPowerW,
        MinInputVoltageMv = p.MinInputVoltageMv,
        MaxInputVoltageMv = p.MaxInputVoltageMv,
    };

    public BenchmarkSettings Clone() => (BenchmarkSettings)MemberwiseClone();

    /// <summary>Liefert eine Liste von Problemen (leer = gültig).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (StartFrequencyMhz <= 0 || MaxFrequencyMhz < StartFrequencyMhz) errors.Add(L.T("Frequenzbereich ungültig."));
        if (StartVoltageMv <= 0 || MaxVoltageMv < StartVoltageMv || MinVoltageMv > StartVoltageMv) errors.Add(L.T("Spannungsbereich ungültig."));
        if (FrequencyStepMhz <= 0 || VoltageStepMv <= 0) errors.Add(L.T("Schrittweiten müssen größer als 0 sein."));
        if (SampleIntervalSeconds < 2) errors.Add(L.T("Messintervall muss mindestens 2 s betragen."));
        if (MeasureSeconds < SampleIntervalSeconds * Math.Max(1, MinSamples)) errors.Add(L.T("Messdauer ist zu kurz für die Mindestanzahl an Messwerten."));
        if (MaxVoltageMv > 1500) errors.Add(L.T("Maximale Kernspannung über 1500 mV ist nicht erlaubt."));
        if (MaxChipTempC > 80) errors.Add(L.T("Maximale Chiptemperatur über 80 °C ist nicht erlaubt."));
        if (MaxVrTempC > 105) errors.Add(L.T("Maximale VR-Temperatur über 105 °C ist nicht erlaubt."));
        if (MaxPowerW is <= 0 or > 400) errors.Add(L.T("Maximale Leistung muss zwischen 1 und 400 W liegen."));
        if (StabilityThreshold is <= 0 or > 1.2) errors.Add(L.T("Stabilitätsschwelle muss zwischen 0 und 1,2 liegen."));
        return errors;
    }

    /// <summary>Grobe Anzahl an Schritten (für die Fortschrittsanzeige).</summary>
    public int EstimatedSteps =>
        Math.Max(1, (MaxFrequencyMhz - StartFrequencyMhz) / Math.Max(1, FrequencyStepMhz) + 1)
        + Math.Max(0, (MaxVoltageMv - StartVoltageMv) / Math.Max(1, VoltageStepMv));

    public TimeSpan EstimatedStepDuration => TimeSpan.FromSeconds(WarmupSeconds + MeasureSeconds + (RestartAfterApply ? 20 : 2));
}
