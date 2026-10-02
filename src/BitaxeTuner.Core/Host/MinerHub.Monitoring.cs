using BitaxeTuner.Core.Config;
using System.Globalization;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Eingang auf einer Miner-Wallet, die das Steuer-Modul nicht selbst überwacht.</summary>
public sealed record PayoutNotice(string Address, string? MinerName, long AmountSat, bool Confirmed, string TxId);

/// <summary>
/// Überwachung (vormals MonitorView.Features in der Oberfläche): dauerhafter Verlauf, Gesundheitsprüfung mit
/// Push, Pool/Shares, Tagesbericht, Best-Diff-Rekorde, Watchdog, Firmware-Check, Wallet-Eingänge.
/// </summary>
public sealed partial class MinerHub
{
    private static CultureInfo De => L.Culture; // Sprache kann sich zur Laufzeit ändern (Server-Einstellung)

    private readonly Watchdog _watchdog = new();
    private readonly Dictionary<string, DateTime> _lastHistoryWrite = new();
    private DateTime _lastAggWrite = DateTime.MinValue;
    /// <summary>Für das Protokoll: als offline eingetragen (unabhängig von Push-Einstellungen).</summary>
    private readonly HashSet<string> _offlineLogged = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPrune = DateTime.MinValue;
    private DateTime _lastFirmwareRefresh = DateTime.MinValue;

    private readonly Dictionary<string, int> _failCount = new();
    private readonly HashSet<string> _offlineNotified = new();
    private readonly Dictionary<string, int> _blockFoundSeen = new();

    private readonly List<Sample> _aggHistory = new();
    private List<BestDiffRecord> _bestDiffs = new();
    private readonly Dictionary<string, WalletInfo> _wallets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _walletErrors = new(StringComparer.OrdinalIgnoreCase);
    private bool _pollBusy, _walletBusy, _reportBusy, _monitoringInitialized;

    private IReadOnlyList<MinerState> States => Polling.States;

    /// <summary>Summe aller Miner im Live-Zeitraum (Gesamt-Diagramm).</summary>
    public IReadOnlyList<Sample> AggregateHistory => _aggHistory;

    public IReadOnlyList<BestDiffRecord> BestDiffs => _bestDiffs;
    public IReadOnlyDictionary<string, WalletInfo> Wallets => _wallets;
    public IReadOnlyDictionary<string, string> WalletErrors => _walletErrors;
    public string WalletStatusText { get; private set; } = "";

    /// <summary>Eingang auf einer Wallet (Desktop: Hinweisfenster, Server: Push/Browser).</summary>
    public event Action<PayoutNotice>? PayoutDetected;

    /// <summary>Wallet-Stände neu abgefragt.</summary>
    public event Action? WalletsUpdated;

    private void InitMonitoring()
    {
        if (_monitoringInitialized) return;
        _monitoringInitialized = true;

        if (History is not null)
        {
            try
            {
                History.Prune(Config.HistoryDays);
                _bestDiffs = History.GetBestDiffs();
                _lastPrune = DateTime.Now;
            }
            catch (Exception ex)
            {
                RaiseStatus(false, L.T("Verlaufsdatenbank nicht verfügbar: ") + ex.Message);
            }
        }
        else
        {
            RaiseStatus(false, L.T("Verlaufsdatenbank nicht verfügbar: ") + HistoryError);
        }

        // Dokumentierter Zufluss aus dem Steuer-Modul → Push (läuft auf einem Pool-Thread)
        TaxMonitor.NewRewardDetected += reward =>
        {
            if (!Config.Notifications.Wants(NotifyCategory.Finds)) return;
            if (DateTime.UtcNow - reward.ReceivedAtUtc > TimeSpan.FromHours(6)) return; // Erstimport alter Eingänge

            var eur = reward.EurValue is { } v ? $" ≈ {v.ToString("N2", De)} €" : "";
            _ = Notify.SendAsync($"reward:{reward.Coin.Symbol()}:{reward.TxId}", L.T("Zufluss dokumentiert"),
                L.T("{0} {1} auf {2}{3}", reward.Amount.ToString("0.00000000", De), reward.Coin.Symbol(), reward.WalletLabel, eur),
                NotifyPriority.High, TimeSpan.FromDays(365), category: NotifyCategory.Finds);
        };
    }

