using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.16: Neuigkeiten, die der Server selbst erzeugt (ohne Internet-Dienst des Projekts):
/// Halving-Countdown (BTC, BCH, wenn die Blockhöhe bekannt ist) in Stufen und große Kursbewegungen geschürfter Coins.
/// </summary>
public sealed partial class MinerHub
{
    public const int HalvingInterval = 210_000;
    public const double PriceMoveNewsPercent = 10;
    private static readonly int[] HalvingStages = [1000, 500, 365, 180, 100, 30, 7, 1];
    private DateTime _localNewsAt = DateTime.MinValue;

    private void TickLocalNews(DateTime now)
    {
        if (now - _localNewsAt < TimeSpan.FromMinutes(30)) return;
        _localNewsAt = now;
        if (Options.OnlineChecks) _ = RefreshNetworkAsync(force: false);   // Blockhöhe (höchstens alle 10 min)
        try
        {
            if (_network is { } btc) HalvingNews("BTC", btc.Height, now);
            if (_bchNetwork is { } bch) HalvingNews("BCH", bch.Height, now);
            PriceMoveNews(now);
        }
        catch { /* Neuigkeiten sind nur Zusatz */ }
    }

    /// <summary>Nächstes Halving und Stufe (Tage) – bei 1000, 500, 365, 180, 100, 30, 7, 1 Tag(en) und beim Halving selbst je eine Meldung.</summary>
    internal NewsItem? HalvingNews(string coin, long height, DateTime now)
    {
        var next = (height / HalvingInterval + 1) * HalvingInterval;
        var remaining = next - height;
        var days = remaining * 10.0 / 1440;   // 10 Minuten je Block
        var epoch = (int)(next / HalvingInterval);
        var reward = 50.0 / Math.Pow(2, epoch);
        // Gerade passiert: die ersten ~1000 Blöcke nach dem Halving
        var since = height % HalvingInterval;
        NewsItem item;
        if (since < 1000 && height >= HalvingInterval)
        {
            var reached = height - since;
            item = new NewsItem($"halving-{coin}-{reached}-done", now.ToUniversalTime(), "halving", coin,
                L.T("{0}-Halving erreicht", coin),
                L.T("Block {0}: Die Blockprämie ist jetzt {1} {2}.", reached.ToString("N0", L.Culture), (50.0 / Math.Pow(2, reached / HalvingInterval)).ToString("0.#####", L.Culture), coin), "");
        }
        else
        {
            if (HalvingStages.Where(s => days <= s).DefaultIfEmpty(-1).Min() is var stage && stage < 0)
                stage = 9999;   // weiter weg als 1000 Tage: eine Meldung „in ca. … Jahren“
            var when = now.AddDays(days);
            item = new NewsItem($"halving-{coin}-{next}-{stage}", now.ToUniversalTime(), "halving", coin,
                days >= 730 ? L.T("{0}-Halving in ca. {1} Jahren", coin, (days / 365).ToString("0.0", L.Culture))
                            : L.T("{0}-Halving in ca. {1} Tagen", coin, Math.Ceiling(days).ToString("N0", L.Culture)),
                L.T("Block {0}: noch {1} Blöcke, voraussichtlich um den {2:d}. Danach {3} {4} je Block.",
                    next.ToString("N0", L.Culture), remaining.ToString("N0", L.Culture), when, reward.ToString("0.#####", L.Culture), coin), "");
        }
        return News.AddLocal(item) ? item : null;
    }

    /// <summary>Geschürfte Coins mit ±10 % in 24 Stunden – je Coin, Tag und Richtung eine Meldung.</summary>
    internal void PriceMoveNews(DateTime now)
    {
        foreach (var q in CoinQuotes())
        {
            if (q.Change24hPercent is not { } ch || Math.Abs(ch) < PriceMoveNewsPercent || q.Price is not { } price) continue;
            var dir = ch > 0 ? "up" : "down";
            var id = $"price-{q.Symbol}-{now:yyyyMMdd}-{dir}";
            if (News.HasLocal(id)) continue;
            var cur = Currencies.Of(Config).Symbol;
            News.AddLocal(new NewsItem(id, now.ToUniversalTime(), "price", q.Symbol,
                L.T("{0} {1} in 24 Stunden", q.Symbol, (ch > 0 ? "+" : "") + ch.ToString("0.0", L.Culture) + " %"),
                q.Kind == "mined"
                    ? L.T("Kurs jetzt {0} {1} – gilt für den Coin, den deine Miner gerade schürfen.", price.ToString(price >= 100 ? "N0" : price >= 1 ? "N2" : "0.######", L.Culture), cur)
                    : L.T("Kurs jetzt {0} {1}.", price.ToString(price >= 100 ? "N0" : price >= 1 ? "N2" : "0.######", L.Culture), cur), ""));
        }
    }
}
