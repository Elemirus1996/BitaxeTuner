using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.24 WLAN-Qualität: <paramref name="Rssi"/> = aktueller Wert, <paramref name="Avg"/>/<paramref name="Min"/>/<paramref name="Max"/>
/// über die letzten 30 Minuten (null, wenn zu wenig Werte). State: good, ok, weak, unstable, unknown.
/// </summary>
public sealed record WifiQuality(int? Rssi, double? Avg, int? Min, int? Max, string State);

public sealed partial class MinerHub
{
    /// <summary>Ab hier (Mittel über 30 min) gilt der Empfang als schwach.</summary>
    public const int WifiWeakDbm = -75;
    /// <summary>Bis hierher gilt er als gut.</summary>
    public const int WifiGoodDbm = -67;
    /// <summary>Spanne (max − min) in 30 min, ab der er als unruhig gilt.</summary>
    public const int WifiSwingDb = 20;

    private readonly Dictionary<string, (DateTime At, WifiQuality Q)> _wifiCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Letzter bekannter Wert je Miner – für den Hinweis bei „offline“.</summary>
    private readonly Dictionary<string, int> _lastRssi = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _wifiChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bewertung des WLAN-Empfangs (höchstens einmal je Minute aus history.db gerechnet).</summary>
    public WifiQuality WifiOf(HubDevice d, DateTime now)
    {
        if (_wifiCache.TryGetValue(d.Host, out var c) && now - c.At < TimeSpan.FromMinutes(1) && now >= c.At) return c.Q;
        int? current = d.State.Online && d.State.Info is { wifiRSSI: var r } && r != 0 ? r : null;
        var points = History?.QueryWifi(d.Host, now.AddMinutes(-30), now, 60) ?? [];
        var q = Rate(current, points.Select(p => p.Rssi).ToList());
        _wifiCache[d.Host] = (now, q);
        return q;
    }

    /// <summary>Bewertung aus aktuellem Wert und den Werten der letzten 30 Minuten.</summary>
    internal static WifiQuality Rate(int? current, IReadOnlyList<double> recent)
    {
        if (recent.Count < 10)
            return new WifiQuality(current, null, null, null,
                current is null ? "unknown" : current < WifiWeakDbm ? "weak" : current < WifiGoodDbm ? "ok" : "good");
        var avg = Math.Round(recent.Average(), 1);
        var min = (int)Math.Floor(recent.Min());
        var max = (int)Math.Ceiling(recent.Max());
        var state = avg < WifiWeakDbm ? "weak" : max - min >= WifiSwingDb ? "unstable" : avg < WifiGoodDbm ? "ok" : "good";
        return new WifiQuality(current, avg, min, max, state);
    }

    public static string WifiStateText(string state) => state switch
    {
        "good" => L.T("gut"),
        "ok" => L.T("mittel"),
        "weak" => L.T("schwach"),
        "unstable" => L.T("unruhig"),
        _ => L.T("unbekannt"),
    };

    /// <summary>Aus der Verlaufs-Schreibrunde (einmal je Minute je Miner): Wert speichern, alle 10 min bewerten und ggf. melden.</summary>
    internal void RecordWifi(HubDevice d, int rssi, DateTime now)
    {
        _lastRssi[d.Host] = rssi;
        History?.AddWifi(d.Host, now, rssi);
        if (_wifiChecked.TryGetValue(d.Host, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        _wifiChecked[d.Host] = now;
        _wifiCache.Remove(d.Host);
        var q = WifiOf(d, now);
        if (q.State == "weak" && q.Avg is { } avg)
            SendAlert(new Alert($"wifi-weak:{d.Host}", L.T("{0}: schwaches WLAN", d.Title),
                L.T("Seit 30 Minuten im Mittel {0} dBm – ab etwa {1} dBm brechen Verbindungen eher ab. Miner näher an Router oder Repeater stellen bzw. die Antenne ausrichten.",
                    avg.ToString("0", L.Culture), WifiWeakDbm),
                NotifyPriority.Normal, TimeSpan.FromHours(12), NotifyCategory.Health, d.Host));
    }

    /// <summary>Zusatz für die Offline-Meldung: war das WLAN zuletzt schwach, kann es die Ursache sein.</summary>
    internal string WifiOfflineHint(string host) =>
        _lastRssi.TryGetValue(host, out var r) && r < WifiWeakDbm
            ? " " + L.T("Letztes WLAN-Signal {0} dBm (schwach) – das WLAN kann die Ursache sein.", r)
            : "";
}