    /// <summary>Alle Miner einmal abfragen und danach alle Prüfungen ausführen. Liefert false, wenn noch eine Runde läuft.</summary>
    public async Task<bool> PollNowAsync()
    {
        if (_pollBusy) return false;
        _pollBusy = true;
        try
        {
            // Zentraler Abruf aller Miner (eine Verbindung je Miner, geteilt mit dem Tuning)
            if (!await Polling.PollOnceAsync()) return false;

            var now = Options.Clock?.Invoke() ?? DateTime.Now;
            var online = States.Where(s => s.Online).Select(s => s.Info!).ToList();
            if (online.Count > 0)
                _aggHistory.Add(new Sample(now, online.Sum(i => i.hashRate), online.Max(i => i.temp), online.Sum(i => i.power)));

            var cutoff = now.AddMinutes(-Math.Max(1, Config.HistoryMinutes));
            _aggHistory.RemoveAll(x => x.Time < cutoff);
            foreach (var s in States) s.History.RemoveAll(x => x.Time < cutoff);

            var ok = States.Count(s => s.Online);
            RaiseStatus(ok > 0, ok == States.Count
                ? L.T("alle {0} Miner online – {1}", ok, now.ToString("HH:mm:ss", De))
                : L.T("{0}/{1} online – {2}", ok, States.Count, now.ToString("HH:mm:ss", De)));

            AfterPoll(now);
            Polled?.Invoke();
            return true;
        }
        finally
        {
            _pollBusy = false;
        }
    }

    /// <summary>Live-Verläufe im Speicher verwerfen ("Leeren" in der Überwachung).</summary>
    public void ClearLiveHistory()
    {
        foreach (var s in States) s.History.Clear();
        _aggHistory.Clear();
    }

    private void AfterPoll(DateTime now)
    {
        foreach (var device in Devices)
        {
            if (device.State.Online && device.State.Normalized is { } info && !device.ProfileResolved)
                _ = ResolveProfileAsync(device, info);
        }

        RecordHistory(now);
        CheckHealth();
        CheckPools(now);
        CheckDailyReport(now);
        RecordHealth(now);
        CheckHealthWarnings(now);
        CheckBackup(now);
        CheckBestDiffs();
        RunWatchdog();
        _ = RefreshFirmwareAsync();
        TickAutomation(now);

        if ((now - _lastPrune).TotalHours >= 24)
        {
            try { History?.Prune(Config.HistoryDays); } catch { /* nicht kritisch */ }
            _lastPrune = now;
        }
    }

    // ---------- Verlauf ----------

    /// <summary>Höchstens ein Datenpunkt je Minute und Miner, plus Summe unter "*".</summary>
    private void RecordHistory(DateTime now)
    {
        if (History is null) return;

        try
        {
            foreach (var s in States)
            {
                var host = s.Config.Host;
                if (IsSimulated(host)) continue;
                if (_lastHistoryWrite.TryGetValue(host, out var last) && (now - last).TotalSeconds < 60) continue;
                _lastHistoryWrite[host] = now;

                if (s.Online && s.Info is { } i) History.AddSample(host, now, i.hashRate, i.temp, i.power, true);
                else History.AddSample(host, now, 0, 0, 0, false);
            }

            if ((now - _lastAggWrite).TotalSeconds >= 60 && States.Count > 0)
            {
                _lastAggWrite = now;
                var online = States.Where(s => s.Online && s.Info is not null && !IsSimulated(s.Config.Host))
                                   .Select(s => s.Info!).ToList();
                History.AddSample(HistoryStore.AggregateHost, now,
                    online.Sum(i => i.hashRate),
                    online.Count > 0 ? online.Max(i => i.temp) : 0,
                    online.Sum(i => i.power),
                    online.Count > 0);
            }
        }
        catch (Exception ex)
        {
            RaiseStatus(false, L.T("Verlauf konnte nicht gespeichert werden: ") + ex.Message);
        }
    }

    // ---------- Gesundheit und Benachrichtigungen ----------

