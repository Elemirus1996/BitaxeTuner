using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Pools;

/// <summary>
/// 0.9.12: Pool-Buchungen → Zuflüsse für die Steuer. Was als Zufluss zählt, ist einstellbar:
/// <list type="bullet">
/// <item><b>Gutschrift</b> (Standard): alle Mining-Gutschriften eines Coins an einem Tag (Steuer-Zeitzone) als ein Zufluss –
/// erst wenn der Tag vorbei ist.</item>
/// <item><b>Auszahlung</b>: jede Auszahlung vom Pool an die Wallet einzeln.</item>
/// </list>
/// 0.9.13 automatischer Umtausch in BTC (Einstellung „AutoExchange“): Mining-Dutch bucht beim Altcoin „Debit_AC“ und im
/// Bitcoin-Pool eine Gutschrift ohne Shares. Bei „Gutschrift“ ist der Umtausch ein Tausch: Verkauf des Altcoins
/// (<see cref="DeriveSwapSales"/>) und Anschaffung der BTC (Zufluss mit <see cref="MinedReward.IsSwap"/>, kein Ertrag).
/// Bei „Auszahlung“ zählen nur die BTC-Auszahlungen an die Wallet.
/// Die einzelnen Buchungen bleiben im Pool-Buch (pool-ledger.json). Zuflüsse, die nicht (mehr) gelten, werden nach
/// pool-inactive.json verschoben, nicht gelöscht – von Hand eingetragene Kurse bleiben so erhalten.
/// </summary>
public static class PoolTaxSync
{
    public const string WalletId = "pool:mining-dutch";
    public const string CreditPrefix = "md-credit-";
    public const string PayoutPrefix = "md-payout-";
    public const string SwapInPrefix = "md-swapin-";
    public const string SwapOutPrefix = "md-swapout-";

