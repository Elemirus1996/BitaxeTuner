using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Kurs eines Coins, den mindestens ein Miner gerade schürft.</summary>
/// <param name="Change24hPercent">Veränderung gegenüber vor 24 Stunden in % (null = unbekannt, z. B. Kurs laut Pool).</param>
/// <param name="Source">„coingecko“ oder „pool“ (Kurs, den der Pool meldet).</param>
/// <param name="Miners">Wie viele Miner diesen Coin gerade schürfen.</param>
/// <param name="Spark">Verlauf der letzten 24 Stunden (bis zu 24 Punkte, leer = unbekannt).</param>
/// <param name="Kind">0.9.16: "mined" (Miner schürfen ihn), "merged" (Merged Mining laut Pool-Konto), "watch" (frei gewählt).</param>
public sealed record CoinQuote(string Symbol, decimal? Price, double? Change24hPercent, string Source, int Miners, IReadOnlyList<double> Spark,
    string Kind = "mined");

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
        list = list.OrderByDescending(c => c.Miners).ThenBy(c => c.Symbol).ToList();
        // 0.9.16: Merged-Mining-Coins (Gutschriften der letzten 7 Tage) und frei gewählte Kurse dahinter
        foreach (var (symbol, kind) in ExtraQuoteSymbols())
        {
            if (list.Any(c => c.Symbol == symbol)) continue;
            if (_coinQuotes.TryGetValue(symbol, out var q) && q.Currency == cur)
                list.Add(new CoinQuote(symbol, q.Price, q.Change, q.Source, 0, q.Spark, kind));
            else if (kind == "merged" || PoolPriceFor(symbol, cur) is not null)
                list.Add(new CoinQuote(symbol, PoolPriceFor(symbol, cur), null, "pool", 0, [], kind));
        }
        return list;
    }

    private IReadOnlyList<string> _mergedSymbols = [];

    private IEnumerable<(string Symbol, string Kind)> ExtraQuoteSymbols() =>
        _mergedSymbols.Select(s => (s, "merged"))
            .Concat(Config.WatchCoins.Select(e => Network.CoinCatalog.Parse(e)).OfType<(string Symbol, string Id)>().Select(p => (p.Symbol, "watch")));

    /// <summary>0.9.16: weitere Kurse speichern (ungültige Einträge werden gemeldet, nichts gespeichert).</summary>
    public void SetWatchCoins(IReadOnlyList<string> entries, string by)
    {
        var list = entries.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (list.FirstOrDefault(e => Network.CoinCatalog.Parse(e) is null) is { } bad)
            throw new LocalizedException("„{0}“ ist kein bekannter Coin – als „KÜRZEL=coingecko-id“ eintragen (die ID steht in der Adresse der CoinGecko-Seite).", bad);
        Config.WatchCoins = list;
        Config.Save();
        _coinQuotesChecked = DateTime.MinValue;   // gleich holen
        LogEvent(null, EventCategories.Settings, L.T("Weitere Kurse gespeichert: {0} – {1}", list.Count == 0 ? "–" : string.Join(", ", list), by));
    }

    /// <summary>Aus dem Abfragetakt: höchstens alle 10 min prüfen, je Coin höchstens stündlich bei CoinGecko nachfragen.</summary>
    private void RefreshCoinQuotes(DateTime now)
    {
        if (!Options.OnlineChecks || _coinQuotesBusy || now - _coinQuotesChecked < TimeSpan.FromMinutes(10)) return;
        _coinQuotesChecked = now;
        var cur = Currencies.Of(Config).Code;
        // Merged-Mining-Coins laut Pool-Konto (Gutschriften der letzten 7 Tage) – hier und nicht bei jeder Anzeige
        if (Config.PoolAccount.Enabled)
            try { _mergedSymbols = PoolAccount.Coins.Where(c => c.Credits7d > 0).Select(c => c.Symbol.ToUpperInvariant()).ToList(); }
            catch { /* ohne Merged-Mining-Coins */ }
        else _mergedSymbols = [];
        var due = States.Where(s => s.Online).Select(MinedSymbolOf).Select(x => x.ToUpperInvariant())
            .Concat(ExtraQuoteSymbols().Select(x => x.Symbol)).Distinct()
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
                else if (Network.CoinCatalog.IdFor(symbol, Config.WatchCoins) is { } geckoId)   // 0.9.16: weitere Coins
                    try { chart = await CoinGecko.GetDayChartAsync(geckoId, cur); } catch { chart = []; }
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
