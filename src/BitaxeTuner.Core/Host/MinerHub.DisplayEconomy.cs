using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.16: Daten der E-Paper-Seiten „Ertrag“ und „Solo-Chance“.</summary>
public sealed partial class MinerHub
{
    private bool _oddsRefreshing;

    /// <summary>Ertrag gegen Strom wie in der Übersicht (<see cref="Profit"/>), alle Miner.</summary>
    private DisplayProfit? BuildProfitPage(DateTime now)
    {
        if (Profit(now) is not { } p) return null;
        string Label(string key) => key switch
        {
            "24h" => L.T("24 Stunden"),
            "7d" => L.T("7 Tage"),
            "30d" => L.T("30 Tage"),
            _ => now.ToString("MMMM", L.Culture),
        };
        return new DisplayProfit(Currencies.Of(Config).Symbol, p.Source,
            p.Windows.Select(w => new DisplayProfitRow(Label(w.Key), w.Income, w.Cost, w.Net, w.Kwh, w.Partial)).ToList());
    }

    /// <summary>
    /// Solo-Chance je Coin (BTC, BCH) wie in der Desktop-Kachel: Miner auf einem Pool-Konto zählen nur im Modus SOLO (mit dem
    /// Coin laut Pool), sonst nach Coin des Miners. Bei einer Anzeige mit Gruppe nur deren Miner.
    /// </summary>
    private List<DisplaySoloCoin> BuildSolo()
    {
        if (Options.OnlineChecks && !_oddsRefreshing)
        {
            _oddsRefreshing = true;
            _ = Task.Run(async () =>
            {
                try { await Odds.RefreshAsync(); } catch { /* nächste Seite erneut */ }
                finally { _oddsRefreshing = false; }
            });
        }
        var group = CurGroup;
        var online = Devices.Where(d => d.State.Online && d.State.Info is not null
            && (group is null || d.Config.Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))).ToList();
        var list = new List<DisplaySoloCoin>();
        foreach (var coin in CoinTypeExtensions.WalletCoins)
        {
            var miners = online.Where(d => PoolWorkerOf(d.State) is { } w
                ? w.IsSolo && PoolCoinOf(d.State) == coin
                : ResolveCoin(d.State) == coin).ToList();
            var gh = miners.Sum(d => d.State.Info!.hashRate);
            var r = Odds.Calculate(coin, gh, miners.Count);
            list.Add(new DisplaySoloCoin(coin.Symbol(), miners.Count, gh, r.Difficulty, r.ChancePerDay, r.ChancePerYear, r.ExpectedDays));
        }
        return list;
    }
}
