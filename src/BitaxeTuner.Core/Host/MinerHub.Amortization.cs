using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.21: Stand der Amortisation eines Miners (Beträge in der eingestellten Währung).
/// Net = Ertragsanteil − Stromkosten seit <see cref="TrackedFrom"/>; Remaining ≤ 0 = bezahlt gemacht.
/// </summary>
/// <param name="TrackedFrom">Ab hier liegen Messwerte vor (Kaufdatum oder später, wenn BitaxeTuner den Miner erst später sah).</param>
/// <param name="NetPerDay">Saldo je Tag der letzten 30 Tage (Grundlage der Prognose).</param>
/// <param name="Eta">Voraussichtlich bezahlt am; null = schon bezahlt oder bei aktuellem Saldo nie.</param>
/// <param name="Partial">Ertrag teilweise ohne Kurs bzw. ohne bekannten Ertrag gerechnet.</param>
public sealed record MinerAmortization(string Host, decimal Price, DateTime PurchaseDate, DateTime TrackedFrom, decimal Income, decimal Cost,
    decimal Net, decimal Remaining, decimal? NetPerDay, DateTime? Eta, bool Paid, bool Partial, string Source, string Currency, DateTime? WarrantyUntil)
{
    /// <summary>Anteil des Kaufpreises, der schon wieder eingespielt ist (0–1, darüber = Gewinn).</summary>
    public double Progress => Price > 0 ? (double)(Net / Price) : 0;
}

public sealed partial class MinerHub
{
    private readonly Dictionary<string, (DateTime At, string Cur, MinerAmortization? Value)> _amortization = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _amortizationTickAt = DateTime.MinValue;

    /// <summary>Amortisation eines Miners (null = kein Kaufpreis/-datum eingetragen oder kein Verlauf). Höchstens alle 30 min neu.</summary>
    public MinerAmortization? AmortizationOf(HubDevice d, DateTime now)
    {
        if (History is null || d.Config.Purchase is not { Price: > 0 and var price, Date: { } date } p) return null;
        var cur = Currencies.Of(Config).Code;
        if (_amortization.TryGetValue(d.Host, out var c) && c.Cur == cur && now - c.At < TimeSpan.FromMinutes(30) && now >= c.At)
            return c.Value;
        MinerAmortization? value = null;
        try { value = ComputeAmortization(d, price, date.Date, p.WarrantyMonths, now, cur); }
        catch { /* nur Zusatz */ }
        _amortization[d.Host] = (now, cur, value);
        return value;
    }

    private MinerAmortization? ComputeAmortization(HubDevice d, decimal price, DateTime purchase, int? warrantyMonths, DateTime now, string cur)
    {
        var earliest = History!.EarliestSample();
        var from = earliest is { } e && e > purchase ? e : purchase;
        if (from >= now) return null;
        List<PoolTransaction> ledger;
        try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
        var pool = Config.PoolAccount.Enabled && ledger.Count > 0;
        List<MinedReward> rewards = [];
        if (!pool) try { rewards = TaxRepository.LoadIncome(); } catch { rewards = []; }

        (decimal Net, decimal Income, decimal Cost, bool Partial) Window(DateTime f)
        {
            var (income, partial) = pool ? PoolIncome(ledger, f.ToUniversalTime(), now.ToUniversalTime(), cur) : TaxIncome(rewards, f.ToUniversalTime(), now.ToUniversalTime(), cur);
            var share = HashShare(d.Host, f, now);
            var mine = Math.Round((income ?? 0) * (decimal)share, 2);
            var cost = Math.Round((decimal)Plugs.EnergyCost.Compute(History!, Config, [d.Host], f, now).Cost, 2);
            return (mine - cost, mine, cost, partial || income is null);
        }

        var all = Window(from);
        var recentFrom = now.AddDays(-30) > from ? now.AddDays(-30) : from;
        var days = (decimal)(now - recentFrom).TotalDays;
        decimal? perDay = days >= 1 ? Math.Round(Window(recentFrom).Net / days, 2) : null;
        var remaining = price - all.Net;
        var paid = remaining <= 0;
        DateTime? eta = !paid && perDay is > 0 ? now.Date.AddDays((double)Math.Ceiling(remaining / perDay.Value)) : null;
        DateTime? warranty = warrantyMonths is > 0 ? purchase.AddMonths(warrantyMonths.Value) : null;
        return new MinerAmortization(d.Host, price, purchase, from, all.Income, all.Cost, all.Net, Math.Round(remaining, 2), perDay, eta, paid,
            all.Partial, pool ? "pool" : "tax", cur, warranty);
    }

