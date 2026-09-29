using System.Globalization;
using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Plugs;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Reports;

/// <summary>Ein Miner im Bericht. Mittelwerte über die Online-Minuten, kWh aus AxeOS.</summary>
public sealed record PeriodMinerRow(string Name, string Host, int OnlineMinutes, int TotalMinutes,
    double? AvgHashGh, double? AvgTemp, double? AvgPowerW, double Kwh, int TuningChanges)
{
    public double? Availability => TotalMinutes > 0 ? (double)OnlineMinutes / TotalMinutes : null;
    public double? Jth => AvgHashGh is > 0 && AvgPowerW is { } w ? w / (AvgHashGh.Value / 1000) : null;
}

/// <summary>Smart Plug im Bericht: gemessene Energie und Messdauer.</summary>
public sealed record PeriodPlugRow(string Name, string Role, double Kwh, double MeasuredHours);

/// <summary>Zuflüsse eines Coins; EurMissing = Zuflüsse ohne Euro-Kurs.</summary>
public sealed record PeriodIncome(string Coin, int Count, decimal Amount, decimal Eur, int EurMissing);

/// <summary>
/// Monats- oder Jahresbericht. Energy: Energie/Kosten wie im Tagesbericht (Smart Plugs, Stundenpreise).
/// Partial = Zeitraum läuft noch; DataFrom = ab hier liegen Messwerte vor (null = alles abgedeckt).
/// </summary>
public sealed record PeriodReport(string Period, DateTime From, DateTime To, bool Partial,
    IReadOnlyList<PeriodMinerRow> Miners, IReadOnlyList<PeriodPlugRow> Plugs, CostResult Energy,
    IReadOnlyList<PeriodIncome> Income, string Currency, DateTime? DataFrom)
{
    /// <summary>Im Mittel gelieferte Hashrate aller Miner: Σ Ø Hashrate × Verfügbarkeit.</summary>
    public double? TotalAvgHashGh => Miners.Any(m => m.AvgHashGh is not null)
        ? Miners.Where(m => m.AvgHashGh is not null).Sum(m => m.AvgHashGh!.Value * (m.Availability ?? 0))
        : null;
    public decimal IncomeEur => Income.Sum(i => i.Eur);
}

