using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.11 Wartungsmodus je Miner: Überwachung pausiert, während am Miner gearbeitet wird.</summary>
public sealed class MaintenanceModeTests : IDisposable
{
    private static readonly DeviceProfile Gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, SimulatedMinerClient> _sims = new();
    private readonly List<string> _sent = [];
    private readonly MinerHub _hub;
    private DateTime _now = new(2026, 10, 8, 12, 0, 0);

    public MaintenanceModeTests()
    {
        var config = new AppConfig { Notifications = { Provider = "ntfy", NtfyTopic = "t" } };
        config.Devices.Add(new DeviceConfig { Name = "Werkbank", Host = "10.0.7.1" });
        _hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = _dir.Path,
            OnlineChecks = false,
            ClientFactory = h => _sims[h] = new SimulatedMinerClient(Gamma, seed: 3, address: h),
            Clock = () => _now,
        });
        _hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        _hub.Notify.Sending += (key, _, _, _) => { lock (_sent) _sent.Add(key); };
    }

    public void Dispose()
    {
        _hub.Dispose();
        _dir.Dispose();
    }

    private HubDevice D => _hub.Device("10.0.7.1")!;

    /// <summary>Abfragen im Minutenabstand (Testuhr); die Verbindung puffert nach echter Zeit, daher kurz warten.</summary>
    private async Task PollMinutesAsync(int polls)
    {
        for (var i = 0; i < polls; i++)
        {
            _now = _now.AddMinutes(1);
            await Task.Delay(MinerConnection.CacheAge + TimeSpan.FromMilliseconds(100));
            await _hub.PollNowAsync();
        }
    }

    [Fact]
    public async Task Switched_off_miner_in_maintenance_raises_no_alerts_and_no_downtime()
    {
        await PollMinutesAsync(1);
        _hub.SetMaintenanceMode(D, true, 2, "Test");
        Assert.True(D.Config.MaintenanceMode);
        Assert.True(_hub.Maintenance.IsActive(D.Host));
        Assert.Equal("Wartung bis " + L(_now.AddHours(2)), MinerHub.MaintenanceText(D.Config));

        _sims[D.Host].Offline = true;                                              // Miner abgeschaltet
        await PollMinutesAsync(4);
        Assert.DoesNotContain(_sent, k => k.StartsWith("offline:"));
        var (online, total) = _hub.History!.MinuteCounts(D.Host, _now.AddHours(-1), _now.AddMinutes(1));
        Assert.Equal(online, total);                                              // keine Ausfallminuten

        // Blockfunde kommen trotzdem, alles andere zu diesem Miner nicht
        Assert.Equal(NotificationService.SendOutcome.NoTarget,
            await _hub.Notify.SendAsync("hot:x", "heiß", "x", category: NotifyCategory.Overheat, host: D.Host));
        Assert.NotEqual(NotificationService.SendOutcome.NoTarget,
            await _hub.Notify.SendAsync("block:x", "Block", "x", category: NotifyCategory.Finds, host: D.Host));

        // Nach Ablauf endet der Wartungsmodus von selbst, dann greift wieder die normale Offline-Regel
        _now = _now.AddHours(2);
        await PollMinutesAsync(1);
        Assert.False(D.Config.MaintenanceMode);
        Assert.False(_hub.Maintenance.IsManual(D.Host));
        Assert.Contains(D.LogLines, l => l.Contains("Wartungsmodus aus"));
        _now = _now.AddMinutes(3);                                                 // Nachlauf vorbei, dann drei Fehlversuche
        await PollMinutesAsync(3);
        Assert.Contains(_sent, k => k.StartsWith("offline:"));
    }

    [Fact]
    public async Task Maintenance_mode_survives_a_restart_and_can_be_ended_by_hand()
    {
        await PollMinutesAsync(1);
        _hub.SetMaintenanceMode(D, true, 0, "Test");
        Assert.Null(D.Config.MaintenanceUntil);                                   // 0 = bis zum Ausschalten
        Assert.Equal("Wartung", MinerHub.MaintenanceText(D.Config));
        Assert.Throws<BitaxeTuner.Core.I18n.LocalizedException>(() => _hub.SetMaintenanceMode(D, true, -1, "Test"));

        _hub.SyncDevices();                                                        // z. B. nach dem Laden der Einstellungen
        Assert.True(_hub.Maintenance.IsManual(D.Host));

        _now = _now.AddDays(3);
        await PollMinutesAsync(1);
        Assert.True(D.Config.MaintenanceMode);                                     // ohne Ablaufzeit bleibt er an
        _hub.SetMaintenanceMode(D, false, null, "Test");
        Assert.False(_hub.Maintenance.IsManual(D.Host));
        Assert.Null(D.Config.MaintenanceSince);
    }

    private static string L(DateTime t) => BitaxeTuner.Core.I18n.L.Short(t);
}