    private void CheckHealth()
    {
        var n = Config.Notifications;

        foreach (var s in States)
        {
            var host = s.Config.Host;
            if (IsSimulated(host)) continue;

            // Absichtlicher Eingriff (Tuning, Benchmark, gewollter Neustart): keine Offline-Zählung, keine Meldung.
            // Ist der Miner nach dem Wartungsfenster noch weg, greift wieder die normale 3-Fehlversuche-Regel.
            if (!s.Online && Maintenance.IsActive(host))
            {
                _failCount[host] = 0;
                continue;
            }

            if (!s.Online)
            {
                var fails = _failCount.GetValueOrDefault(host) + 1;
                _failCount[host] = fails;
                if (fails == 3 && _offlineLogged.Add(host))
                    LogEvent(host, EventCategories.Connection, L.T("offline: {0}", s.Error ?? L.T("keine Verbindung")));

                // Erst nach drei Fehlversuchen melden, einzelne Aussetzer im WLAN sind normal
                if (fails >= 3 && n.Wants(NotifyCategory.Offline))
                {
                    _offlineNotified.Add(host);
                    _ = Notify.SendAsync($"offline:{host}", L.T("{0} offline", s.Config.Name),
                        L.T("{0} antwortet nicht ({1}).", host, s.Error ?? L.T("keine Verbindung")),
                        NotifyPriority.High, TimeSpan.FromHours(6), NotifyCategory.Offline, host);
                }
                continue;
            }

            _failCount[host] = 0;
            if (_offlineLogged.Remove(host)) LogEvent(host, EventCategories.Connection, L.T("wieder online"));
            if (_offlineNotified.Remove(host))
            {
                Notify.Reset($"offline:{host}");
                _ = Notify.SendAsync($"online:{host}", L.T("{0} wieder online", s.Config.Name),
                    L.T("{0} antwortet wieder.", host), NotifyPriority.Normal, TimeSpan.FromMinutes(1), NotifyCategory.Offline, host);
            }

            var i = s.Info!;
            if (n.Wants(NotifyCategory.Overheat) && (i.temp >= Config.TempWarn || i.overheat_mode != 0))
            {
                var text = L.T("ASIC {0} °C, VR {1} °C", i.temp.ToString("0.0", De), i.vrTemp.ToString("0", De)) +
                           (i.overheat_mode != 0 ? L.T(", Overheat-Modus aktiv") : "");
                _ = Notify.SendAsync($"hot:{host}", L.T("{0} zu heiß", s.Config.Name), text,
                    NotifyPriority.Urgent, TimeSpan.FromMinutes(30), NotifyCategory.Overheat, host);
            }

            // Blockfund laut Miner: Zähler gestiegen seit der letzten Abfrage
            if (_blockFoundSeen.TryGetValue(host, out var seen) && i.blockFound > seen)
                OnBlockFound(s.Config.Name.Length > 0 ? s.Config.Name : host, i.blockFound, Options.Clock?.Invoke() ?? DateTime.Now);
            if (_blockFoundSeen.TryGetValue(host, out seen) && i.blockFound > seen && n.Wants(NotifyCategory.Finds))
            {
                _ = Notify.SendAsync($"block:{host}:{i.blockFound}", L.T("BLOCK GEFUNDEN – {0}", s.Config.Name),
                    L.T("Der Miner meldet jetzt {0} gefundene(n) Block/Blöcke.", i.blockFound),
                    NotifyPriority.Urgent, TimeSpan.FromDays(365), category: NotifyCategory.Finds, host: host);
            }
            _blockFoundSeen[host] = i.blockFound;
        }
    }

    // ---------- Pool und Shares ----------

    /// <summary>Fallback-Pool, Ablehnungsquote und Antwortzeit je Miner (nicht während Tuning/Neustart).</summary>
    private void CheckPools(DateTime now)
    {
        var settings = Config.PoolWatch;
        foreach (var s in States)
        {
            if (!s.Online || s.Info is null || IsSimulated(s.Config.Host)) continue;
            var alerts = PoolWatch.Evaluate(s.Config.Host, s.Config.Name, s.Info, now, settings, Maintenance.IsActive(s.Config.Host));
            foreach (var a in alerts)
            {
                RaiseStatus(false, $"{a.Title}: {a.Message}");
                if (Config.Notifications.Wants(NotifyCategory.Pool)) SendAlert(a);
            }
        }
    }

    public string PoolText(MinerState s) =>
        s.Info is { } i && !IsSimulated(s.Config.Host) ? PoolWatch.StatusText(s.Config.Host, i, Config.PoolWatch) : "";

    // ---------- Tagesbericht ----------

    private void CheckDailyReport(DateTime now)
    {
        CheckMonthlyReport(now);
        if (_reportBusy || History is null || !DailyReport.IsDue(Config.DailyReport, now)) return;
        _ = SendDailyReportAsync(now, markSent: true);
    }

