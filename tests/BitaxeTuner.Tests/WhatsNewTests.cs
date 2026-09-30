using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Tests;

/// <summary>„Neu in dieser Version“: wer gefragt wird und welche Neuerungen gezeigt werden.</summary>
public class WhatsNewTests
{
    [Fact]
    public void New_installation_is_not_asked_update_is_asked_once()
    {
        var fresh = new AppConfig();
        Assert.False(WhatsNew.ShouldAsk(fresh, "0.8.0", server: true));
        Assert.Equal("0.8.0", fresh.LastSeenVersion);                  // gemerkt – bekommt „Erste Schritte“

        var update = new AppConfig();
        update.Devices.Add(new DeviceConfig { Host = "10.0.0.5" });     // bestehende Installation (vor 0.8.0)
        Assert.True(WhatsNew.ShouldAsk(update, "0.8.0", server: true));
        Assert.Null(update.LastSeenVersion);                           // erst nach der Antwort gemerkt
        WhatsNew.MarkSeen(update, "0.8.0");
        Assert.False(WhatsNew.ShouldAsk(update, "0.8.0", server: true));
        Assert.False(WhatsNew.ShouldAsk(update, "v0.8.0", server: true)); // auch mit „v“
    }

    [Fact]
    public void Since_shows_only_what_is_new()
    {
        // Unbekannte letzte Version (vor 0.8.0): nur die Neuerungen der aktuellen Version
        var fromOld = WhatsNew.Since(null, "0.8.0", server: false);
        Assert.NotEmpty(fromOld);
        Assert.All(fromOld, f => Assert.Equal("0.8.0", f.Version));
        Assert.Empty(WhatsNew.Since(null, "0.7.0", server: false));   // Version ohne Einträge
        Assert.Empty(WhatsNew.Since("0.8.0", "0.8.0", server: false));
        Assert.Equal(fromOld.Count, WhatsNew.Since("0.7.0", "0.8.1", server: false).Count); // alles dazwischen
        Assert.Contains(WhatsNew.All(server: true), f => f.Section == "Push-Benachrichtigungen" || f.Section == "Push notifications");
    }
}
