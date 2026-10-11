using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Tests;

/// <summary>0.9.15: Speicherübersicht, Sicherungen und vorherige Stände löschen.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MinerHub _hub;

    public StorageTests()
    {
        _hub = new MinerHub(new AppConfig(), new MinerHubOptions { DataDirectory = _dir.Path, OnlineChecks = false });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Lists_sizes_and_deletes_only_matching_names()
    {
        Directory.CreateDirectory(_hub.BackupDirectory);
        var backup = "bitaxetuner-backup-20261010-030000.zip";
        File.WriteAllBytes(Path.Combine(_hub.BackupDirectory, backup), new byte[2048]);
        var previous = Path.Combine(_dir.Path, "backup-20261001-120000");
        Directory.CreateDirectory(Path.Combine(previous, "tax"));
        File.WriteAllBytes(Path.Combine(previous, "history.db"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(previous, "tax", "x.json"), new byte[1024]);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "backup-keep-me"));   // passt nicht zum Muster

        var s = _hub.Storage();
        Assert.Equal(1, s.BackupCount);
        Assert.Equal(2048, s.BackupsBytes);
        Assert.Equal(5120, s.PreviousBytes);
        Assert.Equal("backup-20261001-120000", Assert.Single(s.Previous).Name);
        Assert.True(s.DataBytes >= 2048 + 5120);
        Assert.NotNull(s.FreeBytes);

        Assert.Throws<LocalizedException>(() => _hub.DeletePreviousState("backup-keep-me", "Test"));
        Assert.Throws<LocalizedException>(() => _hub.DeletePreviousState("../backup-20261001-120000", "Test"));
        Assert.Throws<LocalizedException>(() => _hub.DeleteLocalBackup("../config.json", "Test"));

        _hub.DeletePreviousState("backup-20261001-120000", "Test");
        _hub.DeleteLocalBackup(backup, "Test");
        Assert.False(Directory.Exists(previous));
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "backup-keep-me")));
        var after = _hub.Storage();
        Assert.Equal(0, after.BackupCount);
        Assert.Empty(after.Previous);
        Assert.Throws<LocalizedException>(() => _hub.DeleteLocalBackup(backup, "Test"));   // schon weg
    }

    [Fact]
    public void Table_sizes_list_rows_and_bytes()
    {
        using var store = new BitaxeTuner.Core.Monitoring.HistoryStore(Path.Combine(_dir.Path, "t.db"));
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0);
        for (var i = 0; i < 500; i++) store.AddSample("A", t0.AddMinutes(i), 1000, 50, 15, true);
        var sizes = store.TableSizes();
        var samples = sizes.Single(t => t.Name == "samples");
        Assert.Equal(500, samples.Rows);
        Assert.True(samples.Bytes is null or > 0);
        Assert.Equal("samples", sizes[0].Name);                              // größte zuerst
    }
}
