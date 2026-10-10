using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Core.Host;

/// <summary>Zusammenfassung je Coin beim Pool-Konto (Guthaben, Gutschriften, Auszahlungen, Kurs). 0.9.13: je Kürzel.</summary>
public sealed record PoolCoinSummary(string Symbol, CoinType Coin, decimal Confirmed, decimal Unconfirmed, decimal Credits24h, decimal Credits7d,
    decimal Payouts30d, DateTime? LastCreditUtc, DateTime? LastPayoutUtc, decimal? Price);

/// <summary>Stand des Pool-Kontos für Browser und Desktop.</summary>
public sealed record PoolAccountState(bool Enabled, bool HasKey, bool Busy, DateTime? LastPollUtc, string? Error,
    IReadOnlyList<PoolWorker> Workers, IReadOnlyList<PoolCoinSummary> Coins, string Currency, int NewRewards);

/// <summary>
/// 0.9.12: Pool-Konto bei Mining-Dutch. Fragt im eingestellten Abstand Worker, Guthaben und Buchungen ab, merkt sich alle
/// Buchungen im Pool-Buch (pool-ledger.json, nur ergänzend) und übernimmt sie als Zuflüsse in die Steuer
/// (<see cref="PoolTaxSync"/>). Der Coin je Miner kommt aus dem Worker gleichen Namens (Teil hinter dem Punkt im Pool-Benutzer).
/// Nur lesend – am Pool wird nichts umgestellt.
/// </summary>
public sealed partial class MinerHub
{
    private MiningDutchClient? _poolClient;
    private string _poolClientKey = "";
    private volatile bool _poolBusy;
    private DateTime? _poolLastPoll;
    private string? _poolError;
    private int _poolNewRewards;
    private volatile IReadOnlyList<PoolWorker> _poolWorkers = [];
    private volatile IReadOnlyList<PoolBalance> _poolBalances = [];
    private readonly Dictionary<string, (DateTime At, decimal? Price, string Currency)> _poolPrices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>0.9.13: Kürzel und Kurs-Rohwert je Coin-Pool (Slug), aus dem Dashboard; höchstens alle 6 h neu.</summary>
    private readonly Dictionary<string, (DateTime At, PoolCoinInfo Info)> _poolCoinInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Währung je US-Dollar (Umrechnung des Pool-Kurses, der in USD kommt); höchstens stündlich.</summary>
    private readonly Dictionary<string, (DateTime At, decimal Rate)> _fiatPerUsd = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kurs vom Pool für Coins ohne CoinGecko-Kurs: „exchangePrice“ aus dem Dashboard ist ein US-Dollar-Kurs (geprüft an
    /// DGB: 0,00351 laut Pool, 0,00358 $ laut CoinGecko); 0 = der Pool kennt keinen Kurs.
    /// </summary>
    internal decimal? PoolPriceFor(string symbol, string currency)
    {
        var usd = _poolCoinInfo.Values.Select(v => v.Info).FirstOrDefault(i => string.Equals(i.Currency, symbol, StringComparison.OrdinalIgnoreCase))?.ExchangePrice;
        if (usd is not > 0) return null;
        if (currency.Equals("USD", StringComparison.OrdinalIgnoreCase)) return usd;
        return _fiatPerUsd.TryGetValue(currency, out var r) ? usd * r.Rate : null;
    }

    /// <summary>Umrechnung USD → Währung über den Bitcoin-Kurs in beiden Währungen (eine Quelle, keine weitere API).</summary>
    private async Task RefreshFiatPerUsdAsync(IEnumerable<string> currencies)
    {
        if (!Options.OnlineChecks) return;
        decimal? btcUsd = null;
        foreach (var cur in currencies.Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !c.Equals("USD", StringComparison.OrdinalIgnoreCase)))
        {
            if (_fiatPerUsd.TryGetValue(cur, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromHours(1)) continue;
            try
            {
                btcUsd ??= (await CoinGecko.GetPriceAsync(CoinType.Bitcoin, DateTime.UtcNow, "USD")).Quote?.Value;
                var btcCur = (await CoinGecko.GetPriceAsync(CoinType.Bitcoin, DateTime.UtcNow, cur)).Quote?.Value;
                if (btcUsd is > 0 && btcCur is > 0) _fiatPerUsd[cur] = (DateTime.UtcNow, btcCur.Value / btcUsd.Value);
            }
            catch { /* ohne Umrechnung kein Pool-Kurs, nächster Versuch beim nächsten Abruf */ }
        }
    }

    private PoolSyncOptions PoolSync(PoolAccountSettings cfg) =>
        new(cfg.TaxBasis, cfg.TaxImport, cfg.AutoExchange, Options.OnlineChecks, PoolPriceFor);

    /// <summary>Pools, die keine Coins sind (Profit-Wechsel nach Algorithmus).</summary>
    private static readonly HashSet<string> AlgoPools = new(StringComparer.OrdinalIgnoreCase) { "sha256", "sha256d", "" };

    /// <summary>Coin zu einem Pool-Namen: feste Liste, sonst <see cref="CoinType.Other"/> mit dem Kürzel laut Dashboard.</summary>
    private (CoinType Coin, string? Code) CoinOfSlug(string slug)
    {
        foreach (var (c, s) in MiningDutchClient.Slugs)
            if (string.Equals(s, slug, StringComparison.OrdinalIgnoreCase)) return (c, null);
        if (CoinTypeExtensions.FromSymbolOrName(slug) is { } known) return (known, null);
        return (CoinType.Other, _poolCoinInfo.TryGetValue(slug, out var i) ? i.Info.Currency : slug.ToUpperInvariant());
    }

    /// <summary>Kürzel eines Pool-Namens (z. B. „wojakcoin“ → „WJK“).</summary>
    public string PoolSymbolOfSlug(string slug)
    {
        var (coin, code) = CoinOfSlug(slug.Trim());
        return coin.Symbol(code);
    }

    /// <summary>Für Tests: eigener HTTP-Handler und Abstand zwischen den Anfragen.</summary>
    internal Func<string, MiningDutchClient>? PoolClientFactory { get; set; }

    public event Action? PoolAccountUpdated;

    public PoolAccountState PoolAccount
    {
        get
        {
            var cfg = Config.PoolAccount;
            var cur = Currencies.Of(Config).Code;
            List<PoolTransaction> ledger;
            try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
            var now = DateTime.UtcNow;
            var balances = _poolBalances;
            var symbols = MiningDutchClient.Slugs.Keys.Select(c => (Symbol: c.Symbol(), Coin: c))
                .Concat(balances.Select(b => (b.Symbol, b.Coin)))
                .Concat(ledger.Select(t => (t.Symbol, t.Coin)))
                .DistinctBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var coins = symbols
                .Select(x =>
                {
                    var own = ledger.Where(t => string.Equals(t.Symbol, x.Symbol, StringComparison.OrdinalIgnoreCase)).ToList();
                    var bal = balances.FirstOrDefault(b => string.Equals(b.Symbol, x.Symbol, StringComparison.OrdinalIgnoreCase));
                    var credits = own.Where(t => t.IsCredit).ToList();
                    var payouts = own.Where(t => t.IsPayout).ToList();
                    return new PoolCoinSummary(x.Symbol, x.Coin, bal?.Confirmed ?? 0, bal?.Unconfirmed ?? 0,
                        credits.Where(t => now - t.TimeUtc <= TimeSpan.FromHours(24)).Sum(t => t.Amount),
                        credits.Where(t => now - t.TimeUtc <= TimeSpan.FromDays(7)).Sum(t => t.Amount),
                        payouts.Where(t => now - t.TimeUtc <= TimeSpan.FromDays(30)).Sum(t => t.Amount),
                        credits.Count > 0 ? credits.Max(t => t.TimeUtc) : null,
                        payouts.Count > 0 ? payouts.Max(t => t.TimeUtc) : null,
                        _poolPrices.TryGetValue(x.Symbol, out var p) && p.Currency == cur ? p.Price : PoolPriceFor(x.Symbol, cur));
                })
                // Nur Coins mit Guthaben oder Buchungen, Bitcoin immer
                .Where(s => s.Coin == CoinType.Bitcoin || s.Confirmed + s.Unconfirmed > 0 || s.LastCreditUtc is not null || s.LastPayoutUtc is not null)
                .ToList();
            return new PoolAccountState(cfg.Enabled, cfg.ApiKey.Length > 0, _poolBusy, _poolLastPoll, _poolError, _poolWorkers, coins, cur,
                _poolNewRewards);
        }
    }

    /// <summary>Worker beim Pool-Konto für diesen Miner (gleicher Name, Pool-Adresse bei Mining-Dutch); sonst null.</summary>
    public PoolWorker? PoolWorkerOf(MinerState s)
    {
        var workers = _poolWorkers;
        if (workers.Count == 0 || s.Info is not { } i) return null;
        var fallback = i.isUsingFallbackStratum != 0;
        var url = (fallback ? i.fallbackStratumURL : i.stratumURL) ?? "";
        var user = (fallback ? i.fallbackStratumUser : i.stratumUser) ?? "";
        if (!url.Contains("mining-dutch", StringComparison.OrdinalIgnoreCase)) return null;
        var dot = user.IndexOf('.');
        if (dot < 0) return null;
        var name = user[(dot + 1)..].Trim();
        return workers.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Coin, den der Pool diesem Miner gerade gibt (null = kein Pool-Konto-Miner; <see cref="CoinType.Other"/> = weiterer Coin).</summary>
    public CoinType? PoolCoinOf(MinerState s) => PoolWorkerOf(s) is { } w && !AlgoPools.Contains(w.NowMining) ? CoinOfSlug(w.NowMining).Coin : null;

    /// <summary>Kürzel des Coins, den der Pool diesem Miner gerade gibt (auch weitere Coins wie „WJK“).</summary>
    public string? PoolCoinSymbolOf(MinerState s) => PoolWorkerOf(s) is { } w && !AlgoPools.Contains(w.NowMining) ? PoolSymbolOfSlug(w.NowMining) : null;

    /// <summary>Pool-Konto einmal abfragen (Takt und „Jetzt abfragen“). Liefert eine Fehlermeldung oder null.</summary>
    public async Task<string?> PoolAccountTickAsync()
    {
        var cfg = Config.PoolAccount;
        if (!cfg.Enabled) return L.T("Pool-Konto ist ausgeschaltet.");
        if (cfg.ApiKey.Length == 0) return L.T("Kein API-Schlüssel eingetragen.");
        if ((!Options.OnlineChecks && PoolClientFactory is null) || _paused || _poolBusy) return null;

        _poolBusy = true;
        PoolAccountUpdated?.Invoke();
        var hadError = _poolError;
        try
        {
            var client = PoolClient(cfg.ApiKey);
            var workers = await client.GetWorkersAsync();
            _poolWorkers = workers;

            var balances = new List<PoolBalance>();
            var fresh = new List<PoolTransaction>();
            var coinErrors = new List<string>();
            var ledger = TaxRepository.LoadPoolLedger();
            // 0.9.13: feste Coins + was die Worker gerade schürfen (Profit-Wechsel, z. B. wojakcoin) + was schon im Pool-Buch steht
            var slugs = MiningDutchClient.Slugs.Values
                .Concat(workers.Select(w => w.NowMining.Trim().ToLowerInvariant()))
                .Concat(ledger.Select(t => t.Slug ?? ""))
                .Where(s => s.Length > 0 && !AlgoPools.Contains(s) && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var slug in slugs)
            {
                try
                {
                    var (coin, _) = CoinOfSlug(slug);
                    if (coin == CoinType.Other && (!_poolCoinInfo.TryGetValue(slug, out var ci) || DateTime.UtcNow - ci.At > TimeSpan.FromHours(6)))
                        _poolCoinInfo[slug] = (DateTime.UtcNow, await client.GetCoinInfoAsync(slug));
                    var (c, code) = CoinOfSlug(slug);
                    balances.Add(await client.GetBalanceAsync(slug, c, code));
                    fresh.AddRange(await client.GetTransactionsAsync(slug, c, code));
                }
                catch (LocalizedException ex) when (ex.Text != MiningDutchClient.KeyRejected)
                {
                    coinErrors.Add($"{PoolSymbolOfSlug(slug)}: {L.T(ex.Text, ex.Args)}");
                }
            }
            _poolBalances = balances;

            // Pool-Buch ergänzen: der Pool liefert nur die jüngsten Buchungen – alles einmal Gesehene bleibt erhalten
            var known = ledger.Select(t => (t.Symbol, t.Id)).ToHashSet();
            var added = fresh.Where(t => known.Add((t.Symbol, t.Id))).ToList();
            // 0.9.13: ältere Einträge ohne Shares-Angabe ergänzen, sobald der Pool sie noch einmal liefert
            var byKey = fresh.GroupBy(t => (t.Symbol, t.Id)).ToDictionary(g => g.Key, g => g.First());
            var upgraded = 0;
            for (var n = 0; n < ledger.Count; n++)
                if (ledger[n].FromShares is null && byKey.TryGetValue((ledger[n].Symbol, ledger[n].Id), out var f) && f.FromShares is not null)
                {
                    ledger[n] = ledger[n] with { FromShares = f.FromShares, Slug = ledger[n].Slug ?? f.Slug };
                    upgraded++;
                }
            if (added.Count > 0 || upgraded > 0)
            {
                ledger.AddRange(added);
                TaxRepository.SavePoolLedger(ledger.OrderBy(t => t.TimeUtc));
            }

            if (balances.Any(b => b.Coin == CoinType.Other) || ledger.Any(t => t.Coin == CoinType.Other))
                await RefreshFiatPerUsdAsync(["EUR", Currencies.Of(Config).Code]);
            var newRewards = await TaxMonitor.SyncPoolAsync(ledger, PoolSync(cfg));
            _poolNewRewards += newRewards;
            await RefreshPoolPricesAsync(balances.Where(b => b.Confirmed + b.Unconfirmed > 0 && b.Coin.HasPriceSource()).Select(b => b.Coin));

            _poolLastPoll = DateTime.UtcNow;
            _poolError = coinErrors.Count > 0 ? string.Join("; ", coinErrors) : null;
            if (hadError is not null && _poolError is null)
                LogEvent(null, EventCategories.Connection, L.T("Pool-Konto wieder erreichbar."));
            if (added.Count > 0 || newRewards > 0)
                LogEvent(null, EventCategories.Other, L.T("Pool-Konto: {0} neue Buchung(en), {1} neue(r) Zufluss/Zuflüsse für die Steuer.", added.Count, newRewards));
            return _poolError;
        }
        catch (Exception ex)
        {
            _poolError = ex is LocalizedException le ? L.T(le.Text, le.Args) : MinerPollingService.Shorten(ex);
            if (_poolError != hadError) LogEvent(null, EventCategories.Connection, L.T("Pool-Konto: {0}", _poolError));
            return _poolError;
        }
        finally
        {
            _poolBusy = false;
            PoolAccountUpdated?.Invoke();
        }
    }

    /// <summary>Nach dem Speichern der Einstellungen: Steuer-Art/-Übernahme sofort anwenden, neuen Schlüssel gleich prüfen.</summary>
    public async Task ApplyPoolAccountSettingsAsync()
    {
        var cfg = Config.PoolAccount;
        try
        {
            var ledger = TaxRepository.LoadPoolLedger();
            if (ledger.Count > 0) await TaxMonitor.SyncPoolAsync(ledger, PoolSync(cfg));
        }
        catch (Exception ex)
        {
            RaiseStatus(false, L.T("Pool-Konto: {0}", ex.Message));
        }
        if (cfg.Enabled && cfg.ApiKey.Length > 0 && (cfg.ApiKey != _poolClientKey || _poolLastPoll is null)) _ = PoolAccountTickAsync();
        if (!cfg.Enabled)
        {
            _poolWorkers = [];
            _poolBalances = [];
            _poolError = null;
        }
    }

    private MiningDutchClient PoolClient(string key)
    {
        if (_poolClient is null || _poolClientKey != key)
        {
            _poolClient?.Dispose();
            _poolClient = PoolClientFactory?.Invoke(key) ?? new MiningDutchClient(key);
            _poolClientKey = key;
        }
        return _poolClient;
    }

    /// <summary>Aktueller Kurs je Coin mit Guthaben (höchstens stündlich je Coin, für den Wert in der Übersicht).</summary>
    private async Task RefreshPoolPricesAsync(IEnumerable<CoinType> coins)
    {
        if (!Options.OnlineChecks) return;
        var cur = Currencies.Of(Config).Code;
        foreach (var coin in coins)
        {
            if (_poolPrices.TryGetValue(coin.Symbol(), out var p) && p.Currency == cur && p.Price is not null && DateTime.UtcNow - p.At < TimeSpan.FromHours(1)) continue;
            try
            {
                var (quote, _) = await CoinGecko.GetPriceAsync(coin, DateTime.UtcNow, cur);
                // Ohne Kurs (z. B. CoinGecko-Sperre) beim nächsten Durchlauf erneut versuchen
                _poolPrices[coin.Symbol()] = (DateTime.UtcNow, quote?.Value, cur);
            }
            catch { /* ohne Kurs nur die Menge */ }
        }
    }
}
