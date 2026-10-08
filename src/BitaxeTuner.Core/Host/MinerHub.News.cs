using BitaxeTuner.Core.Network;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.11: Neuigkeiten aus der Solo-Mining-Welt – nur geholt, wenn die Seite eingeschaltet ist oder jemand sie ansieht.</summary>
public sealed partial class MinerHub
{
    private NewsFeed? _news;

    /// <summary>Neuigkeiten (news.json aus dem Branch „news“, im Datenordner zwischengespeichert).</summary>
    public NewsFeed News => _news ??= new NewsFeed(DataDirectory);

    /// <summary>Höchstens alle 3 Stunden aus dem Internet holen; ohne Online-Abfragen (Tests, Offline-Betrieb) nie.</summary>
    public Task RefreshNewsAsync(bool force) =>
        Options.OnlineChecks ? News.RefreshAsync(DateTime.UtcNow, force) : Task.CompletedTask;
}