    public static bool IsPoolReward(MinedReward r) => r.WalletAddressId == WalletId;
    public static bool IsCreditReward(MinedReward r) => IsPoolReward(r) && r.TxId.StartsWith(CreditPrefix, StringComparison.Ordinal);
    public static bool IsPoolSale(Disposal d) => d.Id.StartsWith(SwapOutPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Gutschrift aus dem Mining. Nur im Bitcoin-Pool gibt es Gutschriften ohne Shares – das sind die BTC aus dem Umtausch;
    /// ältere Pool-Buch-Einträge ohne Angabe gelten als Mining.
    /// </summary>
    private static bool Mined(PoolTransaction t) => t.Coin != CoinType.Bitcoin || t.FromShares != false;

    /// <summary>Zuflüsse aus dem Pool-Buch (ohne Kurs).</summary>
    public static List<MinedReward> Derive(IEnumerable<PoolTransaction> ledger, PoolIncomeBasis basis, DateTime nowUtc, bool autoExchange = false)
    {
        var list = ledger.ToList();
        var today = TaxTime.ToTax(nowUtc).Date;
        if (basis == PoolIncomeBasis.Payout)
            return list.Where(t => t.IsPayout && (!autoExchange || t.Coin == CoinType.Bitcoin))   // Altcoin-Abbuchung = Umtausch
                .Select(t => New(t.Coin, t.Code, PayoutPrefix + t.Symbol + "-" + t.Id, t.TimeUtc, t.Amount, t.Height,
                    L.T("Mining-Dutch: Auszahlung an die Wallet")))
                .ToList();
        // 0.9.13: je Kürzel – auch Coins außerhalb der festen Liste (z. B. WJK)
        var income = list.Where(t => t.IsCredit && (!autoExchange || Mined(t)))
            .GroupBy(t => (t.Symbol, Day: TaxTime.ToTax(t.TimeUtc).Date))
            .Where(g => g.Key.Day < today)   // erst abgeschlossene Tage
            .Select(g => New(g.First().Coin, g.First().Code, CreditPrefix + g.Key.Symbol + "-" + g.Key.Day.ToString("yyyy-MM-dd"),
                g.Max(t => t.TimeUtc), g.Sum(t => t.Amount), null, L.T("Mining-Dutch: {0} Gutschrift(en) an diesem Tag", g.Count())));
        if (!autoExchange) return income.ToList();
        var swaps = list.Where(t => t.IsCredit && !Mined(t))
            .Select(t =>
            {
                var r = New(t.Coin, t.Code, SwapInPrefix + t.Symbol + "-" + t.Id, t.TimeUtc, t.Amount, t.Height,
                    L.T("Mining-Dutch: Umtausch aus anderen Coins (Anschaffung, kein Ertrag)"));
                r.IsSwap = true;
                return r;
            });
        return income.Concat(swaps).ToList();
    }

    /// <summary>Bei „Gutschrift“ mit automatischem Umtausch: jede Altcoin-Abbuchung als Verkauf (Erlös setzt der Aufrufer).</summary>
    public static List<Disposal> DeriveSwapSales(IEnumerable<PoolTransaction> ledger, PoolIncomeBasis basis, bool autoExchange)
    {
        if (basis != PoolIncomeBasis.Credit || !autoExchange) return [];
        return ledger.Where(t => t.IsPayout && t.Coin != CoinType.Bitcoin)
            .Select(t => new Disposal
            {
                Id = SwapOutPrefix + t.Symbol + "-" + t.Id,
                Coin = t.Coin,
                CoinCode = t.Coin == CoinType.Other ? t.Code : null,
                SoldAtUtc = t.TimeUtc,
                Amount = t.Amount,
                Note = L.T("Mining-Dutch: automatischer Umtausch in BTC"),
            })
            .ToList();
    }

    private static MinedReward New(CoinType coin, string? code, string txId, DateTime timeUtc, decimal amount, long? height, string note) => new()
    {
        WalletAddressId = WalletId,
        WalletLabel = "Mining-Dutch",
        Coin = coin,
        CoinCode = coin == CoinType.Other ? code : null,
        TxId = txId,
        ReceivedAtUtc = timeUtc,
        Amount = amount,
        BlockHeight = height,
        Note = note,
    };

    /// <summary>
    /// Zuflüsse abgleichen: was nicht (mehr) aus dem Pool-Buch folgt (andere Art, Umtausch-Einstellung), in den Speicher;
    /// was wieder gilt, von dort zurück; neue ergänzen; Menge eines Tages nachziehen, wenn später noch Gutschriften dieses
    /// Tages bekannt wurden. Ignorierte Einträge (<paramref name="ignored"/>) kommen nicht zurück.
    /// Liefert die neu hinzugefügten Zuflüsse (zum Bewerten mit Kurs).
    /// </summary>
    public static List<MinedReward> Apply(List<MinedReward> rewards, List<MinedReward> inactive, IEnumerable<MinedReward> derived,
        PoolIncomeBasis basis, ISet<string> ignored, out bool changed)
    {
        changed = false;
        var wanted = derived.Where(d => !ignored.Contains(Tax.Services.TaxLogRepository.TxKey(d.CoinSymbol, d.TxId))).ToList();
        var wantedIds = wanted.Select(d => d.TxId).ToHashSet(StringComparer.Ordinal);

        // 1. Was nicht mehr gilt, in den Speicher; was wieder gilt, von dort zurück
        foreach (var r in rewards.Where(r => IsPoolReward(r) && !wantedIds.Contains(r.TxId)).ToList())
        {
            rewards.Remove(r);
            inactive.RemoveAll(x => x.TxId == r.TxId);
            inactive.Add(r);
            changed = true;
        }
        foreach (var r in inactive.Where(r => wantedIds.Contains(r.TxId)).ToList())
        {
            inactive.Remove(r);
            if (rewards.All(x => x.TxId != r.TxId)) rewards.Add(r);
            changed = true;
        }

        // 2. Neue Zuflüsse ergänzen, Tagesmengen nachziehen
        var added = new List<MinedReward>();
        var byTx = rewards.Where(IsPoolReward).ToDictionary(r => r.TxId, StringComparer.Ordinal);
        foreach (var d in wanted)
        {
            if (byTx.TryGetValue(d.TxId, out var existing))
            {
                if (existing.Amount != d.Amount || existing.IsSwap != d.IsSwap)
                {
                    existing.Amount = d.Amount;
                    existing.ReceivedAtUtc = d.ReceivedAtUtc;
                    existing.IsSwap = d.IsSwap;
                    changed = true;
                }
                continue;
            }
            rewards.Add(d);
            added.Add(d);
            changed = true;
        }
        return added;
    }

    /// <summary>
    /// Umtausch-Verkäufe abgleichen: nicht mehr gültige entfernen, neue ergänzen (Erlös setzt der Aufrufer), Menge nachziehen.
    /// Von Hand erfasste Verkäufe bleiben unberührt. Liefert die neuen Verkäufe.
    /// </summary>
    public static List<Disposal> ApplySales(List<Disposal> disposals, IEnumerable<Disposal> derived, out bool changed)
    {
        changed = false;
        var wanted = derived.ToDictionary(d => d.Id, StringComparer.Ordinal);
        changed |= disposals.RemoveAll(d => IsPoolSale(d) && !wanted.ContainsKey(d.Id)) > 0;
        var added = new List<Disposal>();
        foreach (var d in wanted.Values)
        {
            var existing = disposals.FirstOrDefault(x => x.Id == d.Id);
            if (existing is null)
            {
                disposals.Add(d);
                added.Add(d);
                changed = true;
            }
            else if (existing.Amount != d.Amount)
            {
                existing.Amount = d.Amount;
                changed = true;
            }
        }
        return added;
    }
}

/// <summary>Was bei einem Pool-Konto als Zufluss zählt.</summary>
public enum PoolIncomeBasis
{
    /// <summary>Gutschrift beim Pool (je Coin und Tag zusammengefasst).</summary>
    Credit,
    /// <summary>Auszahlung vom Pool an die Wallet.</summary>
    Payout,
}
