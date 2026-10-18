namespace BitaxeTuner.Server.Api;

/// <summary>
/// 0.9.24 „Rückgängig“ nach dem Speichern: merkt sich den Stand der Einstellungen vor dem letzten Speichern (nur im
/// Speicher, nur die letzte Fassung). Der Browser bietet den Knopf 10 s an; der Server nimmt ihn bis zu einer Minute an,
/// damit langsame Verbindungen nicht scheitern. Die PIN für „Nur ansehen“ wird nicht zurückgesetzt (nur ihr Hash ist bekannt).
/// Gilt nur für BitaxeTuner-Einstellungen, nie für Werte am Miner.
/// </summary>
public sealed class SettingsUndo
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private (SettingsDto Before, DateTime Saved)? _last;

    /// <summary>Uhr (für Tests austauschbar).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public void Remember(SettingsDto before)
    {
        lock (_lock) _last = (before, Now());
    }

    /// <summary>Gemerkten Stand einmalig herausgeben; null, wenn keiner da oder zu alt.</summary>
    public (SettingsDto Before, DateTime Saved)? Take()
    {
        lock (_lock)
        {
            var last = _last;
            _last = null;
            return last is { } l && Now() - l.Saved <= Window ? l : null;
        }
    }
}
