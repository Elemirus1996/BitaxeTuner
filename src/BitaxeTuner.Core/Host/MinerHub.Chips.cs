using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.24: Zustand eines Chips. State: ok, weak (dauerhaft deutlich unter den anderen), dead (liefert nichts mehr).</summary>
public sealed record ChipState(int Index, double HashGh, double PercentOfMedian, string State);

/// <summary>
/// 0.9.24 Ausfall einzelner Chips bei Mehrchip-Minern: liefert die Firmware die Hashrate je ASIC (ESP-Miner ab 2.10
/// „hashrateMonitor“), vergleicht BitaxeTuner jeden Chip mit dem Median der anderen. Ein Chip gilt als schwach, wenn er
/// in drei Prüfungen in Folge (alle 10 min, also 30 min) unter 60 % liegt, als ausgefallen unter 5 %. Nur lesend – keine
/// automatische Änderung.
/// </summary>
public sealed partial class MinerHub
{
    public const double ChipWeakPercent = 60;
    public const double ChipDeadPercent = 5;
    private const int ChipChecksNeeded = 3;
    private static readonly TimeSpan ChipCheckInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _chipChecked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _chipLowCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ChipState>> _chipStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bewertung je Chip aus den aktuellen Werten (ohne Verlauf).</summary>
    internal static List<ChipState> RateChips(IReadOnlyList<double> hashes)
    {
        if (hashes.Count < 2) return [];
        var sorted = hashes.OrderBy(x => x).ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        if (median <= 0) return [];
        return hashes.Select((h, i) =>
        {
            var pct = Math.Round(100 * h / median, 0);
            return new ChipState(i + 1, Math.Round(h, 1), pct, pct < ChipDeadPercent ? "dead" : pct < ChipWeakPercent ? "weak" : "ok");
        }).ToList();
    }

    /// <summary>Zuletzt bestätigter Zustand je Chip (erst nach 30 min auffällig); leer ohne Werte je Chip.</summary>
    public IReadOnlyList<ChipState> ChipStatesOf(HubDevice d) => _chipStates.TryGetValue(d.Host, out var s) ? s : [];

    /// <summary>Aus der Verlaufs-Schreibrunde: Werte speichern, alle 10 min prüfen, bestätigte Auffälligkeiten melden.</summary>
    internal void RecordChipHashrates(HubDevice d, IReadOnlyList<double> hashes, DateTime now)
    {
        History?.AddChipHashrates(d.Host, now, hashes);
        if (_chipChecked.TryGetValue(d.Host, out var last) && now - last < ChipCheckInterval && now >= last) return;
        _chipChecked[d.Host] = now;
        var rated = RateChips(hashes);
        if (rated.Count == 0) { _chipStates.Remove(d.Host); _chipLowCount.Remove(d.Host); return; }
        var counts = _chipLowCount.TryGetValue(d.Host, out var c) && c.Length == rated.Count ? c : new int[rated.Count];
        for (var i = 0; i < rated.Count; i++) counts[i] = rated[i].State == "ok" ? 0 : counts[i] + 1;
        _chipLowCount[d.Host] = counts;
        // erst nach drei auffälligen Prüfungen in Folge (30 min) als auffällig zeigen – kurze Einbrüche sind normal
        var confirmed = rated.Select((r, i) => counts[i] >= ChipChecksNeeded ? r : r with { State = "ok" }).ToList();
        _chipStates[d.Host] = confirmed;
        foreach (var chip in confirmed.Where(x => x.State != "ok"))
            SendAlert(new Alert($"chip:{d.Host}:{chip.Index}",
                chip.State == "dead" ? L.T("{0}: Chip {1} ausgefallen", d.Title, chip.Index) : L.T("{0}: Chip {1} schwach", d.Title, chip.Index),
                L.T("Chip {0} liefert seit 30 Minuten nur {1} % der übrigen Chips ({2} GH/s).", chip.Index, chip.PercentOfMedian.ToString("0", L.Culture), chip.HashGh.ToString("0", L.Culture)) + " " +
                ChipAdvice, NotifyPriority.High, TimeSpan.FromHours(12), NotifyCategory.Health, d.Host));
    }

    internal static string ChipAdvice =>
        L.T("Prüfen: Kühlkörper und Wärmeleitpaste des Chips, Lüfter, ob die Spannung für die Frequenz reicht (zum Test die Werkseinstellung) und die Lötstellen. BitaxeTuner ändert dabei nichts von selbst.");
}
