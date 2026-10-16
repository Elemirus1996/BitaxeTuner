using Microsoft.Extensions.Hosting.Systemd;

namespace BitaxeTuner.Server;

/// <summary>
/// 0.9.20 (Lehre aus #21): Dienst-Watchdog für systemd (WatchdogSec in bitaxetuner.service). Meldet „WATCHDOG=1“ nur,
/// solange der Hub-Ablauf antwortet. Hängt er (z. B. weil eine Arbeit ihn blockiert), bleibt die Meldung aus und systemd
/// startet nur den Dienst neu – statt dass der ganze Rechner unbedienbar bleibt. Ohne systemd-Watchdog (Windows, Docker,
/// Entwicklung) tut dieser Dienst nichts.
/// </summary>
public sealed class SystemdWatchdog(HubService hub, ILogger<SystemdWatchdog> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!long.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out var usec) || usec <= 0) return;
        var notifier = new SystemdNotifier();
        if (!notifier.IsEnabled) return;
        // Dreimal je Watchdog-Zeit nachfragen; jede Antwort des Hub-Ablaufs innerhalb dieses Abstands zählt
        var interval = TimeSpan.FromMicroseconds(usec / 3.0);
        while (!stoppingToken.IsCancellationRequested)
        {
            var ping = hub.RunAsync(_ => true);
            var finished = await Task.WhenAny(ping, Task.Delay(interval, stoppingToken));
            if (finished == ping && ping.IsCompletedSuccessfully)
                notifier.Notify(new ServiceState("WATCHDOG=1"));
            else if (!stoppingToken.IsCancellationRequested)
                log.LogWarning("Hub-Ablauf antwortet seit {Seconds:0} s nicht – keine Watchdog-Meldung an systemd.", interval.TotalSeconds);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
