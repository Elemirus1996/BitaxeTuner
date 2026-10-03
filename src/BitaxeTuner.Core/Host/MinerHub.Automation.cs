using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// Nach jeder Abfragerunde: Temperaturschutz, Zeitplan/Strompreis, Dauertests auswerten, freigegebene
/// Änderungen ausführen, melden und protokollieren; Stand für die Handy-Ansicht bauen
/// (vormals AutomationCoordinator in der Desktop-App).
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Dauertest beendet (bestanden, fehlgeschlagen oder abgebrochen). Bei Fehlschlag evtl. mit Vorschlag.</summary>
    public event Action<HubDevice, SoakResult, SoakSuggestion?>? SoakFinished;

    /// <summary>Preise abrufen und für die Kostenrechnung vergangener Stunden in history.db sichern.</summary>
    private async Task RefreshPricesAsync(DateTime now)
    {
        await Prices.RefreshAsync(now.ToUniversalTime());
        if (Prices.Prices.Count > 0)
            try { History?.AddPrices(Prices.Prices); } catch { /* nicht kritisch */ }
    }

    private void TickAutomation(DateTime now)
    {
        // Preise nur abrufen, wenn eine Quelle eingestellt ist (höchstens alle 30 min)
        if (Config.PriceSource.Source != "none") _ = RefreshPricesAsync(now);

        var list = Devices;
        foreach (var device in list)
        {
            var config = device.Config;
            var groupSchedule = GroupScheduleFor(device);
            if (IsSimulated(device.Host) && !config.ThermalGuard.Enabled && !config.Schedule.Enabled && groupSchedule is null && config.Soak is null && device.PendingSoak is null)
                continue;
            var state = device.State;
            var maintenance = device.Connection.InMaintenance;

            // Direkt nach dem Start: Profil noch nicht erkannt – nichts prüfen, nichts melden (sonst falsche Grenzwert-Hinweise)
            if (!device.ProfileKnown)
            {
                SetAutomationStatus(device, L.T("wartet auf die Erkennung des Geräteprofils …"));
                continue;
            }

            TickSoak(device, state, now, maintenance);

            string status;
            if (!state.Online || state.Normalized is not { } info)
            {
                status = Automation.Evaluate(config, new MinerInfo(), device.Profile, now, busy: true, maintenance, groupSchedule: groupSchedule).Status;
                SetAutomationStatus(device, status);
                continue;
            }

            var result = Automation.Evaluate(config, info, device.Profile, now,
                busy: device.IsBenchmarkRunning || device.Applying, maintenance, scheduleBlocked: config.Soak is not null, groupSchedule: groupSchedule);
            SetAutomationStatus(device, result.Status);

            foreach (var n in result.Notices)
            {
                device.AddLog($"{n.Rule}: {n.Message}", EventCategories.Automation);
                if (Config.Notifications.Wants(NotifyCategory.Maintenance))
                    SendAlert(new Alert($"auto-notice:{n.Host}:{n.Message}", $"{device.Title}: {n.Rule}", n.Message,
                        NotifyPriority.Normal, TimeSpan.FromHours(6), NotifyCategory.Maintenance, n.Host));
            }
            if (result.Action is { } a) _ = ExecuteAutomationAsync(device, info, a);
        }

        if (WebView.IsRunning) WebStatusJson = BuildStatusJson(list, now);
    }

    private void SetAutomationStatus(HubDevice device, string status)
    {
        if (device.AutomationStatus == status) return;
        device.AutomationStatus = status;
        RaiseDeviceChanged(device);
    }

    private async Task ExecuteAutomationAsync(HubDevice device, MinerInfo before, AutomationAction a)
    {
        device.Applying = true;
        try
        {
            // Ein Dauertest misst genau diese Einstellung – ein Eingriff beendet ihn mit Begründung
            if (device.Config.Soak is { } soak && a.Rule == L.T("Temperaturschutz"))
                FinishSoak(device, soak, new SoakResult(SoakOutcome.Failed, L.T("Temperaturschutz musste eingreifen ({0}).", a.Reason)));

            await device.Connection.ApplySettingsAsync(a.FrequencyMhz, a.CoreVoltageMv, TuningSource.Automatic, a.Reason);
            if (Config.RestartAfterApply) await device.Connection.RestartAsync();

            var text = L.T("{0}→{1} MHz / {2}→{3} mV – {4}", before.FrequencyMhz, a.FrequencyMhz, before.CoreVoltageMv, a.CoreVoltageMv, a.Reason);
            device.AddLog(L.T("Automatik: {0}", text), EventCategories.Automation);
            if (Config.Notifications.Wants(NotifyCategory.Maintenance))
                SendAlert(new Alert($"auto:{device.Host}:{a.FrequencyMhz}:{a.CoreVoltageMv}", $"{device.Title}: {a.Rule}", text,
                    a.Rule == L.T("Temperaturschutz") ? NotifyPriority.High : NotifyPriority.Low, TimeSpan.FromMinutes(1), NotifyCategory.Maintenance, device.Host));
        }
        catch (Exception ex)
        {
            device.AddLog(L.T("Automatik fehlgeschlagen ({0}): {1}", a.Rule, ex.Message), EventCategories.Automation);
        }
        finally
        {
            device.Applying = false;
        }
    }

    private void TickSoak(HubDevice device, MinerState state, DateTime now, bool maintenance)
    {
        // „Anwenden + Dauertest“: erst starten, wenn der Miner nach dem Neustart wieder Werte liefert
        if (device.PendingSoak is { } pending && device.Config.Soak is null)
        {
            if (!maintenance && state.Online && device.Info is not null && !device.IsBenchmarkRunning)
            {
                device.PendingSoak = null;
                StartSoak(device, pending.Hours);
            }
            else if (now - pending.Since > TimeSpan.FromMinutes(30))
            {
                device.PendingSoak = null;
                device.AddLog(L.T("Geplanter Dauertest nicht gestartet: Miner war 30 min nach der Änderung nicht bereit."), EventCategories.Soak);
            }
        }
        if (device.Config.Soak is not { } soak)
        {
            device.SoakMonitor = null;
            return;
        }
        device.SoakMonitor ??= new SoakMonitor();
        var r = device.SoakMonitor.Feed(soak, state.Online ? state.Normalized : null, device.Profile, now, maintenance);
        if (device.SoakStatus != r.Message)
        {
            device.SoakStatus = r.Message;
            RaiseDeviceChanged(device);
        }
        if (r.Outcome != SoakOutcome.Running) FinishSoak(device, soak, r);
    }

    private void FinishSoak(HubDevice device, SoakTestState soak, SoakResult r)
    {
        if (device.Config.Soak is null) return;
        var ratio = device.SoakMonitor?.CurrentRatio;
        device.Config.Soak = null;
        Config.Save();
        device.SoakMonitor = null;
        // Für den Effizienz-Ratgeber: welche Einstellung hat den Dauertest bestanden
        try
        {
            History?.AddSoakResult(new SoakResultRecord(device.Host, soak.StartedAt, Options.Clock?.Invoke() ?? DateTime.Now,
                soak.FrequencyMhz, soak.CoreVoltageMv, r.Outcome == SoakOutcome.Passed, ratio, r.Message));
        }
        catch { /* Verlauf nicht verfügbar – Dauertest-Ergebnis steht trotzdem im Protokoll */ }
        device.SoakStatus = r.Message;
        device.AddLog(r.Message, EventCategories.Soak);
        if (Config.Notifications.Wants(NotifyCategory.Maintenance))
            SendAlert(new Alert($"soak:{device.Host}:{soak.StartedAt:O}", L.T("{0}: Dauertest ", device.Title) +
                (r.Outcome == SoakOutcome.Passed ? L.T("bestanden") : r.Outcome == SoakOutcome.Failed ? L.T("fehlgeschlagen") : L.T("beendet")),
                r.Message, r.Outcome == SoakOutcome.Failed ? NotifyPriority.High : NotifyPriority.Normal, TimeSpan.FromDays(1), NotifyCategory.Maintenance, device.Host));

        SoakSuggestion? suggestion = null;
        if (r.Outcome == SoakOutcome.Failed)
        {
            var results = Benchmarks.LatestSession(device)?.Results ?? [];
            if (SoakMonitor.SuggestLower(results, soak.FrequencyMhz) is { } s)
                suggestion = new SoakSuggestion(s.Frequency, s.Voltage,
                    L.T("Dauertest fehlgeschlagen: {0}\n\nVorschlag: nächstniedrigere stabile Einstellung aus dem letzten Benchmark.", r.Message));
            else
                device.AddLog(L.T("Kein Vorschlag möglich – im letzten Benchmark gibt es keine stabile Einstellung unterhalb dieser Frequenz."), EventCategories.Soak);
        }
        device.PendingSuggestion = suggestion;
        RaiseDeviceChanged(device);
        SoakFinished?.Invoke(device, r, suggestion);
    }

    /// <summary>Stand für die Handy-Ansicht – bewusst ohne Wallet-Adressen, Pool-Benutzer und IP-Adressen.</summary>
    private string BuildStatusJson(IReadOnlyList<HubDevice> devices, DateTime now)
    {
        var miners = new List<object>();
        double hash = 0, power = 0, maxTemp = 0;
        var online = 0;
        foreach (var device in devices)
        {
            var state = device.State;
            var i = state.Info;
            var isOnline = state.Online && i is not null;
            if (isOnline)
            {
                online++;
                hash += i!.hashRate;
                power += i.power;
                maxTemp = Math.Max(maxTemp, i.temp);
            }
            var last = History?.QueryTuningEvents(device.Host, now.AddDays(-7), now).LastOrDefault();
            miners.Add(new
            {
                name = device.Title,
                online = isOnline,
                error = isOnline ? null : state.Error,
                hashrate = i?.hashRate ?? 0,
                temp = i?.temp ?? 0,
                vrTemp = i?.vrTemp ?? 0,
                power = i?.power ?? 0,
                efficiency = i is { hashRate: > 1 } ? i.power / (i.hashRate / 1000.0) : (double?)null,
                frequency = (int)(i?.frequency ?? 0),
                voltage = (int)(i?.coreVoltage ?? 0),
                pool = i is null ? "" : (i.isUsingFallbackStratum != 0 ? L.T("Fallback-Pool") : L.T("Primär-Pool")) +
                                        (i.responseTime > 0 ? L.T(" · {0} ms", i.responseTime.ToString("0", De)) : ""),
                history = state.History.TakeLast(60).Select(s => Math.Round(s.HashRateGh, 1)).ToArray(),
                lastTuning = last is null ? null : $"{L.Short(last.Time)} {last.SourceText}: {last.ChangeText}",
                automation = device.AutomationStatus.Length == 0 ? null : device.AutomationStatus,
                soak = device.Config.Soak is null ? null : device.SoakStatus,
            });
        }
        var price = Prices.PriceAt(now.ToUniversalTime());
        return JsonSerializer.Serialize(new
        {
            time = now.ToString("G", De),
            total = new
            {
                hashrate = hash,
                power,
                efficiency = hash > 1 ? power / (hash / 1000.0) : (double?)null,
                online,
                count = devices.Count,
                maxTemp,
            },
            price = price is null ? null : new { source = Prices.SourceName, ct = price },
            miners,
        });
    }
}
