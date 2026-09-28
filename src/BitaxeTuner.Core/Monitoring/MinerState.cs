using System.Globalization;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Core.Monitoring;

public readonly record struct Sample(DateTime Time, double HashRateGh, double Temp, double Power);

/// <summary>Laufzeitzustand eines Miners (aus BitaxeMonitor, ergänzt um die normalisierte Tuning-Sicht).</summary>
public sealed class MinerState
{
    public MinerState(DeviceConfig config) => Config = config;

    /// <summary>Gerätekonfiguration; wird nach "Einstellungen speichern" durch die neue Kopie ersetzt.</summary>
    public DeviceConfig Config { get; set; }

    /// <summary>Rohdaten aus /api/system/info für die Überwachungsansicht.</summary>
    public SystemInfo? Info { get; set; }

    /// <summary>Dieselbe Antwort normalisiert für Tuning und Benchmark.</summary>
    public MinerInfo? Normalized { get; set; }

    public string? Error { get; set; }
    public DateTime? LastOk { get; set; }
    public List<Sample> History { get; } = new();

    public bool Online => Info is not null && Error is null;

    /// <summary>Adresse aus Override oder aus dem Stratum-User (Format &lt;adresse&gt;.&lt;worker&gt;).</summary>
    public string? WalletAddress
    {
        get
        {
            var manual = Config.WalletAddress?.Trim();
            if (!string.IsNullOrEmpty(manual)) return manual;

            var user = Info?.stratumUser;
            if (string.IsNullOrWhiteSpace(user)) return null;
            var candidate = user.Split('.')[0].Trim();
            return candidate.Length >= 26 ? candidate : null;
        }
    }
}

/// <summary>Difficulty-Strings wie "115M" oder "2.4G" vergleichbar machen.</summary>
public static class Difficulty
{
    public static double Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();

        var mult = 1.0;
        var last = char.ToUpperInvariant(s[^1]);
        if (!char.IsDigit(last))
        {
            mult = last switch
            {
                'K' => 1e3,
                'M' => 1e6,
                'G' => 1e9,
                'T' => 1e12,
                'P' => 1e15,
                'E' => 1e18,
                _ => 1.0
            };
            s = s[..^1];
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * mult : 0;
    }

    public static string Format(double d)
    {
        var ci = BitaxeTuner.Core.I18n.L.Culture;
        return d switch
        {
            >= 1e15 => (d / 1e15).ToString("0.00", ci) + "P",
            >= 1e12 => (d / 1e12).ToString("0.00", ci) + "T",
            >= 1e9 => (d / 1e9).ToString("0.00", ci) + "G",
            >= 1e6 => (d / 1e6).ToString("0.00", ci) + "M",
            >= 1e3 => (d / 1e3).ToString("0.00", ci) + "k",
            _ => d.ToString("0", ci)
        };
    }
}
