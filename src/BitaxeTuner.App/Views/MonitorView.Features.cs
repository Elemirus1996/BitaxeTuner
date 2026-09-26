using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Erweiterungen des Hauptfensters: dauerhafter Verlauf, Push-Benachrichtigungen,
/// Watchdog, Firmware-Check, Best-Diff-Rekorde, Solo-Chancen und Tray-Symbol.
/// Eingehängt über InitFeatures (Konstruktor), AfterPoll (nach jeder Abfrage)
/// und DisposeFeatures (Schließen).
/// </summary>
public partial class MonitorView
{
    // Verlauf, Benachrichtigung, Solo-Chancen und Firmware-Check sind App-weit einmalig (AppHost)
    private HistoryStore? _history => _host.History;
    private NotificationService _notify => _host.Notify;
    private SoloOddsService _odds => _host.Odds;
    private FirmwareChecker _firmware => _host.Firmware;
    private TrayService? _tray;
    private readonly Watchdog _watchdog = new();

    private readonly Dictionary<string, DateTime> _lastHistoryWrite = new();
    private DateTime _lastAggWrite = DateTime.MinValue;
    private DateTime _lastPrune = DateTime.MinValue;
    private DateTime _lastFirmwareRefresh = DateTime.MinValue;

    private readonly Dictionary<string, int> _failCount = new();
    private readonly HashSet<string> _offlineNotified = new();
    private readonly Dictionary<string, int> _blockFoundSeen = new();

    private List<BestDiffRecord> _bestDiffs = new();
    private readonly Dictionary<string, (DateTime At, double? Value)> _availabilityCache = new();
    private readonly Dictionary<string, (DateTime At, List<Sample> Data)> _chartCache = new();
    private string _chartRange = "1h";
    private bool _trayHintShown;

    // ---------- Lebenszyklus ----------

    private void InitFeatures()
    {
        if (_history is not null)
        {
            try
            {
                _history.Prune(_config.HistoryDays);
                _bestDiffs = _history.GetBestDiffs();
                _lastPrune = DateTime.Now;
            }
            catch (Exception ex)
            {
                SetStatus(false, "Verlaufsdatenbank nicht verfügbar: " + ex.Message);
            }
        }
        else
        {
            SetStatus(false, "Verlaufsdatenbank nicht verfügbar: " + _host.HistoryError);
        }

        _tray = new TrayService();
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += () => OwnerWindow.Close();

        // Dokumentierter Zufluss aus dem Steuer-Modul → Push (läuft auf einem Pool-Thread)
        _taxMonitor.NewRewardDetected += reward =>
        {
            if (!_config.Notifications.OnFinds) return;
            if (DateTime.UtcNow - reward.ReceivedAtUtc > TimeSpan.FromHours(6)) return; // Erstimport alter Eingänge

            var eur = reward.EurValue is { } v ? $" ≈ {v.ToString("N2", De)} €" : "";
            _ = _notify.SendAsync($"reward:{reward.Coin.Symbol()}:{reward.TxId}", "Zufluss dokumentiert",
                $"{reward.Amount.ToString("0.00000000", De)} {reward.Coin.Symbol()} auf {reward.WalletLabel}{eur}",
                NotifyPriority.High, TimeSpan.FromDays(365));
        };
    }

    private void DisposeFeatures()
    {
        // Verlauf, Benachrichtigung und Firmware-Check gibt der AppHost frei
        _tray?.Dispose();
    }

