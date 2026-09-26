using System.Globalization;
using System.Text.Json;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Nach jeder zentralen Abfragerunde (UI-Thread): Temperaturschutz, Zeitplan/Strompreis, Dauertests auswerten,
/// freigegebene Änderungen ausführen, melden und protokollieren. Baut außerdem den Stand für die Handy-Ansicht.
/// </summary>
public sealed class AutomationCoordinator(AppHost host)
{
    private readonly Dictionary<string, SoakMonitor> _soaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _applying = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public void Tick(IEnumerable<DeviceViewModel> devices)
    {
        var now = DateTime.Now;
        var list = devices.ToList();

        // Preise nur abrufen, wenn eine Quelle eingestellt ist (höchstens alle 30 min)
        if (host.Config.PriceSource.Source != "none") _ = host.Prices.RefreshAsync(now.ToUniversalTime());

        foreach (var vm in list)
        {
            if (AppHost.IsSimulated(vm.Address) && !vm.Config.ThermalGuard.Enabled && !vm.Config.Schedule.Enabled && vm.Config.Soak is null)
                continue;
            var state = host.Polling.State(vm.Address);
            var connection = host.Polling.Connection(vm.Address);
            if (state is null || connection is null) continue;
            var maintenance = connection.InMaintenance;

            TickSoak(vm, state, now, maintenance);

            if (!state.Online || state.Normalized is not { } info)
            {
                vm.AutomationStatus = host.Automation.Evaluate(vm.Config, new MinerInfo(), vm.Profile, now, busy: true, maintenance).Status;
                continue;
            }

            var result = host.Automation.Evaluate(vm.Config, info, vm.Profile, now,
                busy: vm.IsRunning || _applying.Contains(vm.Address), maintenance, scheduleBlocked: vm.Config.Soak is not null);
            vm.AutomationStatus = result.Status;

            foreach (var n in result.Notices)
            {
                vm.AddLog($"{n.Rule}: {n.Message}");
                if (host.Config.Notifications.OnMaintenance)
                    host.SendAlert(new Alert($"auto-notice:{n.Host}:{n.Message}", $"{vm.Title}: {n.Rule}", n.Message,
                        NotifyPriority.Normal, TimeSpan.FromHours(6)));
            }
            if (result.Action is { } a) _ = ExecuteAsync(vm, connection, info, a);
        }

        if (host.WebView.IsRunning) host.WebStatusJson = BuildStatusJson(list, now);
    }

    private async Task ExecuteAsync(DeviceViewModel vm, MinerConnection connection, MinerInfo before, AutomationAction a)
    {
        _applying.Add(vm.Address);
        try
        {
            // Ein Dauertest misst genau diese Einstellung – ein Eingriff beendet ihn mit Begründung
            if (vm.Config.Soak is { } soak && a.Rule == "Temperaturschutz")
                await FinishSoakAsync(vm, soak, new SoakResult(SoakOutcome.Failed, $"Temperaturschutz musste eingreifen ({a.Reason})."));

            await connection.ApplySettingsAsync(a.FrequencyMhz, a.CoreVoltageMv, TuningSource.Automatic, a.Reason);
            if (host.Config.RestartAfterApply) await connection.RestartAsync();

            var text = $"{before.FrequencyMhz}→{a.FrequencyMhz} MHz / {before.CoreVoltageMv}→{a.CoreVoltageMv} mV – {a.Reason}";
            vm.AddLog($"Automatik: {text}");
            if (host.Config.Notifications.OnMaintenance)
                host.SendAlert(new Alert($"auto:{vm.Address}:{a.FrequencyMhz}:{a.CoreVoltageMv}", $"{vm.Title}: {a.Rule}", text,
                    a.Rule == "Temperaturschutz" ? NotifyPriority.High : NotifyPriority.Low, TimeSpan.FromMinutes(1)));
        }
        catch (Exception ex)
        {
            vm.AddLog($"Automatik fehlgeschlagen ({a.Rule}): {ex.Message}");
        }
        finally
        {
            _applying.Remove(vm.Address);
        }
    }

