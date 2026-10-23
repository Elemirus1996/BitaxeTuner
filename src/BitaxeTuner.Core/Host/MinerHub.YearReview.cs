using System.Globalization;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Reports;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.25 Jahresrückblick „Dein Mining-Jahr“ aus den Monatsberichten, Best-Diff-Rekorden und Meilensteinen.</summary>
public sealed partial class MinerHub
{
    private (DateTime At, string Key, YearReview Data)? _yearReviewCache;

    /// <param name="period">„last12“ oder ein Jahr wie „2026“.</param>
    /// <param name="showNames">false: „Miner 1“, „Miner 2“ … statt der eigenen Namen (zum Teilen).</param>
    public YearReview BuildYearReview(string? period, bool showNames, DateTime now)
    {
        if (History is null) throw new LocalizedException("Verlaufsdatenbank nicht verfügbar.");
        if (!YearReview.TryParse(period, now, out var from, out var to, out var title))
            throw new LocalizedException("Ungültiger Zeitraum: {0} (z. B. last12 oder 2026)", period ?? "");
        var key = $"{from:yyyyMM}-{to:yyyyMM}-{showNames}-{Loc.Current.Language}";
        if (_yearReviewCache is { } c && c.Key == key && now - c.At < TimeSpan.FromMinutes(15) && now >= c.At) return c.Data;

        var months = new List<PeriodReport>();
        for (var m = from; m < to && m <= now; m = m.AddMonths(1))
            months.Add(BuildReport(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), now));
        var miners = ReportMiners();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < miners.Count; i++)
            names[miners[i].Host] = showNames ? miners[i].Name : L.T("Miner {0}", i + 1);
        var review = YearReview.Build(title, from, to, now, months, names, History.GetBestDiffs(), News.LocalItems("milestone"));
        _yearReviewCache = (now, key, review);
        return review;
    }
}
