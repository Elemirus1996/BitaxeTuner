using BitaxeTuner.Core.Tax.Models;

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

    public DateTime? LastPollUtc { get; private set; }

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
        _timer?.Dispose();
        var period = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval;
        _timer = new Timer(async _ => await PollOnceAsync(), null, TimeSpan.FromSeconds(5), period);
    }

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
            _repository.AddIgnored(reward.Coin, reward.TxId);
            _knownTxKeys.Add(TaxLogRepository.TxKey(reward.Coin, reward.TxId));
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
                StatusChanged?.Invoke("Keine Wallet-Adressen eingetragen.");
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

                    var reward = new MinedReward
                    {
                        WalletAddressId = wallet.Id,
                        WalletLabel = wallet.Label,
                        Coin = wallet.Coin,
                        TxId = tx.TxId,
                        BlockHeight = tx.BlockId,
                        ReceivedAtUtc = tx.ReceivedAtUtc,
                        Amount = tx.Amount,
                        EurPriceAtReceipt = quote?.Eur,
                        PriceAtUtc = quote?.AtUtc,
                        PriceSource = quote?.Source ?? string.Empty,
                        Note = failure ?? string.Empty
                    };

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
            var text = $"Geprüft {DateTime.Now:HH:mm} · {wallets.Count} Adresse(n) · {found} neu";
            if (repaired > 0) text += $" · {repaired} Kurs(e) nachgeholt";
            if (errors.Count > 0) text += " · Fehler: " + string.Join("; ", errors);
            StatusChanged?.Invoke(text);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Fehler: " + ex.Message);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Einträge ohne Kurs erneut bewerten, z. B. nach einem Netzwerkfehler.
    /// Manuell eingetragene Kurse werden nie angefasst.
    /// </summary>
    private async Task<int> RetryMissingPricesAsync(CancellationToken ct)
    {
        List<MinedReward> pending;
        lock (_walletLock)
        {
            pending = _repository.LoadRewards()
                .Where(r => r.EurPriceAtReceipt is null && !r.IsManualPrice
                            && DateTime.UtcNow - r.ReceivedAtUtc < TimeSpan.FromDays(364))
                .OrderByDescending(r => r.ReceivedAtUtc)
                .Take(PriceRetriesPerPoll)
                .ToList();
        }

        var repaired = 0;
        foreach (var reward in pending)
        {
            var (quote, _) = await _prices.GetEurPriceAsync(reward.Coin, reward.ReceivedAtUtc, ct);
            if (quote is null) continue;

            reward.EurPriceAtReceipt = quote.Eur;
            reward.PriceAtUtc = quote.AtUtc;
            reward.PriceSource = quote.Source;
            reward.Note = $"Kurs nachgeholt am {DateTime.Now:dd.MM.yyyy HH:mm}";
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
