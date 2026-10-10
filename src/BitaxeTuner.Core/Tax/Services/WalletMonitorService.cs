using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>
/// Pollt alle registrierten Adressen, erkennt neue bestätigte Zuflüsse und
/// dokumentiert sie mit dem EUR-Kurs nahe der Blockzeit. Fehlende Kurse werden
/// in späteren Durchläufen nachgeholt. Ein Durchlauf je Intervall, keine
/// Überlappung. Läuft auf einem Thread-Pool-Thread; UI-Anbindung über das
/// Ereignis <see cref="NewRewardDetected"/> muss selbst auf den UI-Thread wechseln.
/// </summary>
public sealed class WalletMonitorService : IDisposable
{
    private readonly IBlockchainService _blockchain;
    private readonly IPriceService _prices;
    private readonly TaxLogRepository _repository;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly object _walletLock = new();

    private Timer? _timer;
    private List<WalletAddress> _wallets;
    private readonly HashSet<string> _knownTxKeys;

    /// <summary>Höchstens so viele fehlende Kurse je Durchlauf nachholen (Rate-Limit).</summary>
    private const int PriceRetriesPerPoll = 3;

    public event Action<MinedReward>? NewRewardDetected;

    /// <summary>Ein vorhandener Eintrag wurde nachträglich ergänzt (z. B. Kurs nachgeholt).</summary>
    public event Action<MinedReward>? RewardUpdated;
    public event Action<string>? StatusChanged;

    /// <summary>0.9.12: Mehrere Einträge geändert (Pool-Konto: Tagesmenge nachgezogen, Art umgeschaltet) – Liste neu laden.</summary>
    public event Action? RewardsReloaded;

    public DateTime? LastPollUtc { get; private set; }

    /// <summary>
    /// 0.9.11: eingestellte Währung (ISO-Code). Der Euro-Kurs wird immer geholt (deutsches Steuerrecht, vorhandene Daten),
    /// bei einer anderen Währung zusätzlich deren Kurs.
    /// </summary>
    public Func<string> Currency { get; set; } = () => "EUR";

    private string CurrentCurrency()
    {
        try { return Config.Currencies.Get(Currency()).Code; }
        catch { return "EUR"; }
    }

    public WalletMonitorService(IBlockchainService blockchain, IPriceService prices, TaxLogRepository repository)
    {
        _blockchain = blockchain;
        _prices = prices;
        _repository = repository;
        _wallets = _repository.LoadWallets();
        _knownTxKeys = _repository.LoadKnownTxKeys();
    }

    /// <summary>Startet das zyklische Polling; erster Durchlauf nach 5 Sekunden.</summary>
    public void Start(TimeSpan interval)
    {
        RunningInterval = interval;
        _timer?.Dispose();
        var period = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval;
        _timer = new Timer(async _ => await PollOnceAsync(), null, TimeSpan.FromSeconds(5), period);
    }

