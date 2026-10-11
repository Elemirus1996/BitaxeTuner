using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Ertrag, Stromkosten und Saldo eines Zeitraums. Income null = kein Ertrag bekannt; Partial = Kurs fehlte für einen Teil.</summary>
public sealed record ProfitWindow(string Key, decimal? Income, decimal Cost, decimal? Net, double Kwh, bool Partial);

/// <summary>Wirtschaftlichkeit: Quelle "pool" (Gutschriften beim Pool, Kurs beim Abruf) oder "tax" (dokumentierte Zuflüsse).</summary>
public sealed record ProfitSummary(DateTime AtUtc, string Currency, string Source, IReadOnlyList<ProfitWindow> Windows)
{
    public ProfitWindow? Window(string key) => Windows.FirstOrDefault(w => w.Key == key);
}

/// <summary>
/// 0.9.14: Ertrag gegen Stromkosten für 24 h, 7 Tage, 30 Tage und den laufenden Monat. Mit Pool-Konto zählen die
/// Mining-Gutschriften im Pool-Buch (bewertet mit dem aktuellen Kurs – eine Schätzung), sonst die dokumentierten Zuflüsse
/// der Steuer (ohne Tausch). Stromkosten wie in Berichten (<see cref="Plugs.EnergyCost.Compute"/>). Höchstens alle 5 min neu.
/// </summary>
public sealed partial class MinerHub
{
    private ProfitSummary? _profit;

    public static readonly string[] ProfitWindows = ["24h", "7d", "30d", "month"];

    public ProfitSummary? Profit(DateTime now)
    {
        if (History is null) return null;
        var cur = Currencies.Of(Config).Code;
        if (_profit is { } cached && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromMinutes(5) && cached.Currency == cur) return cached;
        try
        {
            var hosts = Devices.Select(d => d.Host).ToList();
            List<PoolTransaction> ledger;
            try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
            var pool = Config.PoolAccount.Enabled && ledger.Count > 0;
            List<MinedReward> rewards = [];
            if (!pool) try { rewards = TaxRepository.LoadIncome(); } catch { rewards = []; }
            var windows = new List<ProfitWindow>();
            foreach (var key in ProfitWindows)
            {
                var from = key switch
                {
                    "24h" => now.AddHours(-24),
                    "7d" => now.AddDays(-7),
                    "30d" => now.AddDays(-30),
                    _ => new DateTime(now.Year, now.Month, 1),
                };
                var energy = Plugs.EnergyCost.Compute(History, Config, hosts, from, now);
                var cost = (decimal)energy.Cost;
                var (income, partial) = pool ? PoolIncome(ledger, from.ToUniversalTime(), now.ToUniversalTime(), cur) : TaxIncome(rewards, from.ToUniversalTime(), now.ToUniversalTime(), cur);
                windows.Add(new ProfitWindow(key, income, Math.Round(cost, 2), income is { } i ? Math.Round(i - cost, 2) : null, Math.Round(energy.Kwh, 2), partial));
            }
            return _profit = new ProfitSummary(DateTime.UtcNow, cur, pool ? "pool" : "tax", windows);
        }
        catch
        {
            return null;   // Kennzahl ist nur Zusatz – nie die Übersicht stören
        }
    }

    /// <summary>Zeile für den Tagesbericht: Ertrag, Strom, Saldo der letzten 24 h (null ohne bekannten Ertrag).</summary>
    public string? ProfitLine(DateTime now)
    {
        if (Profit(now)?.Window("24h") is not { Income: { } income, Net: { } net } w) return null;
        var sym = Currencies.Of(Config).Symbol;
        var c = I18n.L.Culture;
        return I18n.L.T("Ertrag {0} {3} − Strom {1} {3} = {2} {3}{4}", income.ToString("N2", c), w.Cost.ToString("N2", c), net.ToString("+0.00;-0.00", c), sym,
            w.Partial ? I18n.L.T(" (ohne Coins ohne Kurs)") : "");
    }

    /// <summary>Mining-Gutschriften (ohne Umtausch-BTC) × aktueller Kurs je Coin; Coins ohne Kurs machen das Ergebnis „teilweise“.</summary>
    private (decimal? Income, bool Partial) PoolIncome(List<PoolTransaction> ledger, DateTime fromUtc, DateTime toUtc, string cur)
    {
        var credits = ledger.Where(t => t.IsCredit && (t.Coin != CoinType.Bitcoin || t.FromShares != false) && t.TimeUtc >= fromUtc && t.TimeUtc <= toUtc);
        decimal sum = 0;
        var partial = false;
        var any = false;
        foreach (var g in credits.GroupBy(t => t.Symbol))
        {
            any = true;
            var price = _poolPrices.TryGetValue(g.Key, out var p) && p.Currency == cur && p.Price is { } x ? x : PoolPriceFor(g.Key, cur);
            if (price is { } pr) sum += g.Sum(t => t.Amount) * pr;
            else partial = true;
        }
        return (any ? Math.Round(sum, 2) : 0, partial);
    }

    private static (decimal? Income, bool Partial) TaxIncome(List<MinedReward> rewards, DateTime fromUtc, DateTime toUtc, string cur)
    {
        var inWindow = rewards.Where(r => r.ReceivedAtUtc >= fromUtc && r.ReceivedAtUtc <= toUtc).ToList();
        var values = inWindow.Select(r => r.ValueIn(cur)).ToList();
        return (Math.Round(values.Sum(v => v ?? 0), 2), values.Any(v => v is null));
    }
}
