using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>
/// Anzeige-Erweiterungen der Überwachung: Diagramme aus dem Verlauf, Verfügbarkeit, Rekorde,
/// Solo-Chancen und Tray-Symbol. Verlauf schreiben, Push, Watchdog, Firmware-/Pool-Prüfung und
/// Tagesbericht laufen im <see cref="Core.Host.MinerHub"/> (auch im Server-Dienst).
/// </summary>
public partial class MonitorView
{
    // Verlauf und Solo-Chancen sind App-weit einmalig (Hub)
    private HistoryStore? _history => _host.History;
    private SoloOddsService _odds => _host.Odds;
    private TrayService? _tray;

    private IReadOnlyList<BestDiffRecord> _bestDiffs => _host.Hub.BestDiffs;
    private readonly Dictionary<string, (DateTime At, double? Value)> _availabilityCache = new();
    private readonly Dictionary<string, (DateTime At, List<Sample> Data)> _chartCache = new();
    private string _chartRange = "1h";
    private bool _trayHintShown;

    // ---------- Lebenszyklus ----------

    private void InitFeatures()
    {
        _tray = new TrayService();
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += () => OwnerWindow.Close();
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
            ChartRangeText.Text = _history is null ? L.T("nur live (Datenbank nicht verfügbar)") : "live";
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
                ChartRangeText.Text = L.T("Datenbankfehler: ") + ex.Message;
                return live;
            }
            _chartCache[key] = cached;
        }

        ChartRangeText.Text = cached.Data.Count == 0
            ? L.T("noch keine Daten in diesem Zeitraum")
            : L.T("{0} Punkte aus der Datenbank", cached.Data.Count);
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
        => Availability(host) is { } a ? L.T("verfügbar 7 T: {0} %", (a * 100).ToString("0.0", De)) : "";

    /// <summary>Mittlere Verfügbarkeit aller Miner.</summary>
    private string AggregateAvailabilityText()
    {
        var values = _states.Select(s => Availability(s.Config.Host)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? "" : L.T("Ø verfügbar 7 T: {0} %", (values.Average() * 100).ToString("0.0", De));
    }

    /// <summary>Beschriftung der Zeitachse: Minuten, Stunden oder Tage.</summary>
    private static string SpanText(TimeSpan span)
        => span.TotalHours < 2 ? L.T("{0} min", span.TotalMinutes.ToString("0", De))
         : span.TotalDays < 2 ? $"{span.TotalHours.ToString("0", De)} h"
         : L.T("{0} Tage", span.TotalDays.ToString("0", De));

    private string PoolText(MinerState s) => _host.Hub.PoolText(s);

    private BestDiffRecord? RecordFor(string host)
        => _bestDiffs.Where(r => r.Host == host).OrderByDescending(r => r.Value).FirstOrDefault();

    private string RecordText(string host)
        => RecordFor(host) is { } r ? L.T("Rekord {0} ({1}, {2})", r.Raw, r.Coin, r.AchievedAt.ToString("d", De)) : "";

    /// <summary>Höchster Rekord über alle aktuell eingetragenen Miner.</summary>
    private (string Value, string Sub) AggregateRecord()
    {
        var hosts = _states.Select(s => s.Config.Host).ToHashSet();
        var top = _bestDiffs.Where(r => hosts.Contains(r.Host)).OrderByDescending(r => r.Value).FirstOrDefault();
        if (top is null) return ("–", "");

        var name = _states.FirstOrDefault(s => s.Config.Host == top.Host)?.Config.Name ?? top.Host;
        return (top.Raw, L.T("Rekord: {0}, {1}, {2}", name, top.Coin, top.AchievedAt.ToString("d", De)));
    }

    private string FirmwareText(MinerState s) => _host.Hub.FirmwareText(s);

    // Coin je Miner: aus den Einstellungen, sonst aus der Wallet-Adresse
    private static CoinType? ResolveCoin(MinerState s) => Core.Host.MinerHub.ResolveCoin(s);

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
                sub.Text = L.T("Netzwerk-Difficulty wird geladen …");
                sub2.Text = "";
                continue;
            }

            if (r.ChancePerYear is null)
            {
                value.Text = "–";
                sub.Text = L.T("kein Miner auf {0} · Difficulty {1}", coin.Symbol(), Difficulty.Format(r.Difficulty.Value));
                sub2.Text = L.T("Coin je Miner: Einstellungen oder Wallet-Adresse");
                continue;
            }

            value.Text = L.T("{0} pro Jahr", Percent(r.ChancePerYear.Value));
            sub.Text = L.T("{0} pro Tag · {1} auf {2} Miner", Percent(r.ChancePerDay!.Value), FormatHash(gh), miners.Count);
            sub2.Text = L.T("im Mittel ein Block alle {0} · Difficulty {1}", DurationText(r.ExpectedDays!.Value), Difficulty.Format(r.Difficulty.Value));
        }
    }

    private static string Percent(double p)
        => p >= 0.01 ? (p * 100).ToString("0.00", De) + " %"
         : p >= 0.00001 ? (p * 100).ToString("0.0000", De) + " %"
         : "1 : " + (1 / p).ToString("N0", De);

    private static string DurationText(double days)
        => days < 365 ? L.T("{0} Tage", days.ToString("N0", De)) : L.T("{0} Jahre", (days / 365).ToString("N0", De));

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

        _tray.Update(state, L.T("Miner {0}/{1} · {2}", online, total, FormatHash(gh)));
    }

    private void HideToTray()
    {
        OwnerWindow.Hide();
        if (_trayHintShown) return;
        _tray?.Balloon("BitaxeTuner", L.T("Läuft im Infobereich weiter. Doppelklick öffnet das Fenster."));
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
