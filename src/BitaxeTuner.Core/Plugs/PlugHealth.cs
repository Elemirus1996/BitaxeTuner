namespace BitaxeTuner.Core.Plugs;

/// <summary>Mehrverbrauch an der Steckdose gegenüber AxeOS: jetzt (Prozent/Watt) und in der Vergleichszeit.</summary>
public sealed record OverheadChange(double RecentPercent, double BasePercent, double RecentW, double BaseW);

/// <summary>
/// Frühwarnung für Netzteil und Verkabelung: steigt der Anteil über AxeOS deutlich gegenüber der Vergleichszeit,
/// altert vermutlich das Netzteil (oder ein Zusatzverbraucher läuft ungewollt mit).
/// </summary>
public static class PlugHealth
{
    /// <summary>Ab so vielen Prozentpunkten Anstieg …</summary>
    public const double MinPercentPoints = 5;

    /// <summary>… und mindestens so vielen Watt mehr gibt es eine Meldung.</summary>
    public const double MinWatt = 3;

    /// <summary>Null = unauffällig oder zu wenig Last für eine Aussage.</summary>
    public static OverheadChange? Check(double plugRecentW, double axeRecentW, double plugBaseW, double axeBaseW)
    {
        if (axeRecentW < 5 || axeBaseW < 5) return null;
        var recentW = plugRecentW - axeRecentW;
        var baseW = plugBaseW - axeBaseW;
        var recent = recentW / axeRecentW * 100;
        var basePct = baseW / axeBaseW * 100;
        return recent - basePct >= MinPercentPoints && recentW - baseW >= MinWatt
            ? new OverheadChange(recent, basePct, recentW, baseW)
            : null;
    }
}
