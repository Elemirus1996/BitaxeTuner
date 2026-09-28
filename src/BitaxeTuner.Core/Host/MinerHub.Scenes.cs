using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Zustand der Sonderanzeigen – übersteht Neustarts (&lt;Datenordner&gt;/display-state.json).</summary>
public sealed class DisplaySceneState
{
    public DisplayBlockFound? BlockFound { get; set; }
    public bool BlockFoundAcknowledged { get; set; }
    public DisplayBestDiff? BestDiff { get; set; }
    public bool BestDiffShown { get; set; }
    /// <summary>Quittierte Warnungen (Art, ohne Messwerte); eine neue Warnung zeigt wieder das Vollbild.</summary>
    public string AlarmAcknowledged { get; set; } = "";
    public int PageIndex { get; set; }
}

/// <summary>
/// Anzeige-Szenen: Seiten im Wechsel (Übersicht, Tagesbilanz, Verlauf, Dauertest, Pool/Netzwerk) und Sonderanzeigen
/// mit Vorrang: Blockfund (bis Taste 1 oder Haltezeit) › Warnungen als Vollbild (bis quittiert) › Best-Diff-Rekord (einmal).
/// </summary>
public sealed partial class MinerHub
{
    private DisplaySceneState? _scene;
    private DisplayScene _lastScene = DisplayScene.Overview;
    private (DateTime At, DisplayDaily Daily)? _dailyCache;
    private (long Height, string Pool, DateTime Time, DateTime Fetched)? _network;
    private bool _networkBusy;

    private string SceneFile => Path.Combine(DataDirectory, "display-state.json");

    public DisplaySceneState SceneState
    {
        get
        {
            if (_scene is not null) return _scene;
            try { _scene = File.Exists(SceneFile) ? JsonSerializer.Deserialize<DisplaySceneState>(File.ReadAllText(SceneFile)) : null; }
            catch { _scene = null; }
            return _scene ??= new DisplaySceneState();
        }
    }

    /// <summary>Zuletzt auf dem Display gezeigte Szene.</summary>
    public DisplayScene LastDisplayScene => _lastScene;

    private void SaveSceneState()
    {
        try { File.WriteAllText(SceneFile, JsonSerializer.Serialize(SceneState)); }
        catch { /* nicht kritisch */ }
    }

    /// <summary>Warnungen ohne Messwerte – ändert sich nur, wenn eine Warnung neu kommt oder wegfällt.</summary>
    internal static string AlarmKey(IEnumerable<string> alerts) =>
        string.Join("|", alerts.Select(a => Regex.Replace(a, @"-?[\d.,]+ °C", "°C")).Order());

    // ---------- Ereignisse ----------

    /// <summary>Blockfund laut Miner (Zähler gestiegen): Vollbild, so schnell wie ein Tastendruck.</summary>
    internal void OnBlockFound(string miner, int count, DateTime now)
    {
        SceneState.BlockFound = new DisplayBlockFound(miner, now, count,
            _network is { } n && now - n.Fetched < TimeSpan.FromMinutes(5) ? n.Height : null);
        SceneState.BlockFoundAcknowledged = false;
        SaveSceneState();
        if (Config.Display.Enabled && Config.Display.BlockFoundScreen) _displayUserRequested = true;
        _ = RefreshNetworkAsync(force: true);
    }

    /// <summary>Neuer Best-Diff-Rekord eines Miners (nicht der Ersteintrag).</summary>
    internal void OnBestDiffRecord(string miner, string coin, string previous, string current, DateTime now)
    {
        SceneState.BestDiff = new DisplayBestDiff(miner, coin, previous, current, now);
        SceneState.BestDiffShown = false;
        SaveSceneState();
    }

    /// <summary>Taste 1 / Browser: Sonderanzeige quittieren oder zur nächsten Seite.</summary>
    private void AdvanceDisplayScene(string source)
    {
        var st = SceneState;
        switch (_lastScene)
        {
            case DisplayScene.BlockFound:
                st.BlockFoundAcknowledged = true;
                RaiseStatus(true, L.T("Blockfund-Anzeige quittiert ({0}).", source));
                break;
            case DisplayScene.Alarm:
                st.AlarmAcknowledged = AlarmKey(BuildDisplayModel(Options.Clock?.Invoke() ?? DateTime.Now).Alerts);
                RaiseStatus(true, L.T("Warnungen auf der Anzeige quittiert ({0}).", source));
                break;
            case DisplayScene.BestDiff:
                st.BestDiffShown = true;
                break;
            default:
                st.PageIndex++;
                break;
        }
        SaveSceneState();
    }

