using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Laufzeit einer weiteren Anzeige: Verbindung zum eigenen Display-Pico, Takt und Zustand der Sonderanzeigen.</summary>
public sealed class ExtraDisplayRuntime(ExtraDisplayConfig config, DisplaySceneState state)
{
    public ExtraDisplayConfig Config { get; set; } = config;
    public DisplaySceneState State { get; } = state;
    internal IFanDevice? Device;
    internal DateTime NextConnect = DateTime.MinValue;
    internal string? DeviceError;
    internal string? Error;
    internal DateTime? Shown;
    internal string AlarmKey = "";
    internal bool Requested, UserRequested, Busy, Refreshing;
    internal readonly Queue<DateTime> UserRefreshes = new();
    public DisplayScene LastScene { get; internal set; } = DisplayScene.Overview;

    public DisplayStatus Status { get; internal set; } = new(false, false, null, null, null, false);
    public string? Description => Device?.Description;
}

/// <summary>
/// 0.9.11 mehrere Anzeigen: Neben der ersten Anzeige (<see cref="AppConfig.Display"/>) beliebig viele weitere, jede mit
/// eigenem Display-Pico (WLAN oder USB mit festem Port), eigenen Seiten, eigenem Intervall und optional nur einer
/// Miner-Gruppe. Taste 1 blättert auf der eigenen Anzeige; Taste 2–4 (Lüfter, Neustart) wirken wie überall.
/// </summary>
public sealed partial class MinerHub
{
    private readonly Dictionary<string, ExtraDisplayRuntime> _extras = new();

    /// <summary>Schlüssel des WLAN-Display-Pico einer weiteren Anzeige.</summary>
    public static string ExtraPicoKey(string id) => $"{PicoKeyDisplay}.{id}";

    private string ExtraSceneFile(string id) => Path.Combine(DataDirectory, $"display-state-{id}.json");

    /// <summary>Laufzeit je konfigurierter Anzeige (neue anlegen, entfernte schließen).</summary>
    public IReadOnlyList<ExtraDisplayRuntime> ExtraRuntimes()
    {
        foreach (var gone in _extras.Keys.Where(id => Config.ExtraDisplays.All(d => d.Id != id)).ToList())
        {
            _extras[gone].Device?.Dispose();
            _extras.Remove(gone);
        }
        var list = new List<ExtraDisplayRuntime>();
        foreach (var c in Config.ExtraDisplays.Where(c => ExtraDisplayConfig.ValidId(c.Id)))
        {
            if (!_extras.TryGetValue(c.Id, out var r))
            {
                DisplaySceneState? state = null;
                try { if (File.Exists(ExtraSceneFile(c.Id))) state = JsonSerializer.Deserialize<DisplaySceneState>(File.ReadAllText(ExtraSceneFile(c.Id))); }
                catch { /* neu beginnen */ }
                _extras[c.Id] = r = new ExtraDisplayRuntime(c, state ?? new DisplaySceneState());
            }
            r.Config = c;   // Einstellungen können ersetzt worden sein
            list.Add(r);
        }
        return list;
    }

    public ExtraDisplayRuntime? ExtraDisplay(string id) => ExtraRuntimes().FirstOrDefault(r => r.Config.Id == id);

    private void CloseExtraDisplays()
    {
        foreach (var r in _extras.Values)
        {
            r.Device?.Dispose();
            r.Device = null;
            r.NextConnect = DateTime.MaxValue;
        }
    }

    /// <summary>Weitere Anzeige: Seite weiter bzw. Sonderanzeige quittieren (Taste 1 dort oder Browser).</summary>
    public void ExtraDisplayNext(ExtraDisplayRuntime display, string source)
    {
        WithDisplay(display, () => { AdvanceDisplayScene(source); return 0; });
        display.UserRequested = true;
    }

    public void RequestExtraDisplayRefresh(ExtraDisplayRuntime display) => display.Requested = true;

    /// <summary>Inhalt einer weiteren Anzeige (Vorschau im Browser): ohne Szene das, was als Nächstes käme.</summary>
    public DisplayModel ComposeExtraDisplay(ExtraDisplayRuntime display, DateTime now, DisplayScene? scene = null) =>
        WithDisplay(display, () => scene is { } sc ? PreviewScene(sc, now) : ComposeDisplay(now));

    private async Task ExtraDisplaysTickAsync(DateTime now)
    {
        foreach (var r in ExtraRuntimes())
        {
            var s = r.Config.Settings;
            if (!s.Enabled)
            {
                if (r.Device is not null) { r.Device.Dispose(); r.Device = null; }
                r.Status = new DisplayStatus(false, false, r.Shown, null, null, false);
                continue;
            }
            await ExtraDeviceTickAsync(r, now);
            await ExtraRefreshTickAsync(r, now);
        }
    }

