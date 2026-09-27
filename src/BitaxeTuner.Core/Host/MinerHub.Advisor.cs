using System.Globalization;
using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Host;

/// <summary>Effizienz-Ratgeber: Vorschläge je Miner; angewendet wird nur über die normale Bestätigung (alt → neu).</summary>
public sealed partial class MinerHub
{
    public IReadOnlyList<AdvisorResult> Advise(AdvisorGoal goal, DateTime now)
    {
        var sessions = Results.LoadAll();
        var list = new List<AdvisorResult>();
        foreach (var d in Devices.Where(d => !d.IsSimulated || Devices.All(x => x.IsSimulated)))
        {
            var hostname = d.State.Normalized?.Hostname;
            var results = sessions
                .Where(s => string.Equals(s.DeviceAddress, d.Host, StringComparison.OrdinalIgnoreCase)
                            || (hostname is { Length: > 0 } && string.Equals(s.Hostname, hostname, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(s => s.Results);
            var soaks = History?.QuerySoakResults(d.Host) ?? [];
            var i = d.Info;

            // Vergleichsbasis: 24-h-Mittel, wenn die Einstellung seit 24 h unverändert ist – sonst der aktuelle Messwert
            double? gh = i?.HashRateGh, w = i?.PowerW;
            var basis = "aktuell";
            try
            {
                if (i is not null && History is not null
                    && History.QueryTuningEvents(d.Host, now.AddHours(-24), now).Count == 0
                    && History.Average(d.Host, now.AddHours(-24), now) is { HashRateGh: > 0 } avg)
                {
                    (gh, w, basis) = (avg.HashRateGh, avg.Power, "Ø 24 h");
                }
            }
            catch { /* Verlauf nicht lesbar – dann aktueller Wert */ }

            list.Add(EfficiencyAdvisor.Evaluate(d.Host, d.Title, d.Profile, i?.FrequencyMhz, i?.CoreVoltageMv, gh, w, basis,
                results, soaks, Config.ElectricityCtPerKwh, goal));
        }
        return list;
    }

    /// <summary>Nach einer bestätigten Änderung einen Dauertest starten, sobald der Miner wieder läuft.</summary>
    public void ScheduleSoak(HubDevice device, int hours)
    {
        if (hours is < 1 or > 168) throw new InvalidOperationException("Dauertest: 1 bis 168 Stunden.");
        device.PendingSoak = (hours, Options.Clock?.Invoke() ?? DateTime.Now);
        device.AddLog($"Dauertest ({hours} h) startet, sobald der Miner wieder läuft.");
    }

    /// <summary>Zeilen für den Tagesbericht: Effizienz-Vorschläge ab 1 Währungseinheit Ersparnis pro Monat.</summary>
    internal IEnumerable<string> AdvisorReportLines(DateTime now)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        foreach (var r in Advise(AdvisorGoal.Efficiency, now))
            if (r.Recommended is { MonthlyCostDelta: <= -1 } c)
                yield return $"Vorschlag {r.Name}: {c.FrequencyMhz} MHz / {c.CoreVoltageMv} mV → " +
                             $"{c.Jth.ToString("0.0", de)} J/TH, spart ca. {(-c.MonthlyCostDelta).ToString("0.00", de)} {Config.Currency}/Monat ({c.Confidence})";
    }
}
