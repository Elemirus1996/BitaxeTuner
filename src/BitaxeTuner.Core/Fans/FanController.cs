using System.Globalization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Fans;

/// <summary>Was die Regelung über einen Miner wissen muss (aus der normalen Abfrage).</summary>
public sealed record MinerTemps(string Host, string Name, bool Online, double? VrTemp, double? AsicTemp, DateTime? LastOk);

/// <summary>Sollwert eines Kanals mit Begründung für die Oberfläche.</summary>
public sealed record FanTarget(int Channel, int Percent, string Reason, bool SafetyOverride = false);

/// <summary>Vorübergehende Vorgabe per Taste oder Browser; gilt bis zum nächsten Neustart.</summary>
public enum FanOverride
{
    /// <summary>Jeder Kanal nach seiner Einstellung (Automatik bzw. manuell).</summary>
    None,
    /// <summary>Alle Zusatzlüfter aus – Sicherheitsregeln bleiben aktiv.</summary>
    Off,
    /// <summary>Alle Zusatzlüfter auf 100 %.</summary>
    Full,
}

/// <summary>
/// Berechnet die Sollwerte der Zusatzlüfter. Reine Logik ohne Hardware:
/// <list type="bullet">
/// <item>VR-Lüfter je Miner: manuell oder Kurve nach VR-Temperatur (ersatzweise ASIC), mit Hysterese.</item>
/// <item>Gehäusegruppe: manuell oder Kurve nach der höchsten Temperatur der zugeordneten Miner, optional Nachtbetrieb.</item>
/// <item>Immer: Miner offline oder Daten älter als <see cref="StaleAfter"/> → 100 % (Gehäuse: <see cref="CaseFanSettings.UnknownPercent"/>).</item>
/// <item>Nicht belegte Kanäle bekommen 100 % (entspricht dem Ausfallschutz der Schaltung).</item>
/// </list>
/// </summary>
public sealed class FanController
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static CultureInfo De => L.Culture; // Sprache kann sich zur Laufzeit ändern (Server-Einstellung)
    private readonly Dictionary<int, int> _last = new();

    /// <summary>Vorgabe per Taste („Aus“ / „100 %“); <see cref="FanOverride.None"/> = Einstellungen.</summary>
    public FanOverride Override { get; set; }

    /// <summary>Höchster Wert der Gehäusefühler (°C), null ohne Sensor.</summary>
    public double? CaseTemperature { get; set; }

    public IReadOnlyList<FanTarget> Compute(FanSettings settings, IReadOnlyList<MinerTemps> miners, DateTime now)
    {
        var result = new List<FanTarget>(FanSettings.ChannelCount);
        FanTarget? caseTarget = null;
        for (var ch = 1; ch <= FanSettings.ChannelCount; ch++)
        {
            var c = settings.Channel(ch);
            FanTarget t;
            switch (c.Role)
            {
                case "miner":
                    t = MinerChannel(c, miners, now);
                    break;
                case "case":
                    caseTarget ??= CaseGroup(settings.Case, miners, now, ch);
                    t = caseTarget with { Channel = ch };
                    break;
                default:
                    _last.Remove(ch);
                    t = new FanTarget(ch, 100, L.T("nicht belegt"));
                    break;
            }
            result.Add(t);
        }
        return result;
    }

    private FanTarget MinerChannel(FanChannelSettings c, IReadOnlyList<MinerTemps> miners, DateTime now)
    {
        var m = miners.FirstOrDefault(x => string.Equals(x.Host.Trim(), c.MinerHost?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is null) return Remember(c.Channel, 100, L.T("kein Miner zugeordnet → 100 %"));
        if (!m.Online) return Remember(c.Channel, 100, L.T("{0} offline → 100 %", m.Name));
        if (m.LastOk is not { } ok || now - ok > StaleAfter) return Remember(c.Channel, 100, L.T("keine aktuellen Daten → 100 %"));
        if (Override == FanOverride.Full) return Remember(c.Channel, 100, L.T("100 % (Taste)"));
        if (Override == FanOverride.Off)
        {
            var t = m.VrTemp is { } v and > 0 ? v : m.AsicTemp ?? 0;
            return t >= c.Curve.FullTemp
                ? Remember(c.Channel, 100, L.T("Sicherheit: {0} °C trotz „Aus“ → 100 %", t.ToString("0.0", De))) with { SafetyOverride = true }
                : Remember(c.Channel, 0, L.T("aus (Taste)"));
        }
        if (c.Mode == "manual") return Remember(c.Channel, Clamp(c.ManualPercent), L.T("manuell {0} %", Clamp(c.ManualPercent)));

        var (temp, label) = m.VrTemp is { } vr and > 0 ? (vr, L.T("VR")) : m.AsicTemp is { } a and > 0 ? (a, L.T("ASIC")) : (double.NaN, "");
        if (double.IsNaN(temp)) return Remember(c.Channel, 100, L.T("keine Temperatur → 100 %"));
        var pct = WithHysteresis(c.Channel, c.Curve, temp);
        return Remember(c.Channel, pct, L.T("Automatik: {0} {1} °C → {2} %", label, temp.ToString("0.0", De), pct));
    }

    private FanTarget CaseGroup(CaseFanSettings s, IReadOnlyList<MinerTemps> miners, DateTime now, int channel)
    {
        const int groupKey = 0; // Hysterese-Zustand der Gruppe
        if (Override == FanOverride.Full) return new FanTarget(channel, 100, L.T("100 % (Taste)"));
        if (s.Mode == "manual" && Override == FanOverride.None)
            return new FanTarget(channel, Clamp(s.ManualPercent), L.T("Gehäuse manuell {0} %", Clamp(s.ManualPercent)));

        if (s.Sensor == "case")
        {
            if (CaseTemperature is not { } ct)
                return new FanTarget(channel, Clamp(s.UnknownPercent), L.T("kein Gehäusefühler → {0} %", Clamp(s.UnknownPercent)));
            if (Override == FanOverride.Off)
                return ct >= s.Curve.FullTemp
                    ? new FanTarget(channel, 100, L.T("Sicherheit: Gehäuse {0} °C trotz „Aus“ → 100 %", ct.ToString("0.0", De)), true)
                    : new FanTarget(channel, 0, L.T("aus (Taste)"));
            var cp = WithHysteresis(groupKey, s.Curve, ct);
            var why = L.T("Automatik: Gehäuse {0} °C → {1} %", ct.ToString("0.0", De), cp);
            if (s.NightEnabled && IsNight(s, now) && ct < s.Curve.FullTemp && cp > s.NightMaxPercent)
            {
                cp = Clamp(s.NightMaxPercent);
                why += L.T(", Nachtbetrieb max. {0} %", cp);
            }
            return new FanTarget(channel, cp, why);
        }

        var selected = s.Miners.Count == 0
            ? miners
            : miners.Where(m => s.Miners.Any(h => string.Equals(h.Trim(), m.Host.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        if (selected.Count == 0) return new FanTarget(channel, Clamp(s.UnknownPercent), L.T("keine Miner zugeordnet → {0} %", Clamp(s.UnknownPercent)));

        var unknown = selected.Where(m => !m.Online || m.LastOk is not { } ok || now - ok > StaleAfter).ToList();
        var temps = selected.Except(unknown)
            .Select(m => s.Sensor == "asic" ? m.AsicTemp ?? m.VrTemp : m.VrTemp ?? m.AsicTemp)
            .Where(t => t is > 0).Select(t => t!.Value).ToList();

        int pct;
        string reason;
        if (temps.Count == 0)
        {
            pct = Clamp(s.UnknownPercent);
            reason = L.T("keine aktuellen Temperaturen → {0} %", pct);
        }
        else if (Override == FanOverride.Off)
        {
            var max = temps.Max();
            var safety = max >= s.Curve.FullTemp || unknown.Count > 0;
            return safety
                ? new FanTarget(channel, 100, L.T("Sicherheit: {0} trotz „Aus“ → 100 %", (unknown.Count > 0 ? L.T("Miner ohne Daten") : $"{max.ToString("0.0", De)} °C")), true)
                : new FanTarget(channel, 0, L.T("aus (Taste)"));
        }
        else
        {
            var max = temps.Max();
            pct = WithHysteresis(groupKey, s.Curve, max);
            reason = L.T("Automatik: höchste {0}-Temperatur {1} °C → {2} %", (s.Sensor == "asic" ? L.T("ASIC") : L.T("VR")), max.ToString("0.0", De), pct);
            if (s.NightEnabled && IsNight(s, now) && max < s.Curve.FullTemp && pct > s.NightMaxPercent)
            {
                pct = Clamp(s.NightMaxPercent);
                reason += L.T(", Nachtbetrieb max. {0} %", pct);
            }
            if (unknown.Count > 0 && pct < s.UnknownPercent)
            {
                pct = Clamp(s.UnknownPercent);
                reason = L.T("{0} ohne aktuelle Daten → {1} %", string.Join(", ", unknown.Select(u => u.Name)), pct);
            }
        }
        return new FanTarget(channel, pct, reason);
    }

    /// <summary>Kurve mit Hysterese: hochregeln sofort, herunterregeln erst, wenn die Temperatur um die Hysterese gesunken ist.</summary>
    private int WithHysteresis(int key, FanCurve curve, double temp)
    {
        var up = Evaluate(curve, temp);
        var down = Evaluate(curve, temp + Math.Max(0, curve.Hysteresis));
        if (!_last.TryGetValue(key, out var last)) last = up;
        var next = up > last ? up : down < last ? down : last;
        _last[key] = next;
        return next;
    }

    public static int Evaluate(FanCurve c, double temp)
    {
        if (temp < c.StartTemp) return Clamp(c.MinPercent);
        if (temp >= c.FullTemp || c.FullTemp <= c.StartTemp) return 100;
        var share = (temp - c.StartTemp) / (c.FullTemp - c.StartTemp);
        return Clamp((int)Math.Round(c.StartPercent + share * (100 - c.StartPercent)));
    }

    public static bool IsNight(CaseFanSettings s, DateTime now)
    {
        var h = now.Hour;
        return s.NightFromHour <= s.NightToHour
            ? h >= s.NightFromHour && h < s.NightToHour
            : h >= s.NightFromHour || h < s.NightToHour;
    }

    private FanTarget Remember(int channel, int pct, string reason)
    {
        _last[channel] = pct;
        return new FanTarget(channel, pct, reason);
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 100);
}
