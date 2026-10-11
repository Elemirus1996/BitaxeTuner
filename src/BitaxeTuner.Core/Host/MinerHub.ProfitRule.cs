using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.15 Ertrags-Automatik: Break-even-Strompreis je Miner = Ertrag je TH und Tag ÷ kWh je TH und Tag.
/// Ertrag je TH: mit Pool-Konto die tatsächlichen Gutschriften der letzten 7 Tage, sonst der PPS-Erwartungswert für BTC
/// (aktuelle Difficulty und Kurs, 2 % Gebühr). Strom: aktueller Preis wie bei den Stromkosten (fest oder Stundenpreis).
/// </summary>
public sealed partial class MinerHub
{
    private DateTime _profitInputsRefreshed = DateTime.MinValue;

    /// <summary>Ertrag je TH und Tag in der gewählten Währung und woher er stammt („pool“ oder „pps“), null = unbekannt.</summary>
    public (decimal PerThDay, string Source)? IncomePerThDay(DateTime now)
    {
        if (Profit(now) is { Source: "pool" } pf && pf.Window("7d") is { Income: { } inc, Partial: false } && inc > 0)
        {
            var th = Devices.Where(d => d.State.Online && d.State.Info is not null).Sum(d => d.State.Info!.hashRate) / 1000;
            if (th > 0.01) return (inc / 7 / (decimal)th, "pool");
        }
        var cur = Currencies.Of(Config).Code;
        var diff = Odds.Calculate(CoinType.Bitcoin, 1000, 1).Difficulty;
        if (diff is > 0 && _poolPrices.TryGetValue(CoinType.Bitcoin.Symbol(), out var p) && p.Currency == cur && p.Price is { } price)
        {
            var a = new MiningModeAssumptions();
            var coins = MiningModes.BlocksPerDay(1000, diff.Value) * (MiningModes.Subsidy + a.BtcFeesPerBlock) * (1 - a.PpsFeePercent / 100);
            return ((decimal)coins * price, "pps");
        }
        return null;
    }

    /// <summary>Stromkosten jetzt und Break-even-Preis dieses Miners in ct/kWh (für die Automatik).</summary>
    internal (double CostCt, double BreakEvenCt)? ProfitabilityOf(string host, DateTime now)
    {
        if (Device(host) is not { State: { Online: true, Normalized: { } info } } || info.HashRateGh <= 1 || info.PowerW <= 0) return null;
        if (IncomePerThDay(now) is not { } income) return null;
        var joulePerTh = info.PowerW / (info.HashRateGh / 1000);
        var kwhPerThDay = joulePerTh * 24 / 1000;
        var breakEven = (double)income.PerThDay / kwhPerThDay * 100;
        var cost = Plugs.EnergyCost.CurrentCt(Config, Prices.PriceAt(now.ToUniversalTime()));
        return (Math.Round(cost, 2), Math.Round(breakEven, 2));
    }

    /// <summary>Kurs und Difficulty für die Ertrags-Automatik höchstens stündlich auffrischen (nur wenn eine Regel sie nutzt).</summary>
    private void RefreshProfitInputs(DateTime now)
    {
        if (!Options.OnlineChecks || now - _profitInputsRefreshed < TimeSpan.FromHours(1)) return;
        var used = Devices.Any(d => d.Config.Schedule is { Enabled: true, Mode: "profit" }) ||
                   Config.GroupSchedules.Any(g => g.Schedule is { Enabled: true, Mode: "profit" });
        if (!used) return;
        _profitInputsRefreshed = now;
        _ = RefreshProfitInputsAsync();
    }

    private async Task RefreshProfitInputsAsync()
    {
        try { await Odds.RefreshAsync(); } catch { /* nächste Stunde erneut */ }
        try { await RefreshPoolPricesAsync([CoinType.Bitcoin]); } catch { /* nächste Stunde erneut */ }
    }

    /// <summary>Freigabetext der Ertrags-Automatik (Miner und Gruppe).</summary>
    public string ProfitRuleText(PresetScheduleRule s, string full, string saving)
    {
        var now = Options.Clock?.Invoke() ?? DateTime.Now;
        var income = IncomePerThDay(now);
        var cur = Currencies.Of(Config);
        var basis = income is { Source: "pool" } ? L.T("Ertrag je TH: Gutschriften deines Pool-Kontos der letzten 7 Tage")
            : L.T("Ertrag je TH: PPS-Erwartungswert für BTC (aktuelle Difficulty und Kurs, 2 % Gebühr)");
        var now2 = income is { } i ? L.T("Derzeit {0} {1} je TH und Tag.", i.PerThDay.ToString("0.00", L.Culture), cur.Code) : L.T("Derzeit unbekannt – die Regel wartet, bis Kurs und Difficulty vorliegen.");
        return L.T("Strom mindestens {0:0} % unter dem Break-even-Preis → {1}\n", s.ProfitMarginPercent, full) +
               L.T("Strom teurer als der Break-even-Preis → {0}\n", saving) +
               L.T("dazwischen bleibt die zuletzt gesetzte Voreinstellung.\n\n") +
               L.T("Break-even = Ertrag je TH und Tag ÷ Stromverbrauch je TH und Tag (aus der aktuellen Effizienz des Miners). ") +
               basis + ". " + now2 + "\n" +
               L.T("Strompreis wie bei den Stromkosten (fest oder Stundenpreis mit Aufschlag).");
    }
}
