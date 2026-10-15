using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.16 Coinbase-Prüfung: ESP-Miner mit „Coinbase entschlüsseln“ liefert, ob die Coinbase-Transaktion des aktuellen Jobs
/// an die eigene Adresse (Pool-Benutzer) zahlt – „verified“, „not_found“, „not_applicable“ (Benutzer ist ein Pool-Konto),
/// „unknown“ (aus bzw. nicht lesbar). „not_found“ in zwei Abfragen in Folge → Protokoll und Push: Bei einem Solo-Pool
/// bekäme man einen gefundenen Block nicht.
/// </summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, int> _coinbaseMiss = new();

    /// <summary>Nach jeder Abfrage aufrufen.</summary>
    private void TickCoinbase()
    {
        foreach (var d in Devices)
        {
            if (!d.State.Online || d.State.Info is not { } i || d.IsSimulated && i.coinbasePayoutStatus is null) continue;
            var count = _coinbaseMiss.GetValueOrDefault(d.Host);
            if (i.coinbasePayoutStatus == "not_found")
            {
                _coinbaseMiss[d.Host] = ++count;
                if (count < 2) continue;   // einmal kann ein Pool-Wechsel sein; Push höchstens einmal am Tag (Sperre)
                var text = L.T("Die Coinbase des Pools zahlt nicht an die eingetragene Adresse ({0}). Bei einem Solo-Pool bekämst du einen gefundenen Block nicht – Pool-Benutzer und Pool prüfen.",
                    ShortAddress(i.stratumUser));
                if (count == 2) d.AddLog(L.T("Coinbase-Prüfung: {0}", text), EventCategories.Settings);
                SendAlert(new Alert($"coinbase:{d.Host}", L.T("{0}: Coinbase zahlt nicht an dich", d.Title), text,
                    NotifyPriority.High, TimeSpan.FromHours(24), NotifyCategory.Pool, d.Host));
            }
            else if (i.coinbasePayoutStatus is "verified" or "not_applicable")
            {
                if (count >= 2) d.AddLog(L.T("Coinbase-Prüfung: zahlt wieder an die eingetragene Adresse."), EventCategories.Settings);
                _coinbaseMiss.Remove(d.Host);
            }
        }
    }

    internal void TickCoinbaseForTest() => TickCoinbase();

    private static string ShortAddress(string? user)
    {
        var a = (user ?? "").Split('.', 2)[0];
        return a.Length > 20 ? a[..8] + "…" + a[^6..] : a;
    }
}
