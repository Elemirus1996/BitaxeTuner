using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Tax.Models;
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
    /// <summary>0.9.11: seit wann die aktuellen Warnungen (Schlüssel) als Vollbild stehen – für „alle nach Stunden“.</summary>
    public string AlarmSinceKey { get; set; } = "";
    public DateTime? AlarmSince { get; set; }
    /// <summary>0.9.11: wann der Best-Diff-Rekord zuerst gezeigt wurde.</summary>
    public DateTime? BestDiffSince { get; set; }
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
    private (long Height, string? Pool, DateTime Time)? _bchNetwork;
    private bool _networkBusy;

    private string SceneFile => Path.Combine(DataDirectory, "display-state.json");

    // 0.9.11 mehrere Anzeigen: Während eine weitere Anzeige zusammengestellt wird, gelten ihre Einstellungen, ihr Zustand
    // und ihre Gruppe (Hub-Thread, synchron – danach wieder die erste Anzeige).
    private ExtraDisplayRuntime? _extraContext;
    private DisplaySettings Ds => _extraContext?.Config.Settings ?? Config.Display;
    private DisplaySceneState CurState => _extraContext?.State ?? SceneState;
    private string? CurGroup => _extraContext?.Config.Group is { Length: > 0 } g ? g : null;

    private T WithDisplay<T>(ExtraDisplayRuntime? display, Func<T> f)
    {
        var before = _extraContext;
        _extraContext = display;
        try { return f(); }
        finally { _extraContext = before; }
    }

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
        try
        {
            if (_extraContext is { } x) File.WriteAllText(ExtraSceneFile(x.Config.Id), JsonSerializer.Serialize(x.State));
            else File.WriteAllText(SceneFile, JsonSerializer.Serialize(SceneState));
        }
        catch { /* nicht kritisch */ }
    }

    /// <summary>Warnungen ohne Messwerte – ändert sich nur, wenn eine Warnung neu kommt oder wegfällt.</summary>
    internal static string AlarmKey(IEnumerable<string> alerts) =>
        string.Join("|", alerts.Select(a => Regex.Replace(a, @"-?[\d.,]+ °C", "°C")).Order());

    // ---------- Ereignisse ----------

    /// <summary>Blockfund laut Miner (Zähler gestiegen): Vollbild, so schnell wie ein Tastendruck.</summary>
    internal void OnBlockFound(string miner, int count, DateTime now)
    {
        var found = new DisplayBlockFound(miner, now, count, _network is { } n && now - n.Fetched < TimeSpan.FromMinutes(5) ? n.Height : null);
        SceneState.BlockFound = found;
        SceneState.BlockFoundAcknowledged = false;
        SaveSceneState();
        if (Config.Display.Enabled && Config.Display.BlockFoundScreen) _displayUserRequested = true;
        foreach (var x in ExtraRuntimes())
        {
            x.State.BlockFound = found;
            x.State.BlockFoundAcknowledged = false;
            WithDisplay(x, () => { SaveSceneState(); return 0; });
            if (x.Config.Settings.BlockFoundScreen) x.UserRequested = true;
        }
        _ = RefreshNetworkAsync(force: true);
    }

    /// <summary>Neuer Best-Diff-Rekord eines Miners (nicht der Ersteintrag).</summary>
    internal void OnBestDiffRecord(string miner, string coin, string previous, string current, DateTime now)
    {
        var record = new DisplayBestDiff(miner, coin, previous, current, now);
        foreach (var st in new[] { SceneState }.Concat(ExtraRuntimes().Select(x => x.State)))
        {
            st.BestDiff = record;
            st.BestDiffShown = false;
            st.BestDiffSince = null;
        }
        SaveSceneState();
        foreach (var x in ExtraRuntimes()) WithDisplay(x, () => { SaveSceneState(); return 0; });
    }

    /// <summary>Taste 1 / Browser: Sonderanzeige quittieren oder zur nächsten Seite.</summary>
    private void AdvanceDisplayScene(string source)
    {
        var st = CurState;
        switch (_extraContext?.LastScene ?? _lastScene)
        {
            case DisplayScene.BlockFound:
                st.BlockFoundAcknowledged = true;
                RaiseStatus(true, L.T("Blockfund-Anzeige quittiert ({0}).", source));
                break;
            case DisplayScene.Alarm:
                st.AlarmAcknowledged = AlarmKey(BuildDisplayModel(Options.Clock?.Invoke() ?? DateTime.Now, CurGroup).Alerts);
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

    /// <summary>Eine Seite im Wechsel; Group nur bei der Gruppenseite.</summary>
    private readonly record struct DisplayPage(DisplayScene Scene, string? Group = null);

    /// <summary>Seiten, die gerade in Frage kommen (Dauertest nur, wenn einer läuft; je Gruppe eine Seite).</summary>
    private List<DisplayPage> EnabledPages()
    {
        var p = Ds.Pages;
        var list = new List<DisplayPage>();
        if (p.Overview) list.Add(new(DisplayScene.Overview));
        if (p.Groups) list.AddRange(MinerGroups.All(Config.Devices).Select(g => new DisplayPage(DisplayScene.Group, g)));
        if (p.Daily && History is not null) list.Add(new(DisplayScene.Daily));
        if (p.Chart && History is not null) list.Add(new(DisplayScene.Chart));
        if (p.Monthly && History is not null) list.Add(new(DisplayScene.Monthly));
        if (p.Prices) list.Add(new(DisplayScene.Prices));
        if (p.Power) list.Add(new(DisplayScene.Power));
        if (p.Sensors && Config.Fans.Sensors.Count > 0) list.Add(new(DisplayScene.Sensors));
        if (p.Soak && Devices.Any(d => d.Config.Soak is not null)) list.Add(new(DisplayScene.Soak));
        if (p.Network) list.Add(new(DisplayScene.Network));
        if (p.Qr) list.Add(new(DisplayScene.Qr));
        if (p.News) list.Add(new(DisplayScene.News));
        if (list.Count == 0) list.Add(new(DisplayScene.Overview));
        return list;
    }

    /// <summary>Anzeige zusammenstellen. <paramref name="nextPage"/>: regelmäßiger Seitenwechsel.</summary>
    public DisplayModel ComposeDisplay(DateTime now, bool nextPage = false)
    {
        var s = Ds;
        var st = CurState;
        var model = BuildDisplayModel(now, CurGroup);

        // 0.9.11: wie Sonderanzeigen enden – je Anzeige („each“) oder für alle gleich („hours“ / „button“)
        var hold = TimeSpan.FromHours(Math.Max(1, s.BlockFoundHoldHours));
        var blockUntil = s.SpecialUntil is "hours" or "button" ? s.SpecialUntil : s.BlockFoundUntil;
        if (s.BlockFoundScreen && st.BlockFound is { } bf && !st.BlockFoundAcknowledged
            && (blockUntil == "button" || now - bf.Time < hold))
            return model with { Scene = DisplayScene.BlockFound, BlockFound = bf };
        if (s.AlarmFullscreen && model.Alerts.Count > 0 && AlarmKey(model.Alerts) is var alarmKey && alarmKey != st.AlarmAcknowledged)
        {
            if (st.AlarmSinceKey != alarmKey)
            {
                st.AlarmSinceKey = alarmKey;
                st.AlarmSince = now;
                SaveSceneState();
            }
            // „alle nach Stunden“: danach nur noch als rote Zeile, wie quittiert
            if (s.SpecialUntil != "hours" || st.AlarmSince is not { } since || now - since < hold)
                return model with { Scene = DisplayScene.Alarm };
        }
        if (s.BestDiffNotice && st.BestDiff is { } bd && !st.BestDiffShown && now - bd.Time < TimeSpan.FromDays(s.SpecialUntil == "button" ? 30 : 1))
        {
            // „each“: einmal; „hours“: bis Taste 1 oder Haltezeit; „button“: bis Taste 1
            if (s.SpecialUntil != "hours" || now - (st.BestDiffSince ??= now) < hold)
                return model with { Scene = DisplayScene.BestDiff, BestDiff = bd };
        }

        var pages = EnabledPages();
        if (nextPage && s.RotatePages) st.PageIndex++;
        var index = ((st.PageIndex % pages.Count) + pages.Count) % pages.Count;
        return WithPage(model with { PageLabel = pages.Count > 1 ? L.T("Seite {0}/{1}", index + 1, pages.Count) : null }, pages[index].Scene, now, pages[index].Group);
    }

    /// <summary>Nach dem Anzeigen: gezeigte Szene merken, einmalige Anzeigen als gesehen markieren.</summary>
    internal void SceneShown(DisplayModel m)
    {
        if (_extraContext is { } x) x.LastScene = m.Scene; else _lastScene = m.Scene;
        // Best-Diff-Rekord: nur bei „je Anzeige“ nach einmaligem Zeigen erledigt, sonst bis Taste 1 bzw. Haltezeit
        if (m.Scene == DisplayScene.BestDiff && Ds.SpecialUntil is not ("hours" or "button"))
        {
            CurState.BestDiffShown = true;
            SaveSceneState();
        }
        // Warnungen sind weg: Quittung verfällt, damit dieselbe Warnung später wieder als Vollbild kommt
        else if (m.Alerts.Count == 0 && CurState.AlarmAcknowledged.Length > 0)
        {
            CurState.AlarmAcknowledged = "";
            SaveSceneState();
        }
    }

    /// <summary>Bestimmte Szene zeigen (Vorschau im Browser); Sonderanzeigen ohne Ereignis mit Beispieldaten.</summary>
    public DisplayModel PreviewScene(DisplayScene scene, DateTime now)
    {
        var model = BuildDisplayModel(now, CurGroup);
        var st = CurState;
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
            _ => WithPage(model, scene, now, scene == DisplayScene.Group ? MinerGroups.All(Config.Devices).FirstOrDefault() : null),
        };
    }

    private DisplayModel WithPage(DisplayModel model, DisplayScene page, DateTime now, string? group = null)
    {
        switch (page)
        {
            case DisplayScene.Group:
                // ohne Gruppen (nur Vorschau): alle Miner
                return (group is null ? model : BuildDisplayModel(now, group) with { PageLabel = model.PageLabel }) with { Scene = DisplayScene.Group };
            case DisplayScene.Daily when BuildDaily(now) is { } daily:
                return model with { Scene = page, Daily = daily, DailySeries = BuildDailySeries(now) };
            case DisplayScene.Monthly when BuildMonthly(now) is { } monthly:
                return model with { Scene = page, Monthly = monthly };
            case DisplayScene.Prices:
                _ = RefreshMarketAsync(force: false);
                return model with { Scene = page, Coins = BuildCoins(), Difficulty = Ds.PriceCoins != "bch" ? _difficulty?.Data : null };
            case DisplayScene.Power:
                return model with { Scene = page, Power = BuildPower(now) };
            case DisplayScene.Qr:
                return model with { Scene = page, Qr = BuildQr() };
            case DisplayScene.News:
                _ = RefreshNewsAsync(force: false);
                return model with { Scene = page, News = News.Items(Ds.NewsKinds, 6) };
            case DisplayScene.Sensors:
                return model with
                {
                    Scene = page,
                    Sensors = (FanStatus.Sensors ?? []).Select(s => new DisplaySensor(s.Name, s.Temp, s.WarnTemp, s.Hot)).ToList(),
                };
            case DisplayScene.Chart when History is not null:
                List<Sample> pts;
                try { pts = History.Query(HistoryStore.AggregateHost, now.AddHours(-24), now, 300); }
                catch { pts = []; }
                // 0.9.11: wählbarer Graph – der Wert steht jeweils im Feld Gh
                var kind = Ds.HistoryChart is "temp" or "power" or "efficiency" ? Ds.HistoryChart : "hashrate";
                return model with
                {
                    Scene = page,
                    ChartKind = kind,
                    Chart = pts.Select(p => new DisplayPoint(p.Time, kind switch
                    {
                        "temp" => p.Temp,
                        "power" => p.Power,
                        "efficiency" => p.HashRateGh > 1 ? p.Power / (p.HashRateGh / 1000) : 0,
                        _ => p.HashRateGh,
                    }, p.Temp)).ToList(),
                };
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
            if (Config.Plugs.Items.Count > 0)
            {
                var avgs = Devices.Select(d => (d.Host, Avg: History.Average(d.Host, from, now))).Where(x => x.Avg is not null)
                    .ToDictionary(x => x.Host, x => x.Avg!.Power, StringComparer.OrdinalIgnoreCase);
                var energy = Plugs.EnergyBalance.FromHistory(History, Config.Plugs, avgs, from, now);
                if (energy.FromPlugs) w = energy.TotalPowerW;
            }
            var kwh = w * 24 / 1000.0;
            var cost = kwh * Plugs.EnergyCost.FixedCt(Config) / 100.0;
            if (Config.PriceSource.DynamicCosts &&
                Plugs.EnergyCost.Compute(History, Config, Devices.Select(d => d.Host).ToList(), from, now) is { Kwh: > 0 } dyn)
                (kwh, cost) = (dyn.Kwh, dyn.Cost);
            var best = BestDiffs.Where(r => Devices.Any(d => d.Host == r.Host)).OrderByDescending(r => r.Value).FirstOrDefault();
            var daily = new DisplayDaily(gh, w, kwh, cost, Config.Currency, best?.Raw,
                best is null ? null : Device(best.Host)?.Title, rows);
            _dailyCache = (now, daily);
            return daily;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Neue Seiten (0.9.7) ----------

    private (DateTime Fetched, Network.DifficultyDto Dto, DisplayDifficulty Data)? _difficulty;
    private readonly Dictionary<CoinType, (DateTime Fetched, List<(DateTime Time, double Eur)> Points)> _coinCharts = [];
    private string _coinChartCurrency = Currencies.Euro;   // 0.9.11: Währung der gespeicherten Kursverläufe
    private bool _marketBusy;
    private (DateTime At, string Key, DisplayMonthly Data)? _monthlyCache;

    private IEnumerable<CoinType> DisplayCoins() => Ds.PriceCoins switch
    {
        "bch" => [CoinType.BitcoinCash],
        "both" => [CoinType.Bitcoin, CoinType.BitcoinCash],
        _ => [CoinType.Bitcoin],
    };

    private List<DisplayCoin> BuildCoins() => DisplayCoins().Select(c =>
    {
        var pts = _coinCharts.TryGetValue(c, out var v) ? v.Points : [];
        double? now = pts.Count > 0 ? pts[^1].Eur : null;
        double? change = pts.Count > 1 && pts[0].Eur > 0 ? (pts[^1].Eur / pts[0].Eur - 1) * 100 : null;
        return new DisplayCoin(c.Symbol(), c == CoinType.Bitcoin ? "Bitcoin (BTC)" : "Bitcoin Cash (BCH)", now, change,
            pts.Select(p => new DisplayValue(p.Time, p.Eur)).ToList(), Currencies.Get(_coinChartCurrency).Symbol);
    }).ToList();

    /// <summary>Kurse (CoinGecko) und Difficulty (mempool.space) höchstens alle 10 Minuten holen – nur für die Kurs-Seite.</summary>
    private async Task RefreshMarketAsync(bool force)
    {
        if (!Options.OnlineChecks || _marketBusy) return;
        var now = DateTime.Now;
        _marketBusy = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var currency = Currencies.Of(Config).Code;
            if (currency != _coinChartCurrency) { _coinCharts.Clear(); _coinChartCurrency = currency; }   // Währung gewechselt
            foreach (var coin in DisplayCoins())
            {
                if (!force && _coinCharts.TryGetValue(coin, out var c) && now - c.Fetched < TimeSpan.FromMinutes(10)) continue;
                var pts = await CoinGecko.GetDayChartAsync(coin, currency, cts.Token);
                if (pts.Count > 0) _coinCharts[coin] = (now, pts);
            }
            if (Ds.PriceCoins != "bch" && (force || _difficulty is not { } d || now - d.Fetched >= TimeSpan.FromMinutes(10)))
            {
                var dto = await NetworkClient.GetDifficultyAsync(cts.Token);
                _difficulty = (now, dto, new DisplayDifficulty(dto.ProgressPercent, dto.ExpectedChangePercent, dto.RemainingBlocks, dto.Eta));
            }
        }
        catch { /* ohne Kursdaten weiter */ }
        finally
        {
            _marketBusy = false;
        }
    }

    /// <summary>24-h-Graph der Tagesbilanz (Summe aller Miner) – nur, wenn in den Einstellungen gewählt.</summary>
    private DisplaySeries? BuildDailySeries(DateTime now)
    {
        var kind = Ds.DailyChart;
        if (History is null || kind is not ("hashrate" or "power" or "efficiency" or "temp")) return null;
        List<Monitoring.Sample> pts;
        try { pts = History.Query(HistoryStore.AggregateHost, now.AddHours(-24), now, 300); }
        catch { pts = []; }
        var hash = pts.Count > 0 && pts.Max(p => p.HashRateGh) >= 1000;
        return kind switch
        {
            "power" => new DisplaySeries(L.T("Leistung"), "W", "0", pts.Where(p => p.Power > 0).Select(p => new DisplayValue(p.Time, p.Power)).ToList()),
            "efficiency" => new DisplaySeries(L.T("Effizienz"), "J/TH", "0.0",
                pts.Where(p => p.HashRateGh > 1 && p.Power > 0).Select(p => new DisplayValue(p.Time, p.Power / (p.HashRateGh / 1000))).ToList()),
            "temp" => new DisplaySeries(L.T("Höchste Chip-Temperatur"), "°C", "0", pts.Where(p => p.Temp > 0).Select(p => new DisplayValue(p.Time, p.Temp)).ToList()),
            _ => new DisplaySeries(L.T("Hashrate"), hash ? "TH/s" : "GH/s", hash ? "0.00" : "0",
                pts.Where(p => p.HashRateGh > 0).Select(p => new DisplayValue(p.Time, hash ? p.HashRateGh / 1000 : p.HashRateGh)).ToList()),
        };
    }

    /// <summary>Monatsbilanz des laufenden Monats (höchstens alle 30 Minuten neu berechnet).</summary>
    private DisplayMonthly? BuildMonthly(DateTime now)
    {
        if (History is null) return null;
        var kind = Ds.MonthlyChart is "cost" or "income" or "hashrate" ? Ds.MonthlyChart : "kwh";
        var key = $"{now:yyyy-MM}|{kind}";
        if (_monthlyCache is { } c && c.Key == key && now - c.At < TimeSpan.FromMinutes(30) && now >= c.At) return c.Data;
        try
        {
            var report = BuildReport(now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), now);
            var start = new DateTime(now.Year, now.Month, 1);
            var hosts = ReportMiners().Select(m => m.Host).ToList();
            List<MinedReward> rewards;
            try { rewards = TaxRepository.LoadRewards(); } catch { rewards = []; }
            var bars = new List<DisplayValue>();
            for (var day = start; day <= now.Date; day = day.AddDays(1))
            {
                var end = day.AddDays(1) < now ? day.AddDays(1) : now;
                double value = kind switch
                {
                    "hashrate" => History.Average(HistoryStore.AggregateHost, day, end)?.HashRateGh ?? 0,
                    // Audit N-F4: Zuflüsse je Tag in Steuerzeit – wie die Monatssumme (Steuer-Zeitzone)
                    "income" => (double)rewards.Where(r => r.ReceivedAtLocal.Date == day).Sum(r => r.ValueIn(report.IncomeCode) ?? 0),
                    "cost" => Plugs.EnergyCost.Compute(History, Config, hosts, day, end).Cost,
                    _ => Plugs.EnergyCost.Compute(History, Config, hosts, day, end).Kwh,
                };
                if (value > 0) bars.Add(new DisplayValue(day, value));
            }
            var tera = kind == "hashrate" && bars.Any(b => b.Value >= 1000);
            if (tera) bars = bars.Select(b => b with { Value = b.Value / 1000 }).ToList();
            var (label, format) = kind switch
            {
                "hashrate" => (tera ? L.T("Ø Hashrate je Tag (TH/s)") : L.T("Ø Hashrate je Tag (GH/s)"), tera ? "0.00" : "0"),
                "income" => (L.T("Ertrag je Tag ({0})", report.IncomeSymbol), "0.00"),
                "cost" => (L.T("Stromkosten je Tag ({0})", Config.Currency), "0.00"),
                _ => (L.T("Strom je Tag (kWh)"), "0.0"),
            };
            var data = new DisplayMonthly(start, report.Partial, report.Energy.Kwh, report.Energy.Cost, report.Currency, (double)report.IncomeEur,
                report.Income.Sum(i => i.EurMissing), report.TotalAvgHashGh, label, format, bars, report.IncomeSymbol);
            _monthlyCache = (now, key, data);
            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Strompreise der nächsten 24 Stunden (Stundenmittel) und das günstigste 3-Stunden-Fenster.</summary>
    private DisplayPower BuildPower(DateTime now)
    {
        var utc = now.ToUniversalTime();
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var hours = Prices.Prices.Where(p => p.EndUtc > utc && p.StartUtc < utc.AddHours(24))
            .GroupBy(p => p.StartUtc.ToLocalTime() is var t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0) : default)
            .Where(g => g.Key >= hourStart)
            .OrderBy(g => g.Key)
            .Select(g => new DisplayValue(g.Key, g.Average(p => p.CtPerKwh)))
            .Take(24).ToList();
        DateTime? cheapFrom = null;
        double? cheapAvg = null;
        for (var i = 0; i + 3 <= hours.Count; i++)
        {
            if (hours[i + 2].Time - hours[i].Time != TimeSpan.FromHours(2)) continue;   // nur zusammenhängende Stunden
            var avg = (hours[i].Value + hours[i + 1].Value + hours[i + 2].Value) / 3;
            if (cheapAvg is null || avg < cheapAvg) (cheapFrom, cheapAvg) = (hours[i].Time, avg);
        }
        return new DisplayPower(Prices.PriceAt(utc), hours, cheapFrom, cheapAvg, Prices.SourceName, Currencies.Of(Config).Cent);
    }

    /// <summary>QR-Code zur Browser-Oberfläche: eigene Adresse aus den Einstellungen oder die dieses Servers.</summary>
    private DisplayQr BuildQr()
    {
        var url = Ds.QrUrl is { Length: > 0 } own ? own.Trim()
            : Options.WebUrl?.Invoke() ?? Web.WebViewServer.LocalUrls(Config.WebView.Port).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url) || url.Length > 200) return new DisplayQr("", []);
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix.Select(r => Enumerable.Range(0, r.Length).Select(i => r[i]).ToArray()).ToList();
        return new DisplayQr(url, rows);
    }

    private DisplayNetwork BuildNetwork()
    {
        var pools = Devices.Select(d =>
        {
            var i = d.State.Online ? d.State.Info : null;
            var fallback = i is not null && i.isUsingFallbackStratum != 0;
            var pool = i is null ? "–" : fallback ? $"{i.fallbackStratumURL}:{i.fallbackStratumPort}" : $"{i.stratumURL}:{i.stratumPort}";
            var best = BestDiffs.Where(r => r.Host == d.Host).OrderByDescending(r => r.Value).FirstOrDefault()?.Raw;
            return new DisplayPool(d.Title, i is not null, pool, fallback, i?.sharesAccepted ?? 0, i?.sharesRejected ?? 0, best,
                d.State.Online ? d.State.Normalized?.PoolDifficulty : null);
        }).ToList();
        return new DisplayNetwork(pools, _network?.Height, _network?.Pool, _network?.Time,
            _bchNetwork?.Height, _bchNetwork?.Pool, _bchNetwork?.Time, Ds.NetworkBch);
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
        // 0.9.11: letzter Bitcoin-Cash-Block (Blockchair) – mit dem BTC-Netzwerk alle 10 Minuten
        if (Ds.NetworkBch)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (await Blockchair.GetLatestBlockAsync(Tax.Models.CoinType.BitcoinCash, cts.Token) is { } bch)
                    _bchNetwork = (bch.Height, bch.Miner, bch.TimeUtc.ToLocalTime());
            }
            catch { /* ohne BCH-Daten weiter */ }
        }
        _networkBusy = false;
    }
}
