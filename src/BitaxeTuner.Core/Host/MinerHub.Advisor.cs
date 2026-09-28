using System.Globalization;
using BitaxeTuner.Core.Advisor;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.I18n;

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
            var basis = L.T("aktuell");
            try
            {
                if (i is not null && History is not null
                    && History.QueryTuningEvents(d.Host, now.AddHours(-24), now).Count == 0
                    && History.Average(d.Host, now.AddHours(-24), now) is { HashRateGh: > 0 } avg)
                {
                    (gh, w, basis) = (avg.HashRateGh, avg.Power, L.T("Ø 24 h"));
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
        if (hours is < 1 or > 168) throw new InvalidOperationException(L.T("Dauertest: 1 bis 168 Stunden."));
        device.PendingSoak = (hours, Options.Clock?.Invoke() ?? DateTime.Now);
        device.AddLog(L.T("Dauertest ({0} h) startet, sobald der Miner wieder läuft.", hours));
    }

    /// <summary>Zeilen für den Tagesbericht: Effizienz-Vorschläge ab 1 Währungseinheit Ersparnis pro Monat.</summary>
    internal IEnumerable<string> AdvisorReportLines(DateTime now)
    {
        var de = L.Culture;
        foreach (var r in Advise(AdvisorGoal.Efficiency, now))
            if (r.Recommended is { MonthlyCostDelta: <= -1 } c)
                yield return L.T("Vorschlag {0}: {1} MHz / {2} mV → ", r.Name, c.FrequencyMhz, c.CoreVoltageMv) +
                             L.T("{0} J/TH, spart ca. {1} {2}/Monat ({3})", c.Jth.ToString("0.0", de), (-c.MonthlyCostDelta).ToString("0.00", de), Config.Currency, c.Confidence);
    }
}
