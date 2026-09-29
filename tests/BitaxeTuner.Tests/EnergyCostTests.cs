using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Plugs;

namespace BitaxeTuner.Tests;

/// <summary>Stromkosten mit Stundenpreisen (aWATTar/Tibber) aus history.db.</summary>
public class EnergyCostTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Local);

    private static HistoryStore Filled(TempDir dir, double watt = 100, int hours = 2)
    {
        var history = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        for (var m = 0; m < hours * 60; m++) history.AddSample("a", Start.AddMinutes(m), 500, 55, watt, true);
        return history;
    }

    private static PricePoint Price(DateTime localStart, int minutes, double ct) =>
        new(localStart.ToUniversalTime(), localStart.AddMinutes(minutes).ToUniversalTime(), ct);

    [Fact]
    public void Hourly_prices_plus_surcharge_and_fixed_price_where_missing()
    {
        using var dir = new TempDir();
        using var history = Filled(dir);
        history.AddPrices([Price(Start, 60, 10)]);                   // nur die erste Stunde hat einen Preis
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        config.PriceSource.DynamicCosts = true;
        config.PriceSource.SurchargeCt = 5;

        var r = EnergyCost.Compute(history, config, ["a"], Start, Start.AddHours(2));
        Assert.Equal(0.2, r.Kwh, 6);
        Assert.Equal(0.1 * 15 / 100 + 0.1 * 30 / 100, r.Cost, 6);  // (10 + 5) ct und 30 ct fest
        Assert.Equal(2, r.Hours);
        Assert.Equal(1, r.DynamicHours);
        Assert.Equal(22.5, r.AvgCt!.Value, 6);

        config.PriceSource.DynamicCosts = false;                    // aus: alles zum festen Preis
        Assert.Equal(0.2 * 30 / 100, EnergyCost.Compute(history, config, ["a"], Start, Start.AddHours(2)).Cost, 6);
    }

    [Fact]
    public void Quarter_hour_prices_are_averaged_per_hour()
    {
        using var dir = new TempDir();
        using var history = Filled(dir, hours: 1);
        history.AddPrices([Price(Start, 15, 8), Price(Start.AddMinutes(15), 15, 12), Price(Start.AddMinutes(30), 15, 8), Price(Start.AddMinutes(45), 15, 12)]);
        Assert.Equal(10, Assert.Single(history.HourlyPrices(Start, Start.AddHours(1))).Value, 6);
        history.AddPrices([Price(Start, 15, 8)]);                   // erneuter Abruf: keine Doppelten
        Assert.True(history.CountRows()["prices"] == 4);
    }

    [Fact]
    public void Plug_measurement_replaces_axeos_per_hour_when_it_covers_half_an_hour()
    {
        using var dir = new TempDir();
        using var history = Filled(dir);
        for (var m = 0; m < 40; m++) history.AddPlugSample("p", Start.AddMinutes(m), 120, null);   // Stunde 1: 40 Minuten
        for (var m = 60; m < 80; m++) history.AddPlugSample("p", Start.AddMinutes(m), 120, null);  // Stunde 2: nur 20 Minuten
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        config.Plugs.Items.Add(new SmartPlugConfig { Id = "p", Role = "miners", Miners = ["a"] });

        var r = EnergyCost.Compute(history, config, ["a"], Start, Start.AddHours(2));
        Assert.Equal(0.12 + 0.1, r.Kwh, 6);                         // Stunde 1 Steckdose, Stunde 2 AxeOS
    }

    [Fact]
    public void Daily_report_shows_dynamic_price_note()
    {
        using var dir = new TempDir();
        var now = Start.AddHours(24);
        using var history = new HistoryStore(Path.Combine(dir.Path, "history.db"));
        for (var m = 0; m < 24 * 60; m++) history.AddSample("a", Start.AddMinutes(m), 500, 55, 100, true);
        history.AddPrices(Enumerable.Range(0, 24).Select(hh => Price(Start.AddHours(hh), 60, 20)));
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        config.PriceSource.DynamicCosts = true;
        config.PriceSource.SurchargeCt = 10;

        var (_, text) = DailyReport.Build(history, config, [("Gamma", "a")], now);
        var c = BitaxeTuner.Core.I18n.L.Culture;
        Assert.Contains($"{2.4.ToString("0.00", c)} kWh ≈ {0.72.ToString("0.00", c)}", text);   // 2,4 kWh × 30 ct (20 + 10)
        Assert.Contains(BitaxeTuner.Core.I18n.L.T(" (Ø {0} ct/kWh, {1} von {2} h mit Stundenpreis)", 30.0.ToString("0.0", c), 24, 24), text);
    }

    [Fact]
    public void Contract_price_gross_or_net_and_vat_on_awattar_but_not_tibber()
    {
        var config = new AppConfig { ElectricityCtPerKwh = 25 };
        Assert.Equal(25, EnergyCost.FixedCt(config));                 // Standard: brutto wie bisher
        config.ElectricityPriceIsNet = true;
        Assert.Equal(29.75, EnergyCost.FixedCt(config), 6);          // 25 ct netto + 19 %
        config.VatPercent = 7;
        Assert.Equal(26.75, EnergyCost.FixedCt(config), 6);

        config.VatPercent = 19;
        config.PriceSource.SurchargeCt = 10;                         // netto wie der Vertrag
        config.PriceSource.Source = "awattar-de";
        Assert.Equal(10 * 1.19 + 10 * 1.19, EnergyCost.DynamicCt(config, 10), 6);
        config.PriceSource.Source = "tibber";
        Assert.Equal(30 + 11.9, EnergyCost.DynamicCt(config, 30), 6); // Tibber-Endpreis ohne weitere MwSt.

        var old = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("""{"ElectricityCtPerKwh": 32}""")!;
        Assert.False(old.ElectricityPriceIsNet);
        Assert.Equal(32, EnergyCost.FixedCt(old));
    }

    [Fact]
    public void Current_price_for_cost_per_day()
    {
        var config = new AppConfig { ElectricityCtPerKwh = 30 };
        Assert.Equal(30, EnergyCost.CurrentCt(config, 12));
        config.PriceSource.DynamicCosts = true;
        config.PriceSource.SurchargeCt = 20;
        Assert.Equal(32, EnergyCost.CurrentCt(config, 12));         // Stundenpreis + Aufschlag
        Assert.Equal(30, EnergyCost.CurrentCt(config, null));       // kein Preis: fester Wert
    }
}