    // ---------- Szene wählen ----------

    /// <summary>Seiten, die gerade in Frage kommen (Dauertest nur, wenn einer läuft).</summary>
    private List<DisplayScene> EnabledPages()
    {
        var p = Config.Display.Pages;
        var list = new List<DisplayScene>();
        if (p.Overview) list.Add(DisplayScene.Overview);
        if (p.Daily && History is not null) list.Add(DisplayScene.Daily);
        if (p.Chart && History is not null) list.Add(DisplayScene.Chart);
        if (p.Soak && Devices.Any(d => d.Config.Soak is not null)) list.Add(DisplayScene.Soak);
        if (p.Network) list.Add(DisplayScene.Network);
        if (list.Count == 0) list.Add(DisplayScene.Overview);
        return list;
    }

    /// <summary>Anzeige zusammenstellen. <paramref name="nextPage"/>: regelmäßiger Seitenwechsel.</summary>
    public DisplayModel ComposeDisplay(DateTime now, bool nextPage = false)
    {
        var s = Config.Display;
        var st = SceneState;
        var model = BuildDisplayModel(now);

        if (s.BlockFoundScreen && st.BlockFound is { } bf && !st.BlockFoundAcknowledged
            && now - bf.Time < TimeSpan.FromHours(Math.Max(1, s.BlockFoundHoldHours)))
            return model with { Scene = DisplayScene.BlockFound, BlockFound = bf };
        if (s.AlarmFullscreen && model.Alerts.Count > 0 && AlarmKey(model.Alerts) != st.AlarmAcknowledged)
            return model with { Scene = DisplayScene.Alarm };
        if (s.BestDiffNotice && st.BestDiff is { } bd && !st.BestDiffShown && now - bd.Time < TimeSpan.FromDays(1))
            return model with { Scene = DisplayScene.BestDiff, BestDiff = bd };

        var pages = EnabledPages();
        if (nextPage && s.RotatePages) st.PageIndex++;
        var index = ((st.PageIndex % pages.Count) + pages.Count) % pages.Count;
        return WithPage(model with { PageLabel = pages.Count > 1 ? L.T("Seite {0}/{1}", index + 1, pages.Count) : null }, pages[index], now);
    }

    /// <summary>Nach dem Anzeigen: gezeigte Szene merken, einmalige Anzeigen als gesehen markieren.</summary>
    private void SceneShown(DisplayModel m)
    {
        _lastScene = m.Scene;
        if (m.Scene == DisplayScene.BestDiff)
        {
            SceneState.BestDiffShown = true;
            SaveSceneState();
        }
        // Warnungen sind weg: Quittung verfällt, damit dieselbe Warnung später wieder als Vollbild kommt
        else if (m.Alerts.Count == 0 && SceneState.AlarmAcknowledged.Length > 0)
        {
            SceneState.AlarmAcknowledged = "";
            SaveSceneState();
        }
    }

    /// <summary>Bestimmte Szene zeigen (Vorschau im Browser); Sonderanzeigen ohne Ereignis mit Beispieldaten.</summary>
    public DisplayModel PreviewScene(DisplayScene scene, DateTime now)
    {
        var model = BuildDisplayModel(now);
        var st = SceneState;
        return scene switch
        {
            DisplayScene.BlockFound => model with
            {
                Scene = scene,
                BlockFound = st.BlockFound ?? new DisplayBlockFound(Devices.FirstOrDefault()?.Title ?? "Bitaxe", now, 1, _network?.Height, Example: true),
            },
            DisplayScene.Alarm => model with
            {
                Scene = scene,
                Alerts = model.Alerts.Count > 0 ? model.Alerts : [L.T("Beispiel: Miner offline"), L.T("Beispiel: Lüfter K1 steht")],
            },
            DisplayScene.BestDiff => model with
            {
                Scene = scene,
                BestDiff = st.BestDiff ?? new DisplayBestDiff(Devices.FirstOrDefault()?.Title ?? "Bitaxe", "BTC", 845.ToString(L.Culture) + " M", 1.23.ToString("0.00", L.Culture) + " G", now),
            },
            _ => WithPage(model, scene, now),
        };
    }

