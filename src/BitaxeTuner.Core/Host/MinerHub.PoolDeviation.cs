using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Ein Miner nutzt nicht mehr die zugewiesene Pool-Vorlage (z. B. in AxeOS geändert oder nach einem Reset).</summary>
public sealed record PoolDeviation(string Host, string Name, IReadOnlyList<string> Differences, string? PrimaryId, string? FallbackId);

/// <summary>
/// 0.9.16: Abweichung von der Pool-Vorlage. Beim Übertragen merkt sich der Miner seine Vorlagen
/// (<see cref="DeviceConfig.PoolPresetPrimary"/>/<see cref="DeviceConfig.PoolPresetFallback"/>); alle 10 min werden Adresse,
/// Port und Benutzer verglichen (bei „Worker behalten“ nur die Wallet). Zweimal in Folge abweichend → Protokoll und Push
/// (Kategorie Pool, einmal am Tag); auf der Pools-Seite „wieder angleichen“ (Vorschau, Bestätigung) oder „so lassen“.
/// </summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, int> _poolDeviationCount = new();
    private readonly Dictionary<string, Pools.PoolLayout> _poolDeviationLayouts = new();
    private DateTime _poolDeviationCheckedAt = DateTime.MinValue;
    private bool _poolDeviationBusy;

    private static string NormalizeUrl(string? url)
    {
        var u = (url ?? "").Trim().ToLowerInvariant();
        if (u.Contains("://", StringComparison.Ordinal)) u = u[(u.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return u.TrimEnd('/');
    }

    /// <summary>
    /// Abweichung eines Miners (null = keine Vorlage gemerkt, offline, noch nicht geprüft oder alles wie vorgesehen).
    /// Grundlage ist die zuletzt gelesene Pool-Liste (ESP-Miner ab 2.15), sonst die Felder stratum*/fallbackStratum*.
    /// </summary>
    public PoolDeviation? PoolDeviationOf(HubDevice d)
    {
        if (!d.State.Online || d.State.Info is not { } i) return null;
        var primary = PoolPresetById(d.Config.PoolPresetPrimary);
        var fallback = PoolPresetById(d.Config.PoolPresetFallback);
        if (primary is null && fallback is null) return null;
        var diffs = new List<string>();
        if (_poolDeviationLayouts.TryGetValue(d.Host, out var layout))
        {
            Compare(primary, layout.Primary?.Url, layout.Primary?.Port ?? 0, layout.Primary?.User, L.T("Haupt-Pool"));
            Compare(fallback, layout.Secondary?.Url, layout.Secondary?.Port ?? 0, layout.Secondary?.User, L.T("Ersatz-Pool"));
        }
        else if (!string.IsNullOrWhiteSpace(i.stratumURL))
        {
            Compare(primary, i.stratumURL, i.stratumPort, i.stratumUser, L.T("Haupt-Pool"));
            Compare(fallback, i.fallbackStratumURL, i.fallbackStratumPort, i.fallbackStratumUser, L.T("Ersatz-Pool"));
        }
        else return null;
        return diffs.Count == 0 ? null : new PoolDeviation(d.Host, d.Title, diffs, primary?.Id, fallback?.Id);

        void Compare(PoolPreset? preset, string? url, int port, string? user, string label)
        {
            if (preset is null) return;
            if (NormalizeUrl(url) != NormalizeUrl(preset.Url) || port != preset.Port)
                diffs.Add(L.T("{0}: {1} statt {2} (Vorlage „{3}“)", label, $"{url}:{port}", preset.Text, preset.Name));
            else if (user is not null && user != preset.UserFor(d.Title, user))
                diffs.Add(L.T("{0}: anderer Benutzer/Wallet als in Vorlage „{1}“", label, preset.Name));
        }
    }

    public List<PoolDeviation> PoolDeviations() => Devices.Select(PoolDeviationOf).OfType<PoolDeviation>().ToList();

    /// <summary>Abweichung ist gewollt: gemerkte Vorlagen des Miners vergessen.</summary>
    public void ForgetPoolPresets(HubDevice d, string by)
    {
        d.Config.PoolPresetPrimary = null;
        d.Config.PoolPresetFallback = null;
        Config.Save();
        _poolDeviationCount.Remove(d.Host);
        d.AddLog(L.T("Pool-Vorlagen nicht mehr überwachen – {0}", by), EventCategories.Settings);
    }

    /// <summary>Alle 10 min vergleichen (im Hintergrund, die Pool-Liste kostet je Miner eine Abfrage).</summary>
    private void TickPoolDeviations(DateTime now)
    {
        if (now - _poolDeviationCheckedAt < TimeSpan.FromMinutes(10) || _poolDeviationBusy) return;
        _poolDeviationCheckedAt = now;
        _ = CheckPoolDeviationsAsync(now);
    }

    /// <summary>Pool-Listen lesen und vergleichen; zweimal in Folge abweichend → Protokoll und Push.</summary>
    private async Task CheckPoolDeviationsAsync(DateTime now)
    {
        _poolDeviationBusy = true;
        try
        {
            foreach (var d in Devices.ToList())
            {
                if (!d.State.Online || d.Applying || d.IsBenchmarkRunning) continue;   // unbekannt bzw. gerade in Arbeit
                if (d.Config.PoolPresetPrimary is null && d.Config.PoolPresetFallback is null) { _poolDeviationLayouts.Remove(d.Host); continue; }
                try { _poolDeviationLayouts[d.Host] = await PoolLayoutAsync(d); }
                catch (Exception ex) when (ex is Api.MinerApiException or HttpRequestException or NotSupportedException or System.Text.Json.JsonException or TaskCanceledException)
                {
                    continue;   // nicht erreichbar: nächste Runde
                }
                EvaluatePoolDeviation(d);
            }
        }
        finally
        {
            _poolDeviationBusy = false;
        }
    }

    private void EvaluatePoolDeviation(HubDevice d)
    {
        var dev = PoolDeviationOf(d);
        var count = _poolDeviationCount.GetValueOrDefault(d.Host);
        if (dev is null)
        {
            if (count >= 2) d.AddLog(L.T("Pools entsprechen wieder der Vorlage."), EventCategories.Settings);
            _poolDeviationCount.Remove(d.Host);
            return;
        }
        _poolDeviationCount[d.Host] = ++count;
        if (count < 2) return;   // einmal kann ein Neustart oder Umschalten sein
        var text = string.Join("; ", dev.Differences);
        if (count == 2) d.AddLog(L.T("Pools weichen von der Vorlage ab: {0}", text), EventCategories.Settings);
        SendAlert(new Alert($"pool-deviation:{d.Host}", L.T("{0}: Pool weicht von der Vorlage ab", d.Title),
            text + "\n" + L.T("Pools → „wieder angleichen“ oder „so lassen“."), NotifyPriority.Normal, TimeSpan.FromHours(24), NotifyCategory.Pool, d.Host));
    }

    internal Task CheckPoolDeviationsForTestAsync(DateTime now) => CheckPoolDeviationsAsync(now);
}
