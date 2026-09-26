using System.Collections.ObjectModel;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Benchmark;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BitaxeTuner.App.ViewModels;

/// <summary>Eine Zeile im Miner-Vergleich.</summary>
public sealed record ComparisonRow(
    string Name, string Chip, string Current,
    double? Hash24, double? Eff24, double? Temp24, double? Availability7,
    string BestHash, string BestEff, int? MaxStableFrequency, string Benchmark)
{
    public string Hash24Text => Hash24 is { } h ? $"{h:0} GH/s" : "–";
    public string Eff24Text => Eff24 is { } e ? $"{e:0.00} J/TH" : "–";
    public string Temp24Text => Temp24 is { } t ? $"{t:0.0} °C" : "–";
    public string AvailabilityText => Availability7 is { } a ? $"{a:P1}" : "–";
    public string MaxStableText => MaxStableFrequency is { } f ? $"{f} MHz" : "–";
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
                i is null ? "–" : $"{i.AsicModel} · Board {i.BoardVersion}",
                i is null ? "offline" : $"{i.FrequencyMhz} MHz / {i.CoreVoltageMv} mV",
                avg?.HashRateGh, avg?.EfficiencyJth, avg?.Temp, availability,
                Format(bestHash), Format(bestEff), maxStable,
                session is null ? "kein Benchmark" : $"{session.StartedAt:dd.MM.yyyy} · {results.Count} Messungen" + (session.IsFinished ? "" : " (unvollständig)")));
        }

        var withBench = Rows.Where(r => r.MaxStableFrequency is not null).ToList();
        var bestChip = withBench.OrderByDescending(r => r.MaxStableFrequency).FirstOrDefault();
        var mostEfficient = Rows.Where(r => r.Eff24 is not null).OrderBy(r => r.Eff24).FirstOrDefault();
        if (Rows.Count == 0)
        {
            Summary = "Keine echten Miner in der Liste – simulierte Geräte werden hier nicht verglichen.";
            return;
        }
        Summary = string.Join(" · ", new[]
        {
            bestChip is null ? null : $"Höchste stabile Frequenz: {bestChip.Name} ({bestChip.MaxStableText})",
            mostEfficient is null ? null : $"Effizientester Miner (24 h): {mostEfficient.Name} ({mostEfficient.Eff24Text})",
            withBench.Count < Rows.Count ? $"{Rows.Count - withBench.Count} Miner ohne Benchmark" : null,
        }.Where(x => x is not null));
    }

    private static string Format(StepResult? r) => r is null ? "–"
        : $"{r.FrequencyMhz} MHz / {r.CoreVoltageMv} mV → {r.AvgHashRateGh:0} GH/s · {r.EfficiencyJth:0.00} J/TH";
}
