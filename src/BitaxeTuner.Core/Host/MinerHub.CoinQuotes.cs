using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Kurs eines Coins, den mindestens ein Miner gerade schürft.</summary>
/// <param name="Change24hPercent">Veränderung gegenüber vor 24 Stunden in % (null = unbekannt, z. B. Kurs laut Pool).</param>
/// <param name="Source">„coingecko“ oder „pool“ (Kurs, den der Pool meldet).</param>
/// <param name="Miners">Wie viele Miner diesen Coin gerade schürfen.</param>
/// <param name="Spark">Verlauf der letzten 24 Stunden (bis zu 24 Punkte, leer = unbekannt).</param>
public sealed record CoinQuote(string Symbol, decimal? Price, double? Change24hPercent, string Source, int Miners, IReadOnlyList<double> Spark);

/// <summary>
/// 0.9.16: Kurse der Coins, die die Miner gerade schürfen – mit Pool-Konto der Coin je Worker (z. B. DGB, NMC, WJK), sonst
/// BTC/BCH laut Pool. CoinGecko-Tagesverlauf höchstens stündlich je Coin; ohne CoinGecko-Eintrag der Kurs laut Pool.
/// </summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, (DateTime AtUtc, string Currency, decimal? Price, double? Change, string Source, List<double> Spark)> _coinQuotes =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _coinQuotesChecked = DateTime.MinValue;
    private bool _coinQuotesBusy;

    /// <summary>Coin, den der Miner gerade schürft (Pool-Konto, sonst laut Pool/Einstellung; unbekannt = BTC, da SHA-256-Miner).</summary>
    public string MinedSymbolOf(MinerState s) => PoolCoinSymbolOf(s) ?? ResolveCoin(s)?.Symbol() ?? "BTC";

    /// <summary>Kurse aller gerade geschürften Coins, meistgeschürfte zuerst.</summary>
    public IReadOnlyList<CoinQuote> CoinQuotes()
    {
        var cur = Currencies.Of(Config).Code;
        var active = States.Where(s => s.Online).Select(MinedSymbolOf)
            .GroupBy(x => x.ToUpperInvariant()).Select(g => (Symbol: g.Key, Miners: g.Count()));
        var list = new List<CoinQuote>();
        foreach (var (symbol, miners) in active)
        {
            if (_coinQuotes.TryGetValue(symbol, out var q) && q.Currency == cur)
                list.Add(new CoinQuote(symbol, q.Price, q.Change, q.Source, miners, q.Spark));
            else
                list.Add(new CoinQuote(symbol, PoolPriceFor(symbol, cur), null, "pool", miners, []));
        }
        return list.OrderByDescending(c => c.Miners).ThenBy(c => c.Symbol).ToList();
    }

    /// <summary>Aus dem Abfragetakt: höchstens alle 10 min prüfen, je Coin höchstens stündlich bei CoinGecko nachfragen.</summary>
    private void RefreshCoinQuotes(DateTime now)
    {
        if (!Options.OnlineChecks || _coinQuotesBusy || now - _coinQuotesChecked < TimeSpan.FromMinutes(10)) return;
        _coinQuotesChecked = now;
        var cur = Currencies.Of(Config).Code;
        var due = States.Where(s => s.Online).Select(MinedSymbolOf).Select(x => x.ToUpperInvariant()).Distinct()
            .Where(sym => !_coinQuotes.TryGetValue(sym, out var q) || q.Currency != cur || DateTime.UtcNow - q.AtUtc > TimeSpan.FromHours(1))
            .ToList();
        if (due.Count == 0) return;
        _coinQuotesBusy = true;
        _ = RefreshCoinQuotesAsync(due, cur);
    }

    private async Task RefreshCoinQuotesAsync(List<string> symbols, string cur)
    {
        try
        {
            foreach (var symbol in symbols)
            {
                var coin = CoinTypeExtensions.FromSymbolOrName(symbol);
                List<(DateTime Time, double Value)> chart = [];
                if (coin is { } c && c.HasPriceSource())
                    try { chart = await CoinGecko.GetDayChartAsync(c, cur); } catch { chart = []; }
                if (chart.Count >= 2)
                {
                    var first = chart[0].Value;
                    var last = chart[^1].Value;
                    var step = Math.Max(1, chart.Count / 24);
                    var spark = chart.Where((_, i) => i % step == 0).Select(p => p.Value).Append(last).ToList();
                    _coinQuotes[symbol] = (DateTime.UtcNow, cur, (decimal)last, first > 0 ? Math.Round((last / first - 1) * 100, 2) : null, "coingecko", spark);
                }
                else
                {
                    _coinQuotes[symbol] = (DateTime.UtcNow, cur, PoolPriceFor(symbol, cur), null, "pool", []);
                }
            }
        }
        finally
        {
            _coinQuotesBusy = false;
        }
    }

    internal void SetCoinQuoteForTest(string symbol, decimal price, double change) =>
        _coinQuotes[symbol] = (DateTime.UtcNow, Currencies.Of(Config).Code, price, change, "coingecko", [1, 2, 3]);
}
