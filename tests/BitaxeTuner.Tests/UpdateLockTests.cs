using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Tests;

/// <summary>0.9.15: Nur ein Server-Update gleichzeitig – egal von welchem Gerät.</summary>
public sealed class UpdateLockTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MinerHub _hub;

    public UpdateLockTests() => _hub = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = _dir.Path, OnlineChecks = false });

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Second_device_is_refused_until_the_owner_releases()
    {
        Assert.Null(_hub.RunningUpdate);
        Assert.True(_hub.TryBeginUpdate("v1.0.0", "Browser", null, out var mine));
        Assert.Equal("backup", mine.Phase);

        // Anderes Gerät (ohne bzw. mit fremder Kennung): abgelehnt, bekommt den laufenden Stand
        Assert.False(_hub.TryBeginUpdate("v1.0.0", "Desktop-App", null, out var other));
        Assert.Equal("Browser", other.By);
        Assert.False(_hub.TryBeginUpdate("v1.0.0", "Desktop-App", "falsch", out _));

        // Eigentümer darf mit seiner Kennung weitermachen (Sicherung → Installation)
        Assert.True(_hub.TryBeginUpdate("v1.0.0", "Browser", mine.Id, out var same));
        Assert.Equal(mine.Id, same.Id);
        _hub.SetUpdatePhase("install");
        Assert.Equal("install", _hub.RunningUpdate!.Phase);

        // Fremde Kennung gibt nichts frei, die eigene schon
        _hub.EndUpdate("Test", "falsch");
        Assert.NotNull(_hub.RunningUpdate);
        _hub.EndUpdate("Sicherung fehlgeschlagen", mine.Id);
        Assert.Null(_hub.RunningUpdate);
        Assert.True(_hub.TryBeginUpdate("v1.0.0", "Desktop-App", null, out _));
    }

    [Fact]
    public void Abandoned_lock_expires()
    {
        var old = new UpdateRun("x", "v1", "Browser", DateTime.UtcNow - MinerHub.UpdateStaleBackup - TimeSpan.FromMinutes(1), "backup");
        Assert.True(old.IsStale(DateTime.UtcNow));
        Assert.False((old with { Phase = "install" }).IsStale(DateTime.UtcNow));   // Installation darf länger dauern
    }
}
