using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Pools;

/// <summary>
/// 0.9.12: Pool-Buchungen → Zuflüsse für die Steuer. Was als Zufluss zählt, ist einstellbar:
/// <list type="bullet">
/// <item><b>Gutschrift</b> (Standard): alle Gutschriften eines Coins an einem Tag (Steuer-Zeitzone) als ein Zufluss –
/// erst wenn der Tag vorbei ist.</item>
/// <item><b>Auszahlung</b>: jede Auszahlung vom Pool an die Wallet einzeln.</item>
/// </list>
/// Die einzelnen Buchungen bleiben im Pool-Buch (pool-ledger.json). Zuflüsse der nicht gewählten Art werden beim
/// Umschalten nach pool-inactive.json verschoben, nicht gelöscht – von Hand eingetragene Kurse bleiben so erhalten.
/// </summary>
public static class PoolTaxSync
{
    public const string WalletId = "pool:mining-dutch";
    public const string CreditPrefix = "md-credit-";
    public const string PayoutPrefix = "md-payout-";

    public static bool IsPoolReward(MinedReward r) => r.WalletAddressId == WalletId;
    public static bool IsCreditReward(MinedReward r) => IsPoolReward(r) && r.TxId.StartsWith(CreditPrefix, StringComparison.Ordinal);

    /// <summary>Zuflüsse aus dem Pool-Buch (ohne Kurs).</summary>
    public static List<MinedReward> Derive(IEnumerable<PoolTransaction> ledger, PoolIncomeBasis basis, DateTime nowUtc)
    {
        var today = TaxTime.ToTax(nowUtc).Date;
        if (basis == PoolIncomeBasis.Payout)
            return ledger.Where(t => t.IsPayout)
                .Select(t => New(t.Coin, PayoutPrefix + t.Coin.Symbol() + "-" + t.Id, t.TimeUtc, t.Amount, t.Height,
                    L.T("Mining-Dutch: Auszahlung an die Wallet")))
                .ToList();
        return ledger.Where(t => t.IsCredit)
            .GroupBy(t => (t.Coin, Day: TaxTime.ToTax(t.TimeUtc).Date))
            .Where(g => g.Key.Day < today)   // erst abgeschlossene Tage
            .Select(g => New(g.Key.Coin, CreditPrefix + g.Key.Coin.Symbol() + "-" + g.Key.Day.ToString("yyyy-MM-dd"),
                g.Max(t => t.TimeUtc), g.Sum(t => t.Amount), null, L.T("Mining-Dutch: {0} Gutschrift(en) an diesem Tag", g.Count())))
            .ToList();
    }

    private static MinedReward New(CoinType coin, string txId, DateTime timeUtc, decimal amount, long? height, string note) => new()
    {
        WalletAddressId = WalletId,
        WalletLabel = "Mining-Dutch",
        Coin = coin,
        TxId = txId,
        ReceivedAtUtc = timeUtc,
        Amount = amount,
        BlockHeight = height,
        Note = note,
    };

    /// <summary>
    /// Zuflüsse abgleichen: Art umschalten (verschieben), neue ergänzen, Menge eines Tages nachziehen, wenn später noch
    /// Gutschriften dieses Tages bekannt wurden. Ignorierte Einträge (<paramref name="ignored"/>) kommen nicht zurück.
    /// Liefert die neu hinzugefügten Zuflüsse (zum Bewerten mit Kurs).
    /// </summary>
    public static List<MinedReward> Apply(List<MinedReward> rewards, List<MinedReward> inactive, IEnumerable<MinedReward> derived,
        PoolIncomeBasis basis, ISet<string> ignored, out bool changed)
    {
        changed = false;
        bool Active(MinedReward r) => basis == PoolIncomeBasis.Payout ? !IsCreditReward(r) : IsCreditReward(r);

        // 1. Art umschalten: falsche Art aus den Zuflüssen in den Speicher, passende von dort zurück
        foreach (var r in rewards.Where(r => IsPoolReward(r) && !Active(r)).ToList())
        {
            rewards.Remove(r);
            inactive.RemoveAll(x => x.TxId == r.TxId);
            inactive.Add(r);
            changed = true;
        }
        foreach (var r in inactive.Where(Active).ToList())
        {
            inactive.Remove(r);
            if (rewards.All(x => x.TxId != r.TxId)) rewards.Add(r);
            changed = true;
        }

        // 2. Neue Zuflüsse ergänzen, Tagesmengen nachziehen
        var added = new List<MinedReward>();
        var byTx = rewards.Where(IsPoolReward).ToDictionary(r => r.TxId, StringComparer.Ordinal);
        foreach (var d in derived)
        {
            if (ignored.Contains(Tax.Services.TaxLogRepository.TxKey(d.Coin, d.TxId))) continue;
            if (byTx.TryGetValue(d.TxId, out var existing))
            {
                if (existing.Amount != d.Amount)
                {
                    existing.Amount = d.Amount;
                    existing.ReceivedAtUtc = d.ReceivedAtUtc;
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
}

/// <summary>Was bei einem Pool-Konto als Zufluss zählt.</summary>
public enum PoolIncomeBasis
{
    /// <summary>Gutschrift beim Pool (je Coin und Tag zusammengefasst).</summary>
    Credit,
    /// <summary>Auszahlung vom Pool an die Wallet.</summary>
    Payout,
}
