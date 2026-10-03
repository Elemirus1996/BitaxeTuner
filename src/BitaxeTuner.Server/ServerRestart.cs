namespace BitaxeTuner.Server;

/// <summary>
/// Dienst neu starten, z. B. nach dem Umstellen auf HTTPS. Beendet den Prozess mit Fehlercode, damit der Dienstverwalter
/// ihn wieder startet: systemd (Restart=always), Docker (restart: unless-stopped), Windows-Dienst (Wiederherstellung
/// „Neustart“ bei Fehler). Von Hand gestartet: der Server muss neu gestartet werden.
/// </summary>
public class ServerRestart(HubService hub, ILogger<ServerRestart> log)
{
    public virtual void Schedule(string reason)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);   // Antwort an den Browser/die App zuerst ausliefern
            try
            {
                await hub.RunAsync(async h =>
                {
                    await h.Benchmarks.StopAllAsync();
                    h.Config.Save();
                    return true;
                });
            }
            catch { /* Neustart trotzdem */ }
            log.LogWarning("Neustart: {Reason}", reason);
            Environment.Exit(3);
        });
    }
}