    /// <summary>Bericht der letzten 24 h senden (auch für "Jetzt senden" in den Einstellungen). Liefert eine Fehlermeldung oder null.</summary>
    public async Task<string?> SendDailyReportAsync(DateTime now, bool markSent)
    {
        if (History is null) return L.T("Verlaufsdatenbank nicht verfügbar.");
        if (!Notify.Enabled) return L.T("Kein Push-Dienst eingerichtet (Einstellungen → Push-Benachrichtigungen).");
        _reportBusy = true;
        try
        {
            var miners = States.Where(x => !IsSimulated(x.Config.Host))
                               .Select(x => (x.Config.Name, x.Config.Host)).ToList();
            List<(string Host, string Line)> tips = [];
            try { tips = AdvisorReportLines(now).ToList(); }
            catch { /* Ratgeber optional */ }
            var title = DailyReport.Build(History, Config, [], now).Title;
            // Je Push-Ziel eigener Text: nur dessen Miner (falls ausgewählt) und nur die gewünschten Teile – gleiche Texte einmal bauen
            var cache = new Dictionary<string, string>();
            string? TextFor(PushTarget t)
            {
                var own = miners.Where(m => t.CoversHost(m.Host, GroupsOfHost(m.Host))).ToList();
                if (own.Count == 0) return null;
                var cacheKey = string.Join(",", own.Select(m => m.Host)) + "|" + string.Join(",", ReportParts.All.Where(t.ReportIncludes));
                if (cache.TryGetValue(cacheKey, out var cached)) return cached;
                var text = DailyReport.Build(History!, Config, own, now, t.ReportIncludes).Message;
                var ownTips = t.ReportIncludes(ReportParts.Tips) ? tips.Where(x => own.Any(m => m.Host == x.Host)).Select(x => x.Line).ToList() : [];
                if (ownTips.Count > 0) text += "\n\n" + string.Join("\n", ownTips) + L.T("\n(Anwenden nur nach Bestätigung: Vergleich → Empfehlungen)");
                return cache[cacheKey] = text;
            }
            // Eigener Schlüssel je Tag; "Jetzt senden" umgeht die Sperre über einen eindeutigen Schlüssel
            var key = markSent ? $"report:{now:yyyy-MM-dd}" : $"report-test:{now:O}";
            await Notify.SendAsync(key, title, TextFor, NotifyPriority.Low, TimeSpan.FromHours(20), NotifyCategory.DailyReport);
            if (Notify.LastError is { } error) return L.T("Senden fehlgeschlagen: ") + error;
            if (markSent)
            {
                Config.DailyReport.LastSent = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Config.Save();
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
        if (History is null) return;

        var changed = false;
        foreach (var s in States.Where(s => s.Online && s.Info is not null))
        {
            var i = s.Info!;
            var best = Math.Max(Difficulty.Parse(i.bestDiff), Difficulty.Parse(i.bestSessionDiff));
            if (best <= 0) continue;

            var coin = ResolveCoin(s)?.Symbol() ?? "?";
            var raw = Difficulty.Format(best);

            BestDiffRecord? previous;
            try { previous = History.UpdateBestDiff(s.Config.Host, coin, best, raw); }
            catch { continue; }

            if (previous is null) continue;
            changed = true;

            // Ersteintrag (Value 0) nicht melden, nur echte Verbesserungen
            if (previous.Value > 0)
                OnBestDiffRecord(s.Config.Name.Length > 0 ? s.Config.Name : s.Config.Host, coin, previous.Raw, raw, Options.Clock?.Invoke() ?? DateTime.Now);
            if (previous.Value > 0 && Config.Notifications.Wants(NotifyCategory.Record))
                _ = Notify.SendAsync($"record:{s.Config.Host}:{coin}", L.T("Neuer Rekord – {0}", s.Config.Name),
                    L.T("Best Diff {0} ({1}), bisher {2}", raw, coin, previous.Raw), NotifyPriority.Low, TimeSpan.FromMinutes(10), category: NotifyCategory.Record, host: s.Config.Host);
        }

        if (changed)
        {
            try { _bestDiffs = History.GetBestDiffs(); } catch { /* alter Stand bleibt */ }
        }
    }

    /// <summary>Coin aus den Einstellungen, sonst aus der Wallet-Adresse; null wenn unbekannt.</summary>
    public static CoinType? ResolveCoin(MinerState s)
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
        foreach (var s in States)
        {
            if (IsSimulated(s.Config.Host)) continue;
            var online = s.Online && s.Info is not null;
            var hash = online ? s.Info!.hashRate : 0;
            var inMaintenance = Maintenance.IsActive(s.Config.Host);
            if (_watchdog.ShouldReboot(s.Config.Host, online, hash, Config.Watchdog, inMaintenance))
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
            var connection = Polling.Connection(s.Config.Host)
                             ?? throw new InvalidOperationException(L.T("Gerät nicht mehr in der Liste"));
            await connection.RestartAsync(cts.Token);
            result = L.T("Neustart ausgelöst");
        }
        catch (Exception ex)
        {
            result = L.T("Neustart fehlgeschlagen: ") + ex.Message;
        }

        RaiseStatus(!result.Contains("fehlgeschlagen"), L.T("Watchdog {0}: {1}", s.Config.Name, result));

        if (Config.Notifications.Wants(NotifyCategory.Maintenance))
            await Notify.SendAsync($"watchdog:{s.Config.Host}", L.T("Watchdog: {0}", s.Config.Name),
                L.T("{0} min ohne Hashrate. {1}.", Config.Watchdog.ZeroHashMinutes, result),
                NotifyPriority.High, TimeSpan.FromMinutes(5), category: NotifyCategory.Maintenance, host: s.Config.Host);
    }

    // ---------- Firmware ----------

    private async Task RefreshFirmwareAsync()
    {
        if (!Options.OnlineChecks || (DateTime.UtcNow - _lastFirmwareRefresh).TotalMinutes < 30) return;
        _lastFirmwareRefresh = DateTime.UtcNow;

        await Firmware.RefreshAsync(States.Select(s => s.Config.FirmwareRepo));

        if (!Config.Notifications.Wants(NotifyCategory.Maintenance) || !Notify.Enabled) return;

        foreach (var s in States.Where(s => !string.IsNullOrWhiteSpace(s.Info?.version)))
        {
            var fw = Firmware.Evaluate(s.Config.FirmwareRepo, s.Info!.version);
            if (fw.Status != FirmwareStatus.UpdateAvailable || fw.Latest is null) continue;

            var repo = s.Config.FirmwareRepo.Trim();
            if (Config.NotifiedFirmware.TryGetValue(repo, out var done) && done == fw.Latest) continue;

            Config.NotifiedFirmware[repo] = fw.Latest;
            Config.Save();

            await Notify.SendAsync($"fw:{repo}:{fw.Latest}", L.T("Firmware-Update verfügbar"),
                L.T("{0}: {1} (installiert bei {2}: {3})", repo, fw.Latest, s.Config.Name, s.Info.version),
                NotifyPriority.Low, TimeSpan.FromDays(30), category: NotifyCategory.Maintenance);
        }
    }

    public string FirmwareText(MinerState s)
    {
        var installed = s.Info?.version;
        var fw = Firmware.Evaluate(s.Config.FirmwareRepo, installed);
        return fw.Status switch
        {
            FirmwareStatus.UpToDate => L.T("Firmware {0} (aktuell)", installed),
            FirmwareStatus.UpdateAvailable => L.T("Firmware {0} → Update {1} verfügbar", installed, fw.Latest),
            _ => L.T("Firmware {0}", installed ?? "?")
        };
    }

    // ---------- Wallets ----------

    /// <summary>Kontostände aller bekannten Miner-Wallets abfragen und neue Eingänge melden.</summary>
    public async Task PollWalletsAsync()
    {
        if (_walletBusy || !Options.OnlineChecks) return;

        var addresses = States
            .Select(s => s.WalletAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0)
        {
            WalletStatusText = L.T("keine Adresse bekannt");
            WalletsUpdated?.Invoke();
            return;
        }

        _walletBusy = true;
        try
        {
            foreach (var address in addresses)
            {
                try
                {
                    var w = await WalletClient.GetAsync(address, CancellationToken.None);
                    _wallets[address] = w;
                    _walletErrors.Remove(address);
                    CheckPayout(w);
                }
                catch (Exception ex)
                {
                    _walletErrors[address] = MinerPollingService.Shorten(ex);
                }
            }

            WalletStatusText = L.T("geprüft {0} · {1} Adresse(n)", DateTime.Now.ToString("HH:mm", De), addresses.Count);
            WalletsUpdated?.Invoke();
        }
        finally
        {
            _walletBusy = false;
        }
    }

    private void CheckPayout(WalletInfo w)
    {
        var txid = w.LastIncomingTxid;
        if (string.IsNullOrEmpty(txid)) return;

        var known = Config.LastSeenPayoutTxids.TryGetValue(w.Address, out var last) ? last : null;
        if (txid == known) return;

        var first = string.IsNullOrEmpty(known);
        Config.LastSeenPayoutTxids[w.Address] = txid;
        Config.Save();
        if (first) return;

        // Überwacht das Steuer-Modul die Adresse, meldet es den Zufluss selbst (mit EUR-Wert).
        if (TaxMonitor.Contains(w.Address)) return;

        var owner = States.FirstOrDefault(s => string.Equals(s.WalletAddress, w.Address, StringComparison.OrdinalIgnoreCase));
        PayoutDetected?.Invoke(new PayoutNotice(w.Address, owner?.Config.Name, w.LastIncomingSat ?? 0, w.LastIncomingConfirmed, txid));
    }
}
