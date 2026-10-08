using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.11 Wartungsmodus je Miner: Während am Miner gearbeitet wird (abgeschaltet, umgebaut, neu geflasht), pausiert die
/// Überwachung – keine Meldungen außer Blockfunden, kein Watchdog-Neustart, keine Automatik, Pool-Umschaltung oder
/// Temperatur-Absenkung, und die Zeit zählt nicht als Ausfall. Die Werte werden weiter abgefragt und angezeigt.
/// </summary>
public sealed partial class MinerHub
{
    /// <summary>Wartungsmodus ein- oder ausschalten; <paramref name="hours"/> &gt; 0 = endet danach von selbst.</summary>
    public void SetMaintenanceMode(HubDevice device, bool on, double? hours, string who)
    {
        var c = device.Config;
        var now = Now();
        if (on)
        {
            if (hours is < 0 or > 24 * 30) throw new LocalizedException("Dauer bitte zwischen 0 und 720 Stunden angeben (0 = bis zum Ausschalten).");
            c.MaintenanceMode = true;
            c.MaintenanceSince ??= now;
            c.MaintenanceUntil = hours is > 0 ? now.AddHours(hours.Value) : null;
        }
        else
        {
            if (!c.MaintenanceMode) return;
            c.MaintenanceMode = false;
            c.MaintenanceSince = null;
            c.MaintenanceUntil = null;
            Maintenance.Extend(device.Host, MaintenanceTracker.DefaultTail);   // kurzer Nachlauf, bis der Miner wieder läuft
        }
        Maintenance.SetManual(device.Host, on);
        Config.Save();
        var text = on
            ? (c.MaintenanceUntil is { } until
                ? L.T("Wartungsmodus an bis {0} ({1}) – Überwachung pausiert", L.Short(until), who)
                : L.T("Wartungsmodus an ({0}) – Überwachung pausiert", who))
            : L.T("Wartungsmodus aus ({0}) – Überwachung läuft wieder", who);
        device.AddLog(text, EventCategories.Automation);   // Verlauf und Protokoll
        DevicesChanged?.Invoke();
    }

    /// <summary>Text für Status und Anzeige, z. B. „Wartung bis 18:00“; null = kein Wartungsmodus.</summary>
    public static string? MaintenanceText(DeviceConfig c) =>
        !c.MaintenanceMode ? null
        : c.MaintenanceUntil is { } until ? L.T("Wartung bis {0}", L.Short(until)) : L.T("Wartung");

    /// <summary>Wartungsmodus mit Ablaufzeit automatisch beenden.</summary>
    private void TickMaintenanceMode(DateTime now)
    {
        foreach (var d in Devices)
            if (d.Config is { MaintenanceMode: true, MaintenanceUntil: { } until } && now >= until)
                SetMaintenanceMode(d, false, null, L.T("Zeit abgelaufen"));
    }

    private DateTime Now() => Options.Clock?.Invoke() ?? DateTime.Now;
}
