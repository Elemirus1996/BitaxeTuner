using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Reports;

namespace BitaxeTuner.Server.Api;

/// <summary>Monats- und Jahresberichte (JSON, CSV, druckbares HTML). Nur Admin – enthält Zuflüsse aus dem Steuer-Bereich.</summary>
public static class ReportEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/reports", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            periods = h.ReportPeriods(DateTime.Now),
            monthly = h.Config.DailyReport.Monthly,
        })));

        g.MapGet("/reports/{period}", async (string period, string? format, HubService hub) =>
        {
            if (!PeriodReports.TryParse(period, out _, out _, out _))
                throw new LocalizedException("Ungültiger Zeitraum: {0} (z. B. 2026-09 oder 2026)", period);
            var report = await hub.RunAsync(h => h.BuildReport(period, DateTime.Now));
            return format switch
            {
                "csv" => Results.File(ReportRenderer.Csv(report), "text/csv; charset=utf-8", $"BitaxeTuner-{period}.csv"),
                "html" => Results.Content(ReportRenderer.Html(report), "text/html; charset=utf-8"),
                _ => Results.Json(report),
            };
        });

        // Steuer-Bereich: Stromkosten je Monat neben den Zuflüssen
        g.MapGet("/reports/{year:int}/months", async (int year, HubService hub) =>
        {
            if (year is < 2020 or > 2100) throw new LocalizedException("Ungültiger Zeitraum: {0} (z. B. 2026-09 oder 2026)", year);
            var months = await hub.RunAsync(h => h.MonthReports(year, DateTime.Now));
            return Results.Json(new
            {
                year,
                currency = months.FirstOrDefault()?.Currency,
                incomeCurrency = months.FirstOrDefault()?.IncomeSymbol,
                months = months.Select(m => new { m.Period, m.Partial, kwh = m.Energy.Kwh, cost = m.Energy.Cost, incomeEur = m.IncomeEur,
                    incomeMissing = m.Income.Sum(i => i.EurMissing), hasData = m.Miners.Any(x => x.TotalMinutes > 0) }).ToList(),
            });
        });

        g.MapPost("/reports/{period}/send", async (string period, HubService hub) =>
        {
            if (!PeriodReports.TryParse(period, out _, out _, out _))
                throw new LocalizedException("Ungültiger Zeitraum: {0} (z. B. 2026-09 oder 2026)", period);
            var error = await hub.RunAsync(h => h.SendMonthlyReportAsync(period, DateTime.Now, markSent: false));
            return Results.Json(new { ok = error is null, error });
        });
    }
}