    private DisplayModel WithPage(DisplayModel model, DisplayScene page, DateTime now)
    {
        switch (page)
        {
            case DisplayScene.Daily when BuildDaily(now) is { } daily:
                return model with { Scene = page, Daily = daily };
            case DisplayScene.Chart when History is not null:
                List<Sample> pts;
                try { pts = History.Query(HistoryStore.AggregateHost, now.AddHours(-24), now, 300); }
                catch { pts = []; }
                return model with { Scene = page, Chart = pts.Select(p => new DisplayPoint(p.Time, p.HashRateGh, p.Temp)).ToList() };
            case DisplayScene.Soak:
                return model with
                {
                    Scene = page,
                    Soaks = Devices.Where(d => d.Config.Soak is not null).Select(d => new DisplaySoak(d.Title, d.SoakStatus,
                        d.Config.Soak!.StartedAt, d.Config.Soak.Until, d.Config.Soak.FrequencyMhz, d.Config.Soak.CoreVoltageMv)).ToList(),
                };
            case DisplayScene.Network:
                _ = RefreshNetworkAsync(force: false);
                return model with { Scene = page, Network = BuildNetwork() };
            default:
                return model with { Scene = DisplayScene.Overview };
        }
    }

    private DisplayDaily? BuildDaily(DateTime now)
    {
        if (History is null) return null;
        if (_dailyCache is { } c && now - c.At < TimeSpan.FromMinutes(10) && now >= c.At) return c.Daily;
        try
        {
            var from = now.AddHours(-24);
            var rows = new List<DisplayDailyRow>();
            double gh = 0, w = 0;
            foreach (var d in Devices.Where(d => !d.IsSimulated || Devices.All(x => x.IsSimulated)))
            {
                var avg = History.Average(d.Host, from, now);
                var availability = History.Availability(d.Host, from);
                if (avg is null)
                {
                    rows.Add(new DisplayDailyRow(d.Title, null, null, null, availability));
                    continue;
                }
                gh += avg.HashRateGh;
                w += avg.Power;
                rows.Add(new DisplayDailyRow(d.Title, avg.HashRateGh, avg.HashRateGh > 0 ? avg.Power / (avg.HashRateGh / 1000) : null, avg.Temp, availability));
            }
            var kwh = w * 24 / 1000.0;
            var best = BestDiffs.Where(r => Devices.Any(d => d.Host == r.Host)).OrderByDescending(r => r.Value).FirstOrDefault();
            var daily = new DisplayDaily(gh, w, kwh, kwh * Config.ElectricityCtPerKwh / 100.0, Config.Currency, best?.Raw,
                best is null ? null : Device(best.Host)?.Title, rows);
            _dailyCache = (now, daily);
            return daily;
        }
        catch
        {
            return null;
        }
    }

    private DisplayNetwork BuildNetwork()
    {
        var pools = Devices.Select(d =>
        {
            var i = d.State.Online ? d.State.Info : null;
            var fallback = i is not null && i.isUsingFallbackStratum != 0;
            var pool = i is null ? "–" : fallback ? $"{i.fallbackStratumURL}:{i.fallbackStratumPort}" : $"{i.stratumURL}:{i.stratumPort}";
            var best = BestDiffs.Where(r => r.Host == d.Host).OrderByDescending(r => r.Value).FirstOrDefault()?.Raw;
            return new DisplayPool(d.Title, i is not null, pool, fallback, i?.sharesAccepted ?? 0, i?.sharesRejected ?? 0, best);
        }).ToList();
        return new DisplayNetwork(pools, _network?.Height, _network?.Pool, _network?.Time);
    }

    /// <summary>Blockhöhe und letzter Block (mempool.space), höchstens alle 10 Minuten.</summary>
    private async Task RefreshNetworkAsync(bool force)
    {
        if (!Options.OnlineChecks || _networkBusy) return;
        var now = DateTime.Now;
        if (!force && _network is { } n && now - n.Fetched < TimeSpan.FromMinutes(10)) return;
        _networkBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var blocks = await NetworkClient.GetBlocksAsync(cts.Token);
            if (blocks.OrderByDescending(b => b.Height).FirstOrDefault() is { } top)
            {
                _network = (top.Height, top.Pool, top.Time.ToLocalTime(), now);
                // Blockfund ohne Höhe (Netzwerkstand war veraltet): nachtragen, wenn der Fund frisch ist
                if (SceneState.BlockFound is { Height: null } bf && now - bf.Time < TimeSpan.FromMinutes(10))
                {
                    SceneState.BlockFound = bf with { Height = top.Height };
                    SaveSceneState();
                }
            }
        }
        catch { /* ohne Netzwerkdaten weiter */ }
        finally
        {
            _networkBusy = false;
        }
    }
}
