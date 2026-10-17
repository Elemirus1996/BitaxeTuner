using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Reports;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Tests;

/// <summary>0.9.21: Amortisation je Miner – Ertragsanteil nach Hashrate minus eigener Strom, Prognose, Garantie.</summary>
public class AmortizationTests
{
    [Fact]
    public void Share_of_income_minus_own_power_counts_against_the_price_and_is_announced_once()
    {
        using var dir = new TempDir();
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        config.Notifications.Provider = "ntfy";
        config.Notifications.NtfyTopic = "t";
        config.Devices.Add(new DeviceConfig { Name = "Klein", Host = "10.0.12.1" });
        config.Devices.Add(new DeviceConfig { Name = "Groß", Host = "10.0.12.2" });
        using var hub = new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        var sent = new List<string>();
        hub.Notify.Sending += (key, _, _, _) => sent.Add(key);
        var now = DateTime.Now;

        // 3 Tage Verlauf (Minutenwerte): „Klein“ liefert ein Viertel der Hashrate
        for (var t = now.AddDays(-3); t < now; t = t.AddMinutes(1))
        {
            hub.History!.AddSample("10.0.12.1", t, 1000, 50, 15, true);
            hub.History.AddSample("10.0.12.2", t, 3000, 55, 45, true);
        }
        MinedReward R(double daysAgo, decimal eur) => new()
        {
            Coin = CoinType.Bitcoin, TxId = Guid.NewGuid().ToString("N"), ReceivedAtUtc = now.ToUniversalTime().AddDays(-daysAgo),
            Amount = eur / 50000m, EurPriceAtReceipt = 50000m,
        };
        hub.TaxRepository.SaveRewards([R(2.5, 400), R(1.5, 400), R(0.5, 200)]);   // 1000 € gesamt

        var klein = hub.Devices.Single(d => d.Host == "10.0.12.1");
        Assert.Null(hub.AmortizationOf(klein, now));                       // ohne Kaufdaten nichts
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => hub.SetPurchase(klein, new DevicePurchase { Price = 300, Date = now.AddDays(3) }));
        hub.SetPurchase(klein, new DevicePurchase { Price = 300, Date = now.AddDays(-400), WarrantyMonths = 14 });

        var a = hub.AmortizationOf(klein, now)!;
        Assert.Equal("tax", a.Source);
        Assert.Equal(250m, a.Income);                                        // ¼ von 1000 €
        Assert.InRange(a.Cost, 0.2m, 0.5m);                                  // 15 W × 72 h × 30 ct ≈ 0,32 €
        Assert.Equal(a.Income - a.Cost, a.Net);
        Assert.Equal(300m - a.Net, a.Remaining);
        Assert.False(a.Paid);
        Assert.True(a.TrackedFrom > a.PurchaseDate);                         // vor dem ersten Messwert keine Daten
        Assert.NotNull(a.NetPerDay);
        Assert.NotNull(a.Eta);
        Assert.Equal(a.PurchaseDate.AddMonths(14), a.WarrantyUntil);
        Assert.Same(a, hub.AmortizationOf(klein, now.AddMinutes(5)));         // 30 min zwischengespeichert

        // Garantie endet in < 30 Tagen (400 Tage nach Kauf, 14 Monate Garantie) → einmal melden
        hub.TickAmortizationForTest(now);
        hub.TickAmortizationForTest(now.AddHours(2));
        Assert.Single(sent, k => k == "warranty:10.0.12.1");
        Assert.DoesNotContain(sent, k => k.StartsWith("amortized:"));
        Assert.True(klein.Config.Purchase!.WarrantyNotified);

        // Mehr Ertrag → bezahlt gemacht, ebenfalls nur einmal
        hub.TaxRepository.SaveRewards([R(2.5, 400), R(1.5, 400), R(0.5, 200), R(0.2, 400)]);
        hub.TickAmortizationForTest(now.AddHours(2).AddMinutes(10));        // Zwischenspeicher (30 min) noch gültig → noch nicht
        Assert.DoesNotContain(sent, k => k.StartsWith("amortized:"));
        hub.TickAmortizationForTest(now.AddHours(3));
        hub.TickAmortizationForTest(now.AddHours(5));
        Assert.Single(sent, k => k == "amortized:10.0.12.1");
        Assert.True(hub.AmortizationOf(klein, now.AddHours(5))!.Paid);

        // Monatsbericht: Abschnitt „Amortisation“ (nur Miner mit Kaufdaten)
        var report = hub.BuildReport(now.ToString("yyyy-MM"), now);
        var row = Assert.Single(report.Amortization);
        Assert.Equal("Klein", row.Name);
        Assert.Contains("Amortisation", ReportRenderer.Html(report));
        Assert.Contains("Klein", System.Text.Encoding.UTF8.GetString(ReportRenderer.Csv(report)));

        // Löschen: alle Felder leer
        hub.SetPurchase(klein, new DevicePurchase());
        Assert.Null(klein.Config.Purchase);
        Assert.Null(hub.AmortizationOf(klein, now));
    }
}