    private async Task ExtraDeviceTickAsync(ExtraDisplayRuntime r, DateTime now)
    {
        var s = r.Config.Settings;
        if (r.Device is null && now >= r.NextConnect)
        {
            try
            {
                r.Device = await Task.Run(() => OpenExtraDevice(r.Config));
                r.DeviceError = null;
                LogEvent(null, EventCategories.Fans, L.T("Display-Pico „{0}“ verbunden: {1}", ExtraName(r.Config), r.Device.Description));
                r.Requested = true;
            }
            catch (Exception ex)
            {
                if (ex.Message != r.DeviceError)
                    LogEvent(null, EventCategories.Fans, L.T("Display-Pico „{0}“ nicht verbunden: {1}", ExtraName(r.Config), ex.Message));
                r.DeviceError = ex.Message;
                r.NextConnect = now.AddSeconds(ex is PicoUpdatedException ? 8 : 10);
            }
        }
        if (r.Device is null || r.Busy) return;
        try
        {
            await r.Device.PollAsync();
            RecordSensors(r.Device.Temperatures, now);
            await HandlePicoEventsAsync(r.Device.DrainEvents(), r);
        }
        catch (Exception ex)
        {
            r.DeviceError = L.T("Verbindung zum Display-Pico verloren: ") + ex.Message;
            r.Device?.Dispose();
            r.Device = null;
            r.NextConnect = now.AddSeconds(5);
        }
    }

    private IFanDevice OpenExtraDevice(ExtraDisplayConfig c)
    {
        var s = c.Settings;
        if (Options.DisplayDeviceFactory is { } factory) return factory(s.Connection == "wlan" ? s.NetworkHost : s.Port);
        if (s.Port == "sim") return new SimulatedFanDevice(PicoFanDevice.RoleDisplay);
        if (s.Connection != "wlan" && (s.Port.Length == 0 || s.Port == "auto"))
            throw new IOException(L.T("Weitere Anzeigen per USB brauchen einen festen Port (z. B. COM5 oder /dev/ttyACM1) – oder WLAN."));
        return s.Connection == "wlan"
            ? OpenNetworkPico(s.NetworkHost, s.NetworkIp, ExtraPicoKey(c.Id), PicoFanDevice.RoleDisplay, ip => s.NetworkIp = ip)
            : OpenUsbPico(s.Port, PicoFanDevice.RoleDisplay, forceInstall: false);
    }

    public static string ExtraName(ExtraDisplayConfig c) => c.Name.Length > 0 ? c.Name : c.Id;

    /// <summary>Wie bei der ersten Anzeige: Intervall, Ruhezeit, neue Warnungen und Tastendrücke lösen ein Neuzeichnen aus.</summary>
    private async Task ExtraRefreshTickAsync(ExtraDisplayRuntime r, DateTime now)
    {
        var s = r.Config.Settings;
        var model = WithDisplay(r, () => BuildDisplayModel(now, CurGroup));
        var alarmKey = AlarmKey(model.Alerts) + "|" + FanOverride + "|" + IsPaused;
        var interval = TimeSpan.FromMinutes(Math.Max(DisplaySettings.MinIntervalMinutes, s.IntervalMinutes));
        var quiet = s.QuietEnabled && FanController.IsNight(new CaseFanSettings { NightFromHour = s.QuietFromHour, NightToHour = s.QuietToHour }, now);
        var routineDue = r.Shown is null || (!quiet && now - r.Shown >= interval);
        var due = routineDue || r.Requested || r.UserRequested || alarmKey != r.AlarmKey;
        var earliest = ExtraEarliest(r, now);
        DateTime? next = due ? (earliest > now ? earliest : now) : r.Shown + interval;
        r.Status = new DisplayStatus(true, r.Device is not null, r.Shown, next, r.Error ?? r.DeviceError, r.Refreshing);
        if (!due || now < earliest || r.Busy || r.Device is not { } pico) return;

        r.Busy = true;
        try
        {
            var rotate = routineDue && r.Shown is not null && !r.Requested && !r.UserRequested && alarmKey == r.AlarmKey;
            var shown = WithDisplay(r, () => ComposeDisplay(now, nextPage: rotate));
            var planes = await Task.Run(() => StatusRenderer.Render(shown));
            await pico.ShowImageAsync(planes);
            WithDisplay(r, () => { SceneShown(shown); return 0; });
            if (r.UserRequested && r.Shown is not null && now < r.Shown + DisplayMinGap) r.UserRefreshes.Enqueue(now);
            r.Shown = now;
            r.AlarmKey = alarmKey;
            r.Requested = r.UserRequested = false;
            r.Refreshing = true;
            r.Error = null;
        }
        catch (Exception ex)
        {
            r.Error = L.T("Anzeige: ") + ex.Message;
            r.Shown = now;
        }
        finally
        {
            r.Busy = false;
            r.Status = new DisplayStatus(true, r.Device is not null, r.Shown, r.Shown + DisplayMinGap, r.Error ?? r.DeviceError, r.Refreshing);
        }
    }

    private static DateTime ExtraEarliest(ExtraDisplayRuntime r, DateTime now)
    {
        if (r.Shown is not { } last) return now;
        if (!r.UserRequested) return last + DisplayMinGap;
        while (r.UserRefreshes.Count > 0 && now - r.UserRefreshes.Peek() > TimeSpan.FromHours(1)) r.UserRefreshes.Dequeue();
        return r.UserRefreshes.Count >= DisplayUserRefreshPerHour ? last + DisplayMinGap : last + DisplayUserGap;
    }
}
