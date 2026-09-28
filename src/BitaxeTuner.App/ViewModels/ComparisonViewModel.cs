using System.Collections.ObjectModel;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Benchmark;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.ViewModels;

/// <summary>Eine Zeile im Miner-Vergleich.</summary>
public sealed record ComparisonRow(
    string Name, string Chip, string Current,
    double? Hash24, double? Eff24, double? Temp24, double? Availability7,
    string BestHash, string BestEff, int? MaxStableFrequency, string Benchmark)
{
    public string Hash24Text => Hash24 is { } h ? L.T("{0:0} GH/s", h) : "–";
    public string Eff24Text => Eff24 is { } e ? L.T("{0:0.00} J/TH", e) : "–";
    public string Temp24Text => Temp24 is { } t ? $"{t:0.0} °C" : "–";
    public string AvailabilityText => Availability7 is { } a ? $"{a:P1}" : "–";
    public string MaxStableText => MaxStableFrequency is { } f ? L.T("{0} MHz", f) : "–";
}

/// <summary>
/// Alle Miner nebeneinander: aktuelle Einstellung, 24-h-Mittel aus history.db, Verfügbarkeit (7 Tage)
/// und die besten Ergebnisse des letzten Benchmarks – zeigt, welcher Chip der gute ist.
/// </summary>
public sealed partial class ComparisonViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<ComparisonRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "";

    [RelayCommand]
    public void Refresh()
    {
        Rows.Clear();
        var now = DateTime.Now;
        foreach (var state in host.Polling.States.Where(s => !AppHost.IsSimulated(s.Config.Host)))
        {
            var host_ = state.Config.Host;
            var i = state.Normalized;
            var avg = host.History?.Average(host_, now.AddHours(-24), now);
            double? availability = null;
            try { availability = host.History?.Availability(host_, now.AddDays(-7)); } catch { /* keine Daten */ }

            var session = host.Results.LoadLatest(host_, i?.Hostname);
            var results = session?.Results ?? [];
            var bestHash = ResultRanking.Best(results, RankingMode.MaxHashrate);
            var bestEff = ResultRanking.Best(results, RankingMode.Efficiency);
            var maxStable = results.Where(r => r.IsStable).Select(r => (int?)r.FrequencyMhz).Max();

            Rows.Add(new ComparisonRow(
                state.Config.Name,
                i is null ? "–" : L.T("{0} · Board {1}", i.AsicModel, i.BoardVersion),
                i is null ? "offline" : L.T("{0} MHz / {1} mV", i.FrequencyMhz, i.CoreVoltageMv),
                avg?.HashRateGh, avg?.EfficiencyJth, avg?.Temp, availability,
                Format(bestHash), Format(bestEff), maxStable,
                session is null ? L.T("kein Benchmark") : L.T("{0:d} · {1} Messungen", session.StartedAt, results.Count) + (session.IsFinished ? "" : L.T(" (unvollständig)"))));
        }

        var withBench = Rows.Where(r => r.MaxStableFrequency is not null).ToList();
        var bestChip = withBench.OrderByDescending(r => r.MaxStableFrequency).FirstOrDefault();
        var mostEfficient = Rows.Where(r => r.Eff24 is not null).OrderBy(r => r.Eff24).FirstOrDefault();
        if (Rows.Count == 0)
        {
            Summary = L.T("Keine echten Miner in der Liste – simulierte Geräte werden hier nicht verglichen.");
            return;
        }
        Summary = string.Join(" · ", new[]
        {
            bestChip is null ? null : L.T("Höchste stabile Frequenz: {0} ({1})", bestChip.Name, bestChip.MaxStableText),
            mostEfficient is null ? null : L.T("Effizientester Miner (24 h): {0} ({1})", mostEfficient.Name, mostEfficient.Eff24Text),
            withBench.Count < Rows.Count ? L.T("{0} Miner ohne Benchmark", Rows.Count - withBench.Count) : null,
        }.Where(x => x is not null));
    }

    private static string Format(StepResult? r) => r is null ? "–"
        : L.T("{0} MHz / {1} mV → {2:0} GH/s · {3:0.00} J/TH", r.FrequencyMhz, r.CoreVoltageMv, r.AvgHashRateGh, r.EfficiencyJth);
}