    /// <summary>Anteil dieses Miners an der gesamten Hashrate im Zeitraum (Ø Hashrate × Online-Messwerte).</summary>
    private double HashShare(string host, DateTime from, DateTime to)
    {
        double Work(string h) => History!.Average(h, from, to) is { } a ? a.HashRateGh * a.Minutes : 0;
        var mine = Work(host);
        var total = Devices.Sum(x => Work(x.Host));
        return total > 0 ? mine / total : 0;
    }

    /// <summary>Stündlich: Garantie läuft in 30 Tagen ab, Miner hat sich bezahlt gemacht – je einmal melden (Merker in der Konfiguration).</summary>
    private void TickAmortization(DateTime now)
    {
        if (now - _amortizationTickAt < TimeSpan.FromHours(1)) return;
        _amortizationTickAt = now;
        var changed = false;
        foreach (var d in Devices)
        {
            if (d.Config.Purchase is not { } p) continue;
            if (p.Date is { } date && p.WarrantyMonths is > 0 && !p.WarrantyNotified)
            {
                var until = date.Date.AddMonths(p.WarrantyMonths.Value);
                var left = (until - now.Date).TotalDays;
                if (left is >= 0 and <= 30)
                {
                    SendAlert(new Alert($"warranty:{d.Host}", L.T("Garantie endet bald"),
                        L.T("Die Garantie von {0} endet am {1:d} – jetzt ist ein guter Zeitpunkt, den Miner zu prüfen.", d.Title, until),
                        NotifyPriority.Normal, TimeSpan.FromDays(60), NotifyCategory.Maintenance, d.Host));
                    LogEvent(d.Host, EventCategories.Other, L.T("Garantie von {0} endet am {1:d}.", d.Title, until));
                    p.WarrantyNotified = changed = true;
                }
            }
            if (!p.PaidNotified && AmortizationOf(d, now) is { Paid: true } a)
            {
                SendAlert(new Alert($"amortized:{d.Host}", L.T("Miner hat sich bezahlt gemacht"),
                    L.T("{0} hat seinen Kaufpreis von {1} {2} wieder eingespielt (Ertrag minus Strom).", d.Title, a.Price.ToString("N2", L.Culture), Currencies.Of(Config).Symbol),
                    NotifyPriority.Normal, TimeSpan.FromDays(3650), NotifyCategory.Finds, d.Host));
                LogEvent(d.Host, EventCategories.Other, L.T("{0} hat sich bezahlt gemacht.", d.Title));
                p.PaidNotified = changed = true;
            }
        }
        if (changed) Config.Save();
    }

    /// <summary>Kaufdaten eines Miners setzen bzw. löschen (Preis und Datum leer).</summary>
    public void SetPurchase(HubDevice d, DevicePurchase? p)
    {
        if (p is null || (p.Price is null && p.Date is null && p.WarrantyMonths is null)) { d.Config.Purchase = null; }
        else
        {
            if (p.Price is < 0 or > 1_000_000) throw new LocalizedException("Kaufpreis ungültig.");
            if (p.Date is { } dt && dt.Date > DateTime.Now.Date) throw new LocalizedException("Das Kaufdatum liegt in der Zukunft.");
            if (p.WarrantyMonths is < 0 or > 120) throw new LocalizedException("Garantie: 0 bis 120 Monate.");
            var old = d.Config.Purchase;
            d.Config.Purchase = new DevicePurchase
            {
                Price = p.Price, Date = p.Date?.Date, WarrantyMonths = p.WarrantyMonths,
                // Merker bleiben, solange Datum/Garantie gleich sind
                WarrantyNotified = old is not null && old.Date == p.Date?.Date && old.WarrantyMonths == p.WarrantyMonths && old.WarrantyNotified,
                PaidNotified = old is not null && old.Price == p.Price && old.Date == p.Date?.Date && old.PaidNotified,
            };
        }
        _amortization.Remove(d.Host);
    }

    internal void TickAmortizationForTest(DateTime now) { _amortizationTickAt = DateTime.MinValue; TickAmortization(now); }
}
