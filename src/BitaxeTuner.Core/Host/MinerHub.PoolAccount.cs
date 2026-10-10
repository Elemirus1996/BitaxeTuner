using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Zusammenfassung je Coin beim Pool-Konto (Guthaben, Gutschriften, Auszahlungen, Kurs).</summary>
public sealed record PoolCoinSummary(CoinType Coin, decimal Confirmed, decimal Unconfirmed, decimal Credits24h, decimal Credits7d,
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
    private readonly Dictionary<CoinType, (DateTime At, decimal? Price, string Currency)> _poolPrices = [];

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
            var coins = MiningDutchClient.Slugs.Keys
                .Select(c =>
                {
                    var own = ledger.Where(t => t.Coin == c).ToList();
                    var bal = _poolBalances.FirstOrDefault(b => b.Coin == c);
                    var credits = own.Where(t => t.IsCredit).ToList();
                    var payouts = own.Where(t => t.IsPayout).ToList();
                    return new PoolCoinSummary(c, bal?.Confirmed ?? 0, bal?.Unconfirmed ?? 0,
                        credits.Where(t => now - t.TimeUtc <= TimeSpan.FromHours(24)).Sum(t => t.Amount),
                        credits.Where(t => now - t.TimeUtc <= TimeSpan.FromDays(7)).Sum(t => t.Amount),
                        payouts.Where(t => now - t.TimeUtc <= TimeSpan.FromDays(30)).Sum(t => t.Amount),
                        credits.Count > 0 ? credits.Max(t => t.TimeUtc) : null,
                        payouts.Count > 0 ? payouts.Max(t => t.TimeUtc) : null,
                        _poolPrices.TryGetValue(c, out var p) && p.Currency == cur ? p.Price : null);
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

    /// <summary>Coin, den der Pool diesem Miner gerade gibt (null = kein Pool-Konto-Miner oder unbekannt).</summary>
    public CoinType? PoolCoinOf(MinerState s) => PoolWorkerOf(s) is { } w ? CoinTypeExtensions.FromSymbolOrName(w.NowMining) : null;

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
            foreach (var coin in MiningDutchClient.Slugs.Keys)
            {
                try
                {
                    balances.Add(await client.GetBalanceAsync(coin));
                    fresh.AddRange(await client.GetTransactionsAsync(coin));
                }
                catch (LocalizedException ex) when (ex.Text != MiningDutchClient.KeyRejected)
                {
                    coinErrors.Add($"{coin.Symbol()}: {L.T(ex.Text, ex.Args)}");
                }
            }
            _poolBalances = balances;

            // Pool-Buch ergänzen: der Pool liefert nur die jüngsten Buchungen – alles einmal Gesehene bleibt erhalten
            var ledger = TaxRepository.LoadPoolLedger();
            var known = ledger.Select(t => (t.Coin, t.Id)).ToHashSet();
            var added = fresh.Where(t => known.Add((t.Coin, t.Id))).ToList();
            if (added.Count > 0)
            {
                ledger.AddRange(added);
                TaxRepository.SavePoolLedger(ledger.OrderBy(t => t.TimeUtc));
            }

            var newRewards = await TaxMonitor.SyncPoolAsync(ledger, cfg.TaxBasis, cfg.TaxImport, fetchPrices: Options.OnlineChecks);
            _poolNewRewards += newRewards;
            await RefreshPoolPricesAsync(balances.Where(b => b.Confirmed + b.Unconfirmed > 0).Select(b => b.Coin));

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
            if (ledger.Count > 0) await TaxMonitor.SyncPoolAsync(ledger, cfg.TaxBasis, cfg.TaxImport, fetchPrices: Options.OnlineChecks);
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
            if (_poolPrices.TryGetValue(coin, out var p) && p.Currency == cur && DateTime.UtcNow - p.At < TimeSpan.FromHours(1)) continue;
            try
            {
                var (quote, _) = await CoinGecko.GetPriceAsync(coin, DateTime.UtcNow, cur);
                _poolPrices[coin] = (DateTime.UtcNow, quote?.Value, cur);
            }
            catch { /* ohne Kurs nur die Menge */ }
        }
    }
}
