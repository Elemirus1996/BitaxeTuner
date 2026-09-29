using System.Text;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Reports;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Tests;

/// <summary>Monats- und Jahresberichte aus history.db und Steuerdaten.</summary>
public class ReportTests
{
    private static readonly DateTime Sep = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>Miner „a“: 2 Tage im September und 1 Tag im Oktober je 12 h online mit 100 W / 1000 GH/s.</summary>
    private static HistoryStore Filled(TempDir dir)
    {
        var h = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        foreach (var day in new[] { Sep.AddDays(3), Sep.AddDays(10), Sep.AddMonths(1).AddDays(2) })
            for (var m = 0; m < 24 * 60; m++)
                h.AddSample("a", day.AddMinutes(m), m < 720 ? 1000 : 0, 60, m < 720 ? 100 : 0, m < 720);
        return h;
    }

    private static MinedReward Reward(DateTime local, decimal btc, decimal? eur) =>
        new() { Coin = CoinType.Bitcoin, Amount = btc, ReceivedAtUtc = local.ToUniversalTime(), EurPriceAtReceipt = eur };

    [Fact]
    public void Month_report_has_availability_energy_and_income()
    {
        using var dir = new TempDir();
        using var h = Filled(dir);
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        var rewards = new[] { Reward(Sep.AddDays(5), 0.001m, 60000m), Reward(Sep.AddDays(6), 0.002m, null), Reward(Sep.AddMonths(1), 1m, 1m) };

        var r = PeriodReports.Build(h, config, [("Gamma", "a")], rewards, "2026-09", new DateTime(2026, 10, 15));
        var m = Assert.Single(r.Miners);
        Assert.False(r.Partial);
        Assert.Equal(0.5, m.Availability!.Value, 6);
        Assert.Equal(1000, m.AvgHashGh!.Value, 6);
        Assert.Equal(100, m.Jth!.Value, 6);
        Assert.Equal(2.4, m.Kwh, 6);                          // 2 Tage × 12 h × 100 W
        Assert.Equal(2.4, r.Energy.Kwh, 6);
        Assert.Equal(0.72, r.Energy.Cost, 6);
        var inc = Assert.Single(r.Income);
        Assert.Equal(2, inc.Count);
        Assert.Equal(60m, inc.Eur);
        Assert.Equal(1, inc.EurMissing);
        Assert.Equal(500, r.TotalAvgHashGh!.Value, 6);          // 1000 GH/s × 50 % Verfügbarkeit
    }

    [Fact]
    public void Completed_month_is_stored_and_survives_pruning_year_combines_months()
    {
        using var dir = new TempDir();
        using var h = Filled(dir);
        var config = new AppConfig();
        var now = new DateTime(2026, 11, 20);
        PeriodReports.Build(h, config, [("Gamma", "a")], [], "2026-09", now);
        Assert.Contains("2026-09", h.StoredPeriods());

        h.Prune(1);                                             // Minutenwerte weg
        var sep = PeriodReports.Build(h, config, [("Gamma", "a")], [], "2026-09", now);
        Assert.Equal(2.4, sep.Energy.Kwh, 6);                   // aus dem gespeicherten Bericht

        var year = PeriodReports.Build(h, config, [("Gamma", "a")], [Reward(Sep, 0.001m, 50000m)], "2026", now);
        Assert.True(year.Partial);
        // September aus dem gespeicherten Bericht (2,4 kWh), Oktober live (1,2 kWh – Prune rechnet mit der echten Uhr,
        // die Oktober-Testdaten liegen danach und bleiben)
        Assert.Equal(3.6, year.Energy.Kwh, 6);
        Assert.Equal(50m, year.IncomeEur);
        Assert.Equal(2160, Assert.Single(year.Miners).OnlineMinutes);
    }

    [Fact]
    public void Months_of_a_year_up_to_now_for_the_tax_section()
    {
        using var dir = new TempDir();
        using var h = Filled(dir);
        var months = PeriodReports.Months(h, new AppConfig { ElectricityCtPerKwh = 30 }, [("Gamma", "a")],
            [Reward(Sep.AddDays(5), 0.001m, 60000m)], 2026, new DateTime(2026, 10, 15));
        Assert.Equal(10, months.Count);                          // Januar bis Oktober
        var sep = months[8];
        Assert.Equal("2026-09", sep.Period);
        Assert.Equal(0.72, sep.Energy.Cost, 6);
        Assert.Equal(60m, sep.IncomeEur);
        Assert.True(months[9].Partial);
        Assert.All(months[..8], m => Assert.Equal(0, m.Energy.Kwh));
    }

    [Fact]
    public void Running_month_is_partial_and_not_stored()
    {
        using var dir = new TempDir();
        using var h = Filled(dir);
        var r = PeriodReports.Build(h, new AppConfig(), [("Gamma", "a")], [], "2026-10", new DateTime(2026, 10, 15));
        Assert.True(r.Partial);
        Assert.DoesNotContain("2026-10", h.StoredPeriods());
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() =>
            PeriodReports.Build(h, new AppConfig(), [], [], "2026-13", DateTime.Now));
        Assert.False(PeriodReports.TryParse("../etc", out _, out _, out _));
    }

    [Fact]
    public void Csv_and_html_output_escapes_names()
    {
        using var dir = new TempDir();
        using var h = Filled(dir);
        var r = PeriodReports.Build(h, new AppConfig(), [("<script>alert(1)</script>; Gamma", "a")], [Reward(Sep.AddDays(1), 0.001m, 60000m)],
            "2026-09", new DateTime(2026, 10, 15));

        var csv = ReportRenderer.Csv(r);
        Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]);
        var text = Encoding.UTF8.GetString(csv[3..]);
        Assert.Contains("\"<script>alert(1)</script>; Gamma\"", text);   // Semikolon im Namen → in Anführungszeichen

        var html = ReportRenderer.Html(r);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script", html);                 // kein Inline-Skript (CSP)
        Assert.Contains("@media print", html);
    }
}