/// <summary>Berichte bauen, abgeschlossene Monate in history.db ablegen.</summary>
public static class PeriodReports
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>„2026-09“ (Monat) oder „2026“ (Jahr).</summary>
    public static bool TryParse(string period, out DateTime from, out DateTime to, out bool isYear)
    {
        from = to = default;
        isYear = false;
        if (DateTime.TryParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        {
            from = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Local);
            to = from.AddMonths(1);
            return m.Year is >= 2020 and <= 2100;
        }
        if (period.Length == 4 && int.TryParse(period, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2020 and <= 2100)
        {
            from = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Local);
            to = from.AddYears(1);
            isYear = true;
            return true;
        }
        return false;
    }

    public static PeriodReport Build(HistoryStore history, AppConfig config, IReadOnlyList<(string Name, string Host)> miners,
        IEnumerable<MinedReward> rewards, string period, DateTime now)
    {
        if (!TryParse(period, out var from, out var to, out var isYear))
            throw new I18n.LocalizedException("Ungültiger Zeitraum: {0} (z. B. 2026-09 oder 2026)", period);
        var rewardList = rewards.ToList();
        if (!isYear) return Month(history, config, miners, rewardList, period, from, to, now);

        var months = new List<PeriodReport>();
        for (var m = from; m < to && m <= now; m = m.AddMonths(1))
            months.Add(Month(history, config, miners, rewardList, m.ToString("yyyy-MM", CultureInfo.InvariantCulture), m, m.AddMonths(1), now));
        return Combine(period, from, to, now, months, Income(rewardList, from, to), config.Currency);
    }

    /// <summary>Alle Monate eines Jahres bis heute (Steuer-Bereich: Stromkosten je Monat neben den Zuflüssen).</summary>
    public static List<PeriodReport> Months(HistoryStore history, AppConfig config, IReadOnlyList<(string Name, string Host)> miners,
        IEnumerable<MinedReward> rewards, int year, DateTime now)
    {
        var list = rewards.ToList();
        var result = new List<PeriodReport>();
        for (var m = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local); m.Year == year && m <= now; m = m.AddMonths(1))
            result.Add(Month(history, config, miners, list, m.ToString("yyyy-MM", CultureInfo.InvariantCulture), m, m.AddMonths(1), now));
        return result;
    }

    private static PeriodReport Month(HistoryStore history, AppConfig config, IReadOnlyList<(string Name, string Host)> miners,
        List<MinedReward> rewards, string period, DateTime from, DateTime to, DateTime now)
    {
        var complete = to <= now;
        if (complete && history.GetPeriodReport(period) is { } stored)
        {
            try
            {
                if (JsonSerializer.Deserialize<PeriodReport>(stored, Json) is { } r)
                    return r with { Income = Income(rewards, from, to) }; // Zuflüsse können nachträglich erfasst werden
            }
            catch (JsonException) { /* neu berechnen */ }
        }

        var end = complete ? to : now;
        var rows = new List<PeriodMinerRow>();
        foreach (var (name, host) in miners)
        {
            var (online, total) = history.MinuteCounts(host, from, end);
            var avg = history.Average(host, from, end);
            var kwh = history.HourlyEnergyWh(host, from, end).Values.Sum() / 1000.0;
            var changes = history.QueryTuningEvents(host, from, end).Count(e => e.Source != TuningSource.Benchmark);
            rows.Add(new PeriodMinerRow(name, host, online, total, avg?.HashRateGh, avg?.Temp, avg?.Power, kwh, changes));
        }
        var plugs = config.Plugs.Items.Select(p =>
        {
            var hours = history.HourlyPlugPower(p.Id, from, end).Values.ToList();
            return new PeriodPlugRow(p.Name, p.Role, hours.Sum(h => h.Value * h.Count / 60.0) / 1000.0, hours.Sum(h => h.Count) / 60.0);
        }).Where(p => p.MeasuredHours > 0).ToList();
        var energy = EnergyCost.Compute(history, config, miners.Select(m => m.Host).ToList(), from, end);
        var earliest = history.EarliestSample();
        var report = new PeriodReport(period, from, to, !complete, rows, plugs, energy, Income(rewards, from, to), config.Currency,
            earliest is { } e && e > from ? e : null);

        // Abgeschlossenen Monat mit Messwerten einmal ablegen – bleibt, auch wenn die Minutenwerte bereinigt werden
        if (complete && rows.Any(r => r.TotalMinutes > 0))
            try { history.SavePeriodReport(period, JsonSerializer.Serialize(report with { Income = [] }, Json)); } catch { /* nicht kritisch */ }
        return report;
    }

    /// <summary>Jahr aus Monaten: Summen, Mittelwerte gewichtet nach Online-Minuten.</summary>
    private static PeriodReport Combine(string period, DateTime from, DateTime to, DateTime now, List<PeriodReport> months,
        List<PeriodIncome> income, string currency)
    {
        var rows = months.SelectMany(m => m.Miners).GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var online = g.Sum(r => r.OnlineMinutes);
            double? W(Func<PeriodMinerRow, double?> f) => online > 0 && g.Any(r => f(r) is not null)
                ? g.Where(r => f(r) is not null).Sum(r => f(r)!.Value * r.OnlineMinutes) / Math.Max(1, g.Where(r => f(r) is not null).Sum(r => r.OnlineMinutes))
                : null;
            return new PeriodMinerRow(g.Last().Name, g.Key, online, g.Sum(r => r.TotalMinutes), W(r => r.AvgHashGh), W(r => r.AvgTemp),
                W(r => r.AvgPowerW), g.Sum(r => r.Kwh), g.Sum(r => r.TuningChanges));
        }).ToList();
        var plugs = months.SelectMany(m => m.Plugs).GroupBy(p => (p.Name, p.Role))
            .Select(g => new PeriodPlugRow(g.Key.Name, g.Key.Role, g.Sum(p => p.Kwh), g.Sum(p => p.MeasuredHours))).ToList();
        var energy = new CostResult(months.Sum(m => m.Energy.Kwh), months.Sum(m => m.Energy.Cost),
            months.Sum(m => m.Energy.Hours), months.Sum(m => m.Energy.DynamicHours));
        // Frühester Messwert ist für alle Monate derselbe; gesetzt nur, wenn er nach Beginn des Zeitraums liegt
        var dataFrom = months.Select(m => m.DataFrom).FirstOrDefault(d => d is not null);
        return new PeriodReport(period, from, to, to > now, rows, plugs, energy, income, currency, dataFrom);
    }

    private static List<PeriodIncome> Income(List<MinedReward> rewards, DateTime from, DateTime to) =>
        rewards.Where(r => r.ReceivedAtLocal >= from && r.ReceivedAtLocal < to)
            .GroupBy(r => r.CoinSymbol)
            .Select(g => new PeriodIncome(g.Key, g.Count(), g.Sum(r => r.Amount), g.Sum(r => r.EurValue ?? 0), g.Count(r => r.EurValue is null)))
            .OrderBy(i => i.Coin).ToList();
}