    private void TickSoak(DeviceViewModel vm, MinerState state, DateTime now, bool maintenance)
    {
        if (vm.Config.Soak is not { } soak)
        {
            _soaks.Remove(vm.Address);
            return;
        }
        if (!_soaks.TryGetValue(vm.Address, out var monitor)) _soaks[vm.Address] = monitor = new SoakMonitor();
        var r = monitor.Feed(soak, state.Online ? state.Normalized : null, vm.Profile, now, maintenance);
        vm.SoakStatus = r.Message;
        if (r.Outcome != SoakOutcome.Running) _ = FinishSoakAsync(vm, soak, r);
    }

    private async Task FinishSoakAsync(DeviceViewModel vm, Core.Config.SoakTestState soak, SoakResult r)
    {
        if (vm.Config.Soak is null) return;
        vm.Config.Soak = null;
        host.Config.Save();
        _soaks.Remove(vm.Address);
        if (host.Config.Notifications.OnMaintenance)
            host.SendAlert(new Alert($"soak:{vm.Address}:{soak.StartedAt:O}", $"{vm.Title}: Dauertest " +
                (r.Outcome == SoakOutcome.Passed ? "bestanden" : r.Outcome == SoakOutcome.Failed ? "fehlgeschlagen" : "beendet"),
                r.Message, r.Outcome == SoakOutcome.Failed ? NotifyPriority.High : NotifyPriority.Normal, TimeSpan.FromDays(1)));
        await vm.OnSoakFinishedAsync(r, soak);
    }

    /// <summary>Stand für die Handy-Ansicht – bewusst ohne Wallet-Adressen, Pool-Benutzer und IP-Adressen.</summary>
    private string BuildStatusJson(List<DeviceViewModel> devices, DateTime now)
    {
        var miners = new List<object>();
        double hash = 0, power = 0, maxTemp = 0;
        var online = 0;
        foreach (var vm in devices)
        {
            var state = host.Polling.State(vm.Address);
            var i = state?.Info;
            var isOnline = state?.Online == true && i is not null;
            if (isOnline)
            {
                online++;
                hash += i!.hashRate;
                power += i.power;
                maxTemp = Math.Max(maxTemp, i.temp);
            }
            var last = host.History?.QueryTuningEvents(vm.Address, now.AddDays(-7), now).LastOrDefault();
            miners.Add(new
            {
                name = vm.Title,
                online = isOnline,
                error = isOnline ? null : state?.Error,
                hashrate = i?.hashRate ?? 0,
                temp = i?.temp ?? 0,
                vrTemp = i?.vrTemp ?? 0,
                power = i?.power ?? 0,
                efficiency = i is { hashRate: > 1 } ? i.power / (i.hashRate / 1000.0) : (double?)null,
                frequency = (int)(i?.frequency ?? 0),
                voltage = (int)(i?.coreVoltage ?? 0),
                pool = i is null ? "" : (i.isUsingFallbackStratum != 0 ? "Fallback-Pool" : "Primär-Pool") +
                                        (i.responseTime > 0 ? $" · {i.responseTime.ToString("0", De)} ms" : ""),
                history = state?.History.TakeLast(60).Select(s => Math.Round(s.HashRateGh, 1)).ToArray() ?? [],
                lastTuning = last is null ? null : $"{last.Time.ToString("dd.MM. HH:mm", De)} {last.SourceText}: {last.ChangeText}",
                automation = vm.AutomationStatus == "keine Automatik" ? null : vm.AutomationStatus,
                soak = vm.Config.Soak is null ? null : vm.SoakStatus,
            });
        }
        var price = host.Prices.PriceAt(now.ToUniversalTime());
        return JsonSerializer.Serialize(new
        {
            time = now.ToString("dd.MM.yyyy HH:mm:ss", De),
            total = new
            {
                hashrate = hash,
                power,
                efficiency = hash > 1 ? power / (hash / 1000.0) : (double?)null,
                online,
                count = devices.Count,
                maxTemp,
            },
            price = price is null ? null : new { source = host.Prices.SourceName, ct = price },
            miners,
        });
    }
}
