using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Tests;

/// <summary>0.9.24: „Rückgängig“ nach dem Speichern der Einstellungen in der Desktop-App.</summary>
public class ConfigSnapshotTests
{
    [Fact]
    public void Restore_puts_the_old_values_back_into_the_same_object()
    {
        var config = new AppConfig { TempWarn = 65, FilePath = "x/config.json" };
        config.Devices.Add(new DeviceConfig { Name = "Alpha", Host = "10.0.24.1" });
        config.Notifications.NtfyTopic = "geheim-topic-test";
        var before = ConfigSnapshot.Take(config);

        config.TempWarn = 80;
        config.Devices.Add(new DeviceConfig { Name = "Beta", Host = "10.0.24.2" });
        config.Notifications.NtfyTopic = "";
        var same = config;
        before.RestoreInto(config);

        Assert.Same(same, config);
        Assert.Equal(65, config.TempWarn);
        Assert.Equal("Alpha", Assert.Single(config.Devices).Name);
        Assert.Equal("geheim-topic-test", config.Notifications.NtfyTopic);   // Tokens gehören zum Stand
        Assert.Equal("x/config.json", config.FilePath);                        // Laufzeitfeld bleibt
    }
}
