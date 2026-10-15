using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.16: Daten der E-Paper-Seiten „Bitcoin-Netz“, „Pool“ und „Monatsvergleich“, Seitenfolge nach Tageszeit.</summary>
public sealed partial class MinerHub
{
    private (DateTime Fetched, FeesDto Fees)? _fees;
    private bool _bitcoinBusy;
    private (DateTime At, string Key, DisplayMonthCompare Data)? _monthCompareCache;

    /// <summary>Gebühren und Difficulty-Anpassung (mempool.space), höchstens alle 10 Minuten; Blockhöhe über das Netzwerk.</summary>
    private async Task RefreshBitcoinAsync()
    {
        if (!Options.OnlineChecks || _bitcoinBusy) return;
        var now = DateTime.Now;
        _ = RefreshNetworkAsync(force: false);
        if (_fees is { } f && now - f.Fetched < TimeSpan.FromMinutes(10) && _difficulty is { } d && now - d.Fetched < TimeSpan.FromMinutes(10)) return;
        _bitcoinBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _fees = (now, await NetworkClient.GetFeesAsync(cts.Token));
            if (_difficulty is not { } dd || now - dd.Fetched >= TimeSpan.FromMinutes(10))
            {
                var dto = await NetworkClient.GetDifficultyAsync(cts.Token);
                _difficulty = (now, dto, new DisplayDifficulty(dto.ProgressPercent, dto.ExpectedChangePercent, dto.RemainingBlocks, dto.Eta));
            }
        }
        catch { /* ohne Netzdaten weiter */ }
        finally
        {
            _bitcoinBusy = false;
        }
    }

    private DisplayBitcoin BuildBitcoin()
    {
        _ = RefreshBitcoinAsync();
        var height = _network?.Height;
        var next = height is { } h ? (h / HalvingInterval + 1) * HalvingInterval : 0;
        var epoch = (int)(next / HalvingInterval);
        return new DisplayBitcoin(height, _network?.Pool, _network?.Time, _fees?.Fees, _difficulty?.Data,
            next, height is { } hh ? next - hh : HalvingInterval,
            height is { } x ? 50.0 / Math.Pow(2, x / HalvingInterval) : MiningModes.Subsidy, 50.0 / Math.Pow(2, epoch));
    }

    /// <summary>Pool-Seite: je Miner Coin und Pool (bzw. Abrechnung laut Pool-Konto), Guthaben, letzte Auszahlung, bester Pool.</summary>
    private DisplayPoolPage BuildPoolPage(DateTime now)
    {
        var group = CurGroup;
        var miners = Devices
            .Where(d => group is null || d.Config.Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
            .Select(d =>
            {
                var s = d.State;
                var worker = PoolWorkerOf(s);
                var pool = s.Online && s.Info is { } i ? PoolIdentities.For(i)?.Name : null;
                return new DisplayPoolMiner(d.Title, s.Online ? MinedSymbolOf(s) : "–", pool, worker?.Mode);
            }).ToList();

        var cur = Currencies.Of(Config);
        List<DisplayPoolCoin> coins = [];
        DateTime? lastPayout = null;
        string? lastCoin = null;
        if (Config.PoolAccount.Enabled)
        {
            var acc = PoolAccount;
            coins = acc.Coins.Where(c => c.Confirmed + c.Unconfirmed > 0)
                .Select(c => new DisplayPoolCoin(c.Symbol, c.Confirmed + c.Unconfirmed, c.Price is { } p ? Math.Round((c.Confirmed + c.Unconfirmed) * p, 2) : null))
                .OrderByDescending(c => c.Value ?? 0).ToList();
            if (acc.Coins.Where(c => c.LastPayoutUtc is not null).MaxBy(c => c.LastPayoutUtc) is { } paid)
                (lastPayout, lastCoin) = (paid.LastPayoutUtc, paid.Symbol);
        }

        // Zuverlässigster Pool der letzten 7 Tage: höchste Verfügbarkeit, dann wenigste abgelehnte Shares (mind. 12 h Daten)
        string? best = null, note = null;
        try
        {
            if (PoolComparisonFor("7d", now).Rows.Where(r => r.Hours >= 12 && r.AvailabilityPercent is not null)
                    .OrderByDescending(r => r.AvailabilityPercent).ThenBy(r => r.RejectPercent ?? 100).FirstOrDefault() is { } row)
            {
                best = row.Pool;
                note = L.T("{0} % verfügbar", row.AvailabilityPercent!.Value.ToString("0.0", L.Culture)) +
                       (row.RejectPercent is { } rej ? " · " + L.T("{0} % abgelehnt", rej.ToString("0.00", L.Culture)) : "");
            }
        }
        catch { /* ohne Vergleich */ }
        return new DisplayPoolPage(miners, coins, cur.Symbol, lastPayout, lastCoin, best, note);
    }

    /// <summary>Strom (kWh, Kosten) und Ertrag der letzten Monate (6 bzw. 12), höchstens stündlich neu gerechnet.</summary>
    private DisplayMonthCompare? BuildMonthCompare(DateTime now)
    {
        if (History is null) return null;
        var months = Ds.MonthCompareMonths is 12 ? 12 : 6;
        var cur = Currencies.Of(Config);
        var key = $"{now:yyyy-MM}|{months}|{cur.Code}";
        if (_monthCompareCache is { } c && c.Key == key && now - c.At < TimeSpan.FromHours(1) && now >= c.At) return c.Data;
        try
        {
            var hosts = Devices.Select(d => d.Host).ToList();
            List<PoolTransaction> ledger;
            try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
            var pool = Config.PoolAccount.Enabled && ledger.Count > 0;
            List<MinedReward> rewards = [];
            if (!pool) try { rewards = TaxRepository.LoadIncome(); } catch { rewards = []; }
            var list = new List<DisplayMonthBar>();
            var first = new DateTime(now.Year, now.Month, 1).AddMonths(-(months - 1));
            for (var m = first; m <= now; m = m.AddMonths(1))
            {
                var end = m.AddMonths(1) < now ? m.AddMonths(1) : now;
                var energy = Plugs.EnergyCost.Compute(History, Config, hosts, m, end);
                var (income, _) = pool ? PoolIncome(ledger, m.ToUniversalTime(), end.ToUniversalTime(), cur.Code) : TaxIncome(rewards, m.ToUniversalTime(), end.ToUniversalTime(), cur.Code);
                if (energy.Kwh <= 0 && income is null && list.Count == 0) continue;   // Monate vor den ersten Daten weglassen
                list.Add(new DisplayMonthBar(m, Math.Round(energy.Kwh, 1), Math.Round((decimal)energy.Cost, 2), income is { } i ? Math.Round(i, 2) : null));
            }
            var data = new DisplayMonthCompare(list, cur.Symbol);
            _monthCompareCache = (now, key, data);
            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Zeitfenster der Seitenfolge, das jetzt gilt (null = keins, alle Seiten wie eingestellt).</summary>
    private DisplayPageWindow? CurrentPageWindow(DisplaySettings s, DateTime now) =>
        s.PageWindows?.FirstOrDefault(w => w.From == w.To || (w.From < w.To ? now.Hour >= w.From && now.Hour < w.To : now.Hour >= w.From || now.Hour < w.To));

    /// <summary>Intervall der Anzeige – im Zeitfenster mit eigenem Intervall dieses.</summary>
    private TimeSpan DisplayInterval(DisplaySettings s, DateTime now) =>
        TimeSpan.FromMinutes(Math.Max(DisplaySettings.MinIntervalMinutes,
            CurrentPageWindow(s, now) is { IntervalMinutes: > 0 } w ? w.IntervalMinutes : s.IntervalMinutes));
}