    /// <summary>Zyklisches Polling anhalten (Pause, Datenübertragung).</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        RunningInterval = null;
    }

    /// <summary>Abstand des laufenden Pollings (null = angehalten).</summary>
    public TimeSpan? RunningInterval { get; private set; }

    // ---------- Wallets ----------

    public IReadOnlyList<WalletAddress> Wallets
    {
        get { lock (_walletLock) return _wallets.ToList(); }
    }

    public bool Contains(string address)
    {
        var norm = Normalize(address);
        lock (_walletLock) return _wallets.Any(w => Normalize(w.Address) == norm);
    }

    public bool AddWallet(WalletAddress wallet)
    {
        lock (_walletLock)
        {
            if (_wallets.Any(w => Normalize(w.Address) == Normalize(wallet.Address))) return false;
            _wallets.Add(wallet);
            _repository.SaveWallets(_wallets);
            return true;
        }
    }

    public void UpdateWallet(WalletAddress wallet)
    {
        lock (_walletLock) _repository.SaveWallets(_wallets);
    }

    public void RemoveWallet(string walletId)
    {
        lock (_walletLock)
        {
            _wallets = _wallets.Where(w => w.Id != walletId).ToList();
            _repository.SaveWallets(_wallets);
        }
    }

    /// <summary>Vergleich ohne bitcoincash:-Präfix und ohne Groß-/Kleinschreibung.</summary>
    public static string Normalize(string address)
    {
        var a = address.Trim();
        if (a.StartsWith("bitcoincash:", StringComparison.OrdinalIgnoreCase)) a = a["bitcoincash:".Length..];
        return a.ToLowerInvariant();
    }

    // ---------- Rewards ----------

    public List<MinedReward> LoadRewards() => _repository.LoadRewards();

    /// <summary>
    /// 0.9.12: Zuflüsse unter derselben Sperre wie die Wallet-Abfrage ändern (Pool-Konto). <paramref name="change"/> liefert
    /// true, wenn gespeichert werden soll.
    /// </summary>
    public void UpdateRewards(Func<List<MinedReward>, bool> change)
    {
        lock (_walletLock)
        {
            var all = _repository.LoadRewards();
            if (change(all)) _repository.SaveRewards(all);
        }
    }

    /// <summary>
    /// 0.9.12: Pool-Buchungen als Zuflüsse abgleichen (<see cref="PoolTaxSync"/>) und neue Einträge bewerten. Liefert die Zahl
    /// neuer Zuflüsse. Ohne Steuer-Übernahme (<paramref name="import"/> = false) wandern vorhandene Pool-Zuflüsse in den
    /// Speicher für die andere Art – sie gehen nicht verloren und kommen beim Wiedereinschalten zurück.
    /// </summary>
    /// <param name="fetchPrices">false: keine Kursabfrage (Tests ohne Netz) – fehlende Kurse holt ein späterer Durchlauf nach.</param>
    public Task<int> SyncPoolAsync(IReadOnlyList<PoolTransaction> ledger, PoolIncomeBasis basis, bool import, CancellationToken ct = default,
        bool fetchPrices = true) =>
        SyncPoolAsync(ledger, new PoolSyncOptions(basis, import, false, fetchPrices), ct);

    /// <summary>
    /// 0.9.13: wie oben, mit automatischem Umtausch in BTC (Umtausch-Verkäufe der Altcoins, BTC als Anschaffung) und
    /// Kursen vom Pool für Coins ohne CoinGecko-Kurs (<see cref="PoolSyncOptions.PoolPrice"/>, Kurs beim Abruf).
    /// </summary>
    public async Task<int> SyncPoolAsync(IReadOnlyList<PoolTransaction> ledger, PoolSyncOptions o, CancellationToken ct = default)
    {
        await _pollLock.WaitAsync(ct);
        try
        {
            List<MinedReward> added;
            List<Disposal> newSales = [];
            bool changed, salesChanged;
            lock (_walletLock)
            {
                var all = _repository.LoadRewards();
                var inactive = _repository.LoadPoolInactive();
                var disposals = _repository.LoadDisposals();
                if (o.Import)
                {
                    added = PoolTaxSync.Apply(all, inactive, PoolTaxSync.Derive(ledger, o.Basis, DateTime.UtcNow, o.AutoExchange), o.Basis,
                        _repository.LoadIgnoredTxKeys(), out changed);
                    newSales = PoolTaxSync.ApplySales(disposals, PoolTaxSync.DeriveSwapSales(ledger, o.Basis, o.AutoExchange), out salesChanged);
                }
                else
                {
                    added = [];
                    var parked = all.Where(PoolTaxSync.IsPoolReward).ToList();
                    foreach (var r in parked)
                    {
                        all.Remove(r);
                        inactive.RemoveAll(x => x.TxId == r.TxId);
                        inactive.Add(r);
                    }
                    changed = parked.Count > 0;
                    salesChanged = disposals.RemoveAll(PoolTaxSync.IsPoolSale) > 0;
                }
                // Coins ohne CoinGecko-Kurs (z. B. WJK): Kurs vom Pool beim Abruf, nie über einen von Hand eingetragenen
                if (o.PoolPrice is { } pool)
                    foreach (var r in all.Where(r => PoolTaxSync.IsPoolReward(r) && !r.Coin.HasPriceSource()))
                        foreach (var cur in new[] { "EUR", CurrentCurrency() }.Distinct())
                            if (r.PriceIn(cur) is null && !r.IsManualIn(cur) && pool(r.CoinSymbol, cur) is { } p)
                            {
                                r.SetPrice(cur, p, DateTime.UtcNow, L.T("Mining-Dutch (Kurs beim Abruf)"));
                                changed = true;
                            }
                if (changed)
                {
                    _repository.SavePoolInactive(inactive);
                    _repository.SaveRewards(all);
                    foreach (var r in added) _knownTxKeys.Add(TaxLogRepository.TxKey(r.CoinSymbol, r.TxId));
                }
                if (salesChanged) _repository.SaveDisposals(disposals);
            }
            if (added.Count > 0)
            {
                // Kurse für die neuesten Einträge gleich holen, ältere holt jeder Durchlauf nach (Rate-Limit)
                foreach (var r in added.Where(r => r.Coin.HasPriceSource() && r.PriceIn("EUR") is null)
                             .OrderByDescending(r => r.ReceivedAtUtc).Take(o.FetchPrices ? PriceRetriesPerPoll : 0))
                {
                    var (quote, failure) = await _prices.GetEurPriceAsync(r.Coin, r.ReceivedAtUtc, ct);
                    if (quote is not null) r.SetPrice("EUR", quote.Value, quote.AtUtc, quote.Source);
                    var currency = CurrentCurrency();
                    if (currency != "EUR" && (await _prices.GetPriceAsync(r.Coin, r.ReceivedAtUtc, currency, ct)).Quote is { } other)
                        r.SetPrice(currency, other.Value, other.AtUtc, other.Source);
                    if (failure is not null) r.Note += " · " + failure;
                    SaveReward(r);
                }
                foreach (var r in added.OrderBy(r => r.ReceivedAtUtc)) NewRewardDetected?.Invoke(r);
            }
            if (o.Import) await PriceSwapSalesAsync(o, ct);
            // Danach die ganze Liste neu (nachgezogene Mengen, umgeschaltete Art) – ersetzt auch die eben ergänzten Zeilen
            if (changed || salesChanged) RewardsReloaded?.Invoke();
            if (o.Import && o.FetchPrices) await RetryMissingPricesAsync(ct);
            return added.Count;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Erlös der Umtausch-Verkäufe in Euro (Kurs zum Zeitpunkt des Umtauschs; ohne CoinGecko-Kurs der Pool-Kurs beim Abruf).
    /// Ohne Kurs bleibt der Erlös 0 mit Hinweis, der nächste Durchlauf versucht es erneut.
    /// </summary>
    private async Task PriceSwapSalesAsync(PoolSyncOptions o, CancellationToken ct)
    {
        List<Disposal> open;
        lock (_walletLock) open = _repository.LoadDisposals().Where(d => PoolTaxSync.IsPoolSale(d) && d.ProceedsEur == 0).ToList();
        var priced = new List<Disposal>();
        foreach (var d in open.Take(PriceRetriesPerPoll))
        {
            decimal? price = null;
            var source = "";
            if (d.Coin.HasPriceSource() && o.FetchPrices)
            {
                var (quote, _) = await _prices.GetEurPriceAsync(d.Coin, d.SoldAtUtc, ct);
                price = quote?.Value;
                source = quote?.Source ?? "";
            }
            if (price is null && o.PoolPrice?.Invoke(d.CoinSymbol, "EUR") is { } p)
            {
                price = p;
                source = L.T("Mining-Dutch (Kurs beim Abruf)");
            }
            if (price is null) continue;
            d.ProceedsEur = Math.Round(d.Amount * price.Value, 2);
            d.Note = L.T("Mining-Dutch: automatischer Umtausch in BTC") + " · " + source;
            priced.Add(d);
        }
        if (priced.Count == 0) return;
        lock (_walletLock)
        {
            var all = _repository.LoadDisposals();
            foreach (var d in priced)
                if (all.FirstOrDefault(x => x.Id == d.Id) is { } x) { x.ProceedsEur = d.ProceedsEur; x.Note = d.Note; }
            _repository.SaveDisposals(all);
        }
    }

    /// <summary>Manuell geänderten Eintrag (Kurs/Notiz) sichern.</summary>
    public void SaveReward(MinedReward reward)
    {
        lock (_walletLock)
        {
            var all = _repository.LoadRewards();
            var idx = all.FindIndex(r => r.Id == reward.Id);
            if (idx >= 0) all[idx] = reward; else all.Add(reward);
            _repository.SaveRewards(all);
        }
    }

    /// <summary>
    /// Eintrag löschen und die TXID dauerhaft ignorieren, damit sie beim
    /// nächsten Durchlauf nicht wieder auftaucht (z. B. Eingang von einer Börse).
    /// </summary>
    public void RemoveReward(MinedReward reward)
    {
        lock (_walletLock)
        {
            var all = _repository.LoadRewards();
            all.RemoveAll(r => r.Id == reward.Id);
            _repository.SaveRewards(all);
            _repository.AddIgnored(reward.CoinSymbol, reward.TxId);
            _knownTxKeys.Add(TaxLogRepository.TxKey(reward.CoinSymbol, reward.TxId));
        }
    }

    // ---------- Polling ----------

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (!await _pollLock.WaitAsync(0, ct)) return;

        try
        {
            var wallets = Wallets;
            if (wallets.Count == 0)
            {
                StatusChanged?.Invoke(L.T("Keine Wallet-Adressen eingetragen."));
                return;
            }

            var found = 0;
            var errors = new List<string>();

            foreach (var wallet in wallets)
            {
                IReadOnlyList<IncomingTransaction> incoming;
                try
                {
                    incoming = await _blockchain.GetIncomingTransactionsAsync(wallet.Address, wallet.Coin, ct);
                }
                catch (Exception ex)
                {
                    errors.Add($"{wallet.Label}: {ex.Message}");
                    continue;
                }

                foreach (var tx in incoming.OrderBy(t => t.ReceivedAtUtc))
                {
                    var key = TaxLogRepository.TxKey(wallet.Coin, tx.TxId);
                    if (_knownTxKeys.Contains(key)) continue;

                    var (quote, failure) = await _prices.GetEurPriceAsync(wallet.Coin, tx.ReceivedAtUtc, ct);
                    var currency = CurrentCurrency();
                    var (other, otherFailure) = currency == "EUR" ? (null, null) : await _prices.GetPriceAsync(wallet.Coin, tx.ReceivedAtUtc, currency, ct);

                    var reward = new MinedReward
                    {
                        WalletAddressId = wallet.Id,
                        WalletLabel = wallet.Label,
                        Coin = wallet.Coin,
                        TxId = tx.TxId,
                        BlockHeight = tx.BlockId,
                        ReceivedAtUtc = tx.ReceivedAtUtc,
                        Amount = tx.Amount,
                        EurPriceAtReceipt = quote?.Value,
                        PriceAtUtc = quote?.AtUtc,
                        PriceSource = quote?.Source ?? string.Empty,
                        Note = failure ?? otherFailure ?? string.Empty
                    };
                    if (other is not null) reward.SetPrice(currency, other.Value, other.AtUtc, other.Source);

                    lock (_walletLock)
                    {
                        var all = _repository.LoadRewards();
                        all.Add(reward);
                        _repository.SaveRewards(all);
                        _knownTxKeys.Add(key);
                    }

                    found++;
                    NewRewardDetected?.Invoke(reward);
                }
            }

            var repaired = await RetryMissingPricesAsync(ct);

            LastPollUtc = DateTime.UtcNow;
            var text = L.T("Geprüft {0:HH:mm} · {1} Adresse(n) · {2} neu", DateTime.Now, wallets.Count, found);
            if (repaired > 0) text += L.T(" · {0} Kurs(e) nachgeholt", repaired);
            if (errors.Count > 0) text += L.T(" · Fehler: ") + string.Join("; ", errors);
            StatusChanged?.Invoke(text);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(L.T("Fehler: ") + ex.Message);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Einträge ohne Kurs erneut bewerten, z. B. nach einem Netzwerkfehler oder (0.9.11) nach dem Wechsel der Währung.
    /// Manuell eingetragene Kurse werden nie angefasst.
    /// </summary>
    private async Task<int> RetryMissingPricesAsync(CancellationToken ct)
    {
        var currencies = new[] { "EUR", CurrentCurrency() }.Distinct().ToList();
        List<(MinedReward Reward, string Currency)> pending;
        lock (_walletLock)
        {
            pending = _repository.LoadRewards()
                .Where(r => r.Coin.HasPriceSource() && DateTime.UtcNow - r.ReceivedAtUtc < TimeSpan.FromDays(364))
                .OrderByDescending(r => r.ReceivedAtUtc)
                .SelectMany(r => currencies.Where(c => r.PriceIn(c) is null && !r.IsManualIn(c)).Select(c => (r, c)))
                .Take(PriceRetriesPerPoll)
                .ToList();
        }

        var repaired = 0;
        foreach (var (reward, currency) in pending)
        {
            var (quote, _) = await _prices.GetPriceAsync(reward.Coin, reward.ReceivedAtUtc, currency, ct);
            if (quote is null) continue;

            reward.SetPrice(currency, quote.Value, quote.AtUtc, quote.Source);
            reward.Note = L.T("Kurs nachgeholt am {0:g}", DateTime.Now);
            SaveReward(reward);

            repaired++;
            RewardUpdated?.Invoke(reward);
        }

        return repaired;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _pollLock.Dispose();
    }
}

/// <summary>0.9.13: Optionen für den Abgleich der Pool-Buchungen.</summary>
/// <param name="PoolPrice">Aktueller Kurs vom Pool je Kürzel und Währung (für Coins ohne CoinGecko-Kurs), null = keiner.</param>
public sealed record PoolSyncOptions(PoolIncomeBasis Basis, bool Import, bool AutoExchange, bool FetchPrices = true,
    Func<string, string, decimal?>? PoolPrice = null);