    /// <summary>Fensterbezogene Teile (Tray beim Minimieren), sobald die Ansicht im Hauptfenster hängt.</summary>
    private void AttachWindow()
    {
        var window = OwnerWindow;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized && _config.MinimizeToTray) HideToTray();
        };

        // "Minimiert starten" setzt WindowState vor dem Anzeigen, dabei feuert kein StateChanged
        if (window.WindowState == WindowState.Minimized && _config.MinimizeToTray) HideToTray();
    }

    /// <summary>Nach jeder Miner-Abfrage auf dem UI-Thread.</summary>
    private void AfterPoll(DateTime now)
    {
        RecordHistory(now);
        CheckHealth();
        CheckPools(now);
        CheckDailyReport(now);
        CheckBestDiffs();
        RunWatchdog();
        UpdateTray();
        _ = RefreshFirmwareAsync();

        if ((now - _lastPrune).TotalHours >= 24)
        {
            try { _history?.Prune(_config.HistoryDays); } catch { /* nicht kritisch */ }
            _lastPrune = now;
        }
    }

    // ---------- Verlauf ----------

    /// <summary>Höchstens ein Datenpunkt je Minute und Miner, plus Summe unter "*".</summary>
    private void RecordHistory(DateTime now)
    {
        if (_history is null) return;

        try
        {
            foreach (var s in _states)
            {
                var host = s.Config.Host;
                if (AppHost.IsSimulated(host)) continue;
                if (_lastHistoryWrite.TryGetValue(host, out var last) && (now - last).TotalSeconds < 60) continue;
                _lastHistoryWrite[host] = now;

                if (s.Online && s.Info is { } i) _history.AddSample(host, now, i.hashRate, i.temp, i.power, true);
                else _history.AddSample(host, now, 0, 0, 0, false);
            }

            if ((now - _lastAggWrite).TotalSeconds >= 60 && _states.Count > 0)
            {
                _lastAggWrite = now;
                var online = _states.Where(s => s.Online && s.Info is not null && !AppHost.IsSimulated(s.Config.Host))
                                    .Select(s => s.Info!).ToList();
                _history.AddSample(HistoryStore.AggregateHost, now,
                    online.Sum(i => i.hashRate),
                    online.Count > 0 ? online.Max(i => i.temp) : 0,
                    online.Sum(i => i.power),
                    online.Count > 0);
            }
        }
        catch (Exception ex)
        {
            SetStatus(false, "Verlauf konnte nicht gespeichert werden: " + ex.Message);
        }
    }

    private void ChartRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string range }) return;
        _chartRange = range;
        _chartCache.Clear();
        RenderSelected();
    }

    /// <summary>1 h: Live-Puffer im Speicher. Längere Zeiträume: aus der Datenbank, 60 s gecacht.</summary>
    private IReadOnlyList<Sample> ChartData(string host, IReadOnlyList<Sample> live)
    {
        if (_chartRange == "1h" || _history is null)
        {
            ChartRangeText.Text = _history is null ? "nur live (Datenbank nicht verfügbar)" : "live";
            return live;
        }

        var span = _chartRange switch
        {
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(30)
        };

        var key = host + "|" + _chartRange;
        if (!_chartCache.TryGetValue(key, out var cached) || (DateTime.Now - cached.At).TotalSeconds > 60)
        {
            try
            {
                cached = (DateTime.Now, _history.Query(host, DateTime.Now - span, DateTime.Now));
            }
            catch (Exception ex)
            {
                ChartRangeText.Text = "Datenbankfehler: " + ex.Message;
                return live;
            }
            _chartCache[key] = cached;
        }

        ChartRangeText.Text = cached.Data.Count == 0
            ? "noch keine Daten in diesem Zeitraum"
            : $"{cached.Data.Count} Punkte aus der Datenbank";
        return cached.Data;
    }

    /// <summary>Anteil der Minuten online in den letzten 7 Tagen, 60 s gecacht.</summary>
    private double? Availability(string host)
    {
        if (_history is null) return null;
        if (_availabilityCache.TryGetValue(host, out var c) && (DateTime.Now - c.At).TotalSeconds < 60) return c.Value;

        double? value;
        try { value = _history.Availability(host, DateTime.Now.AddDays(-7)); }
        catch { value = null; }

        _availabilityCache[host] = (DateTime.Now, value);
        return value;
    }

    private string AvailabilityText(string host)
        => Availability(host) is { } a ? $"verfügbar 7 T: {(a * 100).ToString("0.0", De)} %" : "";

    /// <summary>Mittlere Verfügbarkeit aller Miner.</summary>
    private string AggregateAvailabilityText()
    {
        var values = _states.Select(s => Availability(s.Config.Host)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? "" : $"Ø verfügbar 7 T: {(values.Average() * 100).ToString("0.0", De)} %";
    }

    /// <summary>Beschriftung der Zeitachse: Minuten, Stunden oder Tage.</summary>
    private static string SpanText(TimeSpan span)
        => span.TotalHours < 2 ? $"{span.TotalMinutes.ToString("0", De)} min"
         : span.TotalDays < 2 ? $"{span.TotalHours.ToString("0", De)} h"
         : $"{span.TotalDays.ToString("0", De)} Tage";

    // ---------- Gesundheit und Benachrichtigungen ----------

    private void CheckHealth()
    {
        var n = _config.Notifications;

        foreach (var s in _states)
        {
            var host = s.Config.Host;
            if (AppHost.IsSimulated(host)) continue;

            // Absichtlicher Eingriff (Tuning, Benchmark, gewollter Neustart): keine Offline-Zählung, keine Meldung.
            // Ist der Miner nach dem Wartungsfenster noch weg, greift wieder die normale 3-Fehlversuche-Regel.
            if (!s.Online && _host.Maintenance.IsActive(host))
            {
                _failCount[host] = 0;
                continue;
            }

            if (!s.Online)
            {
                var fails = _failCount.GetValueOrDefault(host) + 1;
                _failCount[host] = fails;

                // Erst nach drei Fehlversuchen melden, einzelne Aussetzer im WLAN sind normal
                if (fails >= 3 && n.OnOffline)
                {
                    _offlineNotified.Add(host);
                    _ = _notify.SendAsync($"offline:{host}", $"{s.Config.Name} offline",
                        $"{host} antwortet nicht ({s.Error ?? "keine Verbindung"}).",
                        NotifyPriority.High, TimeSpan.FromHours(6));
                }
                continue;
            }

            _failCount[host] = 0;
            if (_offlineNotified.Remove(host))
            {
                _notify.Reset($"offline:{host}");
                _ = _notify.SendAsync($"online:{host}", $"{s.Config.Name} wieder online",
                    $"{host} antwortet wieder.", NotifyPriority.Normal, TimeSpan.FromMinutes(1));
            }

            var i = s.Info!;
            if (n.OnOverheat && (i.temp >= _config.TempWarn || i.overheat_mode != 0))
            {
                var text = $"ASIC {i.temp.ToString("0.0", De)} °C, VR {i.vrTemp.ToString("0", De)} °C" +
                           (i.overheat_mode != 0 ? ", Overheat-Modus aktiv" : "");
                _ = _notify.SendAsync($"hot:{host}", $"{s.Config.Name} zu heiß", text,
                    NotifyPriority.Urgent, TimeSpan.FromMinutes(30));
            }

            // Blockfund laut Miner: Zähler gestiegen seit der letzten Abfrage
            if (_blockFoundSeen.TryGetValue(host, out var seen) && i.blockFound > seen && n.OnFinds)
            {
                _ = _notify.SendAsync($"block:{host}:{i.blockFound}", $"BLOCK GEFUNDEN – {s.Config.Name}",
                    $"Der Miner meldet jetzt {i.blockFound} gefundene(n) Block/Blöcke.",
                    NotifyPriority.Urgent, TimeSpan.FromDays(365));
            }
            _blockFoundSeen[host] = i.blockFound;
        }
    }

    // ---------- Pool und Shares ----------

    /// <summary>Fallback-Pool, Ablehnungsquote und Antwortzeit je Miner (nicht während Tuning/Neustart).</summary>
    private void CheckPools(DateTime now)
    {
        var settings = _config.PoolWatch;
        foreach (var s in _states)
        {
            if (!s.Online || s.Info is null || AppHost.IsSimulated(s.Config.Host)) continue;
            var alerts = _host.PoolWatch.Evaluate(s.Config.Host, s.Config.Name, s.Info, now, settings,
                _host.Maintenance.IsActive(s.Config.Host));
            foreach (var a in alerts)
            {
                SetStatus(false, $"{a.Title}: {a.Message}");
                if (_config.Notifications.OnPool) _host.SendAlert(a);
            }
        }
    }

    private string PoolText(MinerState s) =>
        s.Info is { } i && !AppHost.IsSimulated(s.Config.Host) ? _host.PoolWatch.StatusText(s.Config.Host, i, _config.PoolWatch) : "";

    // ---------- Tagesbericht ----------

    private bool _reportBusy;

    private void CheckDailyReport(DateTime now)
    {
        if (_reportBusy || _history is null || !DailyReport.IsDue(_config.DailyReport, now)) return;
        _ = SendDailyReportAsync(now, markSent: true);
    }

    /// <summary>Bericht der letzten 24 h senden (auch für "Jetzt senden" in den Einstellungen).</summary>
    public async Task<string?> SendDailyReportAsync(DateTime now, bool markSent)
    {
        if (_history is null) return "Verlaufsdatenbank nicht verfügbar.";
        if (!_notify.Enabled) return "Kein Push-Dienst eingerichtet (Einstellungen → Push-Benachrichtigungen).";
        _reportBusy = true;
        try
        {
            var miners = _states.Where(x => !AppHost.IsSimulated(x.Config.Host))
                                .Select(x => (x.Config.Name, x.Config.Host)).ToList();
            var (title, text) = DailyReport.Build(_history, _config, miners, now);
            // Eigener Schlüssel je Tag; "Jetzt senden" umgeht die Sperre über einen eindeutigen Schlüssel
            var key = markSent ? $"report:{now:yyyy-MM-dd}" : $"report-test:{now:O}";
            await _notify.SendAsync(key, title, text, NotifyPriority.Low, TimeSpan.FromHours(20));
            if (_notify.LastError is { } error) return "Senden fehlgeschlagen: " + error;
            if (markSent)
            {
                _config.DailyReport.LastSent = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                _config.Save();
            }
            return null;
        }
        finally
        {
            _reportBusy = false;
        }
    }

    // ---------- Best-Diff-Rekorde ----------

    private void CheckBestDiffs()
    {
        if (_history is null) return;

        var changed = false;
        foreach (var s in _states.Where(s => s.Online && s.Info is not null))
        {
            var i = s.Info!;
            var best = Math.Max(Difficulty.Parse(i.bestDiff), Difficulty.Parse(i.bestSessionDiff));
            if (best <= 0) continue;

            var coin = ResolveCoin(s)?.Symbol() ?? "?";
            var raw = Difficulty.Format(best);

            BestDiffRecord? previous;
            try { previous = _history.UpdateBestDiff(s.Config.Host, coin, best, raw); }
            catch { continue; }

            if (previous is null) continue;
            changed = true;

            // Ersteintrag (Value 0) nicht melden, nur echte Verbesserungen
            if (previous.Value > 0 && _config.Notifications.OnRecord)
                _ = _notify.SendAsync($"record:{s.Config.Host}:{coin}", $"Neuer Rekord – {s.Config.Name}",
                    $"Best Diff {raw} ({coin}), bisher {previous.Raw}", NotifyPriority.Low, TimeSpan.FromMinutes(10));
        }

        if (changed)
        {
            try { _bestDiffs = _history.GetBestDiffs(); } catch { /* alter Stand bleibt */ }
        }
    }

    private BestDiffRecord? RecordFor(string host)
        => _bestDiffs.Where(r => r.Host == host).OrderByDescending(r => r.Value).FirstOrDefault();

    private string RecordText(string host)
        => RecordFor(host) is { } r ? $"Rekord {r.Raw} ({r.Coin}, {r.AchievedAt.ToString("dd.MM.yy", De)})" : "";

    /// <summary>Höchster Rekord über alle aktuell eingetragenen Miner.</summary>
    private (string Value, string Sub) AggregateRecord()
    {
        var hosts = _states.Select(s => s.Config.Host).ToHashSet();
        var top = _bestDiffs.Where(r => hosts.Contains(r.Host)).OrderByDescending(r => r.Value).FirstOrDefault();
        if (top is null) return ("–", "");

        var name = _states.FirstOrDefault(s => s.Config.Host == top.Host)?.Config.Name ?? top.Host;
        return (top.Raw, $"Rekord: {name}, {top.Coin}, {top.AchievedAt.ToString("dd.MM.yy", De)}");
    }

    // ---------- Coin je Miner ----------

    /// <summary>Coin aus den Einstellungen, sonst aus der Wallet-Adresse; null wenn unbekannt.</summary>
    private static CoinType? ResolveCoin(MinerState s)
    {
        switch (s.Config.Coin)
        {
            case "BTC": return CoinType.Bitcoin;
            case "BCH": return CoinType.BitcoinCash;
        }

        var address = s.WalletAddress;
        return string.IsNullOrWhiteSpace(address) ? null : CoinTypeExtensions.GuessFromAddress(address);
    }

    // ---------- Watchdog ----------

    private void RunWatchdog()
    {
        foreach (var s in _states)
        {
            if (AppHost.IsSimulated(s.Config.Host)) continue;
            var online = s.Online && s.Info is not null;
            var hash = online ? s.Info!.hashRate : 0;
            var inMaintenance = _host.Maintenance.IsActive(s.Config.Host);
            if (_watchdog.ShouldReboot(s.Config.Host, online, hash, _config.Watchdog, inMaintenance))
                _ = RebootByWatchdogAsync(s);
        }
    }

    private async Task RebootByWatchdogAsync(MinerState s)
    {
        string result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Über die gemeinsame Verbindung – öffnet auch das Wartungsfenster (keine Offline-Meldung für diesen Neustart)
            var connection = _host.Polling.Connection(s.Config.Host)
                             ?? throw new InvalidOperationException("Gerät nicht mehr in der Liste");
            await connection.RestartAsync(cts.Token);
            result = "Neustart ausgelöst";
        }
        catch (Exception ex)
        {
            result = "Neustart fehlgeschlagen: " + ex.Message;
        }

        SetStatus(!result.Contains("fehlgeschlagen"), $"Watchdog {s.Config.Name}: {result}");

        if (_config.Notifications.OnMaintenance)
            await _notify.SendAsync($"watchdog:{s.Config.Host}", $"Watchdog: {s.Config.Name}",
                $"{_config.Watchdog.ZeroHashMinutes} min ohne Hashrate. {result}.",
                NotifyPriority.High, TimeSpan.FromMinutes(5));
    }

    // ---------- Firmware ----------

    private async Task RefreshFirmwareAsync()
    {
        if ((DateTime.UtcNow - _lastFirmwareRefresh).TotalMinutes < 30) return;
        _lastFirmwareRefresh = DateTime.UtcNow;

        await _firmware.RefreshAsync(_states.Select(s => s.Config.FirmwareRepo));

        if (!_config.Notifications.OnMaintenance || !_notify.Enabled) return;

        foreach (var s in _states.Where(s => !string.IsNullOrWhiteSpace(s.Info?.version)))
        {
            var fw = _firmware.Evaluate(s.Config.FirmwareRepo, s.Info!.version);
            if (fw.Status != FirmwareStatus.UpdateAvailable || fw.Latest is null) continue;

            var repo = s.Config.FirmwareRepo.Trim();
            if (_config.NotifiedFirmware.TryGetValue(repo, out var done) && done == fw.Latest) continue;

            _config.NotifiedFirmware[repo] = fw.Latest;
            _config.Save();

            await _notify.SendAsync($"fw:{repo}:{fw.Latest}", "Firmware-Update verfügbar",
                $"{repo}: {fw.Latest} (installiert bei {s.Config.Name}: {s.Info.version})",
                NotifyPriority.Low, TimeSpan.FromDays(30));
        }
    }

    private string FirmwareText(MinerState s)
    {
        var installed = s.Info?.version;
        var fw = _firmware.Evaluate(s.Config.FirmwareRepo, installed);
        return fw.Status switch
        {
            FirmwareStatus.UpToDate => $"Firmware {installed} (aktuell)",
            FirmwareStatus.UpdateAvailable => $"Firmware {installed} → Update {fw.Latest} verfügbar",
            _ => $"Firmware {installed ?? "?"}"
        };
    }

    // ---------- Solo-Chancen ----------

    private async Task RefreshSoloOddsAsync()
    {
        await _odds.RefreshAsync();
        RenderSoloOdds();
    }

    private void RenderSoloOdds()
    {
        foreach (var coin in Enum.GetValues<CoinType>())
        {
            var (value, sub, sub2) = coin == CoinType.Bitcoin
                ? (OddsBtcValue, OddsBtcSub, OddsBtcSub2)
                : (OddsBchValue, OddsBchSub, OddsBchSub2);

            var miners = _states.Where(s => s.Online && s.Info is not null && ResolveCoin(s) == coin).ToList();
            var gh = miners.Sum(s => s.Info!.hashRate);
            var r = _odds.Calculate(coin, gh, miners.Count);

            if (r.Difficulty is null)
            {
                value.Text = "–";
                sub.Text = "Netzwerk-Difficulty wird geladen …";
                sub2.Text = "";
                continue;
            }

            if (r.ChancePerYear is null)
            {
                value.Text = "–";
                sub.Text = $"kein Miner auf {coin.Symbol()} · Difficulty {Difficulty.Format(r.Difficulty.Value)}";
                sub2.Text = "Coin je Miner: Einstellungen oder Wallet-Adresse";
                continue;
            }

            value.Text = $"{Percent(r.ChancePerYear.Value)} pro Jahr";
            sub.Text = $"{Percent(r.ChancePerDay!.Value)} pro Tag · {FormatHash(gh)} auf {miners.Count} Miner";
            sub2.Text = $"im Mittel ein Block alle {DurationText(r.ExpectedDays!.Value)} · Difficulty {Difficulty.Format(r.Difficulty.Value)}";
        }
    }

    private static string Percent(double p)
        => p >= 0.01 ? (p * 100).ToString("0.00", De) + " %"
         : p >= 0.00001 ? (p * 100).ToString("0.0000", De) + " %"
         : "1 : " + (1 / p).ToString("N0", De);

    private static string DurationText(double days)
        => days < 365 ? $"{days.ToString("N0", De)} Tage" : $"{(days / 365).ToString("N0", De)} Jahre";

    // ---------- Tray ----------

    private void UpdateTray()
    {
        if (_tray is null) return;

        var total = _states.Count;
        var online = _states.Count(s => s.Online);
        var hot = _states.Any(s => s.Online && s.Info!.temp >= _config.TempWarn);
        var gh = _states.Where(s => s.Online).Sum(s => s.Info!.hashRate);

        var state = total == 0 ? TrayState.Unknown
                  : online == 0 ? TrayState.Error
                  : online < total || hot ? TrayState.Warning
                  : TrayState.Ok;

        _tray.Update(state, $"Miner {online}/{total} · {FormatHash(gh)}");
    }

    private void HideToTray()
    {
        OwnerWindow.Hide();
        if (_trayHintShown) return;
        _tray?.Balloon("BitaxeTuner", "Läuft im Infobereich weiter. Doppelklick öffnet das Fenster.");
        _trayHintShown = true;
    }

    private void RestoreFromTray()
    {
        var window = OwnerWindow;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
