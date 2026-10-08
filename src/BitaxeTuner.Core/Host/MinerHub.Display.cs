using System.Globalization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

public sealed record DisplayStatus(bool Enabled, bool Connected, DateTime? LastShown, DateTime? NextDue, string? Error, bool Refreshing);

/// <summary>
/// E-Paper-Anzeige und Taster am Pico.
/// <list type="bullet">
/// <item>Anzeige: regelmäßig (Standard 5 min) und bei neuen Warnungen – nie öfter als alle 3 Minuten;
/// auf Tastendruck frühestens 30 s nach der letzten Aktualisierung (höchstens 10-mal pro Stunde).</item>
/// <item>Taster: 1 = Anzeige weiter / quittieren, 2 = Lüfter Automatik, 3 = alle 100 %, 3 (5 s halten) = Lüfter aus
/// (Sicherheitsregeln bleiben), 4 (3 s halten) = Neustart Pico und Rechner.</item>
/// <item>„Aus“ und „100 %“ gelten bis zum nächsten Neustart des Servers.</item>
/// </list>
/// </summary>
public sealed partial class MinerHub
{
    private static readonly TimeSpan DisplayMinGap = TimeSpan.FromMinutes(DisplaySettings.MinIntervalMinutes);
    /// <summary>Auf Tastendruck darf die Anzeige schneller reagieren – aber nicht beliebig oft (Panel schonen).</summary>
    public static readonly TimeSpan DisplayUserGap = TimeSpan.FromSeconds(30);
    public const int DisplayUserRefreshPerHour = 10;
    private readonly Queue<DateTime> _displayUserRefreshes = new();
    private bool _displayUserRequested;
    private DateTime? _displayShown;
    private string _displayAlarmKey = "";
    private bool _displayRequested, _displayBusy, _displayRefreshing;
    private string? _displayError;
    private bool _rebooting;

    /// <summary>Vorgabe per Taste/Browser (nicht gespeichert).</summary>
    public FanOverride FanOverride { get; private set; }

    public DisplayStatus DisplayStatus { get; private set; } = new(false, false, null, null, null, false);

    // ---------- Tasten und Vorgaben ----------

    /// <summary>Lüfter-Vorgabe setzen (Taste oder Browser) und sofort anwenden.</summary>
    public async Task SetFanOverrideAsync(FanOverride mode, string source)
    {
        if (FanOverride != mode)
        {
            FanOverride = mode;
            var text = mode switch
            {
                FanOverride.Off => L.T("Zusatzlüfter AUS (Sicherheitsregeln bleiben aktiv)"),
                FanOverride.Full => L.T("alle Zusatzlüfter 100 %"),
                _ => L.T("Zusatzlüfter wieder nach Einstellung (Automatik)"),
            };
            RaiseStatus(true, $"{text} – {source}");
            foreach (var c in Config.Fans.Channels.Where(c => c.Role == "miner"))
                Device(c.MinerHost ?? "")?.AddLog(L.T("Lüfter K{0}: {1} ({2})", c.Channel, text, source), EventCategories.Fans);
            _displayRequested = true;
        }
        await FanTickAsync();
    }

    /// <param name="display">0.9.11: Ereignisse eines weiteren Display-Pico (Taste 1 blättert dann dort).</param>
    private async Task HandlePicoEventsAsync(IReadOnlyList<string> events, ExtraDisplayRuntime? display = null)
    {
        foreach (var e in events)
        {
            if (e.StartsWith("EPD DONE", StringComparison.Ordinal))
            {
                if (display is null) _displayRefreshing = false; else display.Refreshing = false;
                // 8.1: BUSY-Leitung meldete nie „beschäftigt“ – der Pico hat mit fester Wartezeit gearbeitet; einmal je Verbindung melden
                if (e.Contains(" busy=0", StringComparison.Ordinal) && _busyWarned.Add(display?.Config.Id ?? ""))
                    LogEvent(null, EventCategories.Fans, L.T("E-Paper: Die BUSY-Leitung meldet nie „beschäftigt“ – der Pico wartet deshalb fest 25 Sekunden. Steckverbindung zum Display prüfen (BUSY = GP13 beim Waveshare-Board)."));
                continue;
            }
            if (e.StartsWith("INFO ", StringComparison.Ordinal))
            {
                if (PicoInfoText(e) is { } text) LogEvent(null, EventCategories.Fans, text);
                continue;
            }
            if (!(display?.Config.Settings ?? Config.Display).ButtonsEnabled) continue;
            switch (e)
            {
                case "BTN 1" when display is not null: ExtraDisplayNext(display, L.T("Taste 1")); break;
                case "BTN 1": DisplayNextOrAcknowledge(L.T("Taste 1")); break;
                case "BTN 2": await SetFanOverrideAsync(FanOverride.None, L.T("Taste 2")); break;
                case "BTN 3": await SetFanOverrideAsync(FanOverride.Full, L.T("Taste 3")); break;
                case "BTN 3 LONG": await SetFanOverrideAsync(FanOverride.Off, L.T("Taste 3 lang")); break;
                case "BTN 4 LONG": _ = RebootAsync(L.T("Taste 4")); break;
            }
        }
    }

    private readonly HashSet<string> _busyWarned = [];

    /// <summary>
    /// 8.1: „INFO reset=wdt|power|other err=…“ vom Pico verständlich machen. Ein normaler Start (Strom an, ohne Fehler)
    /// wird nicht protokolliert.
    /// </summary>
    internal static string? PicoInfoText(string line)
    {
        string? reset = null, err = null;
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (part.StartsWith("reset=", StringComparison.Ordinal)) reset = part[6..];
            else if (part.StartsWith("err=", StringComparison.Ordinal)) err = part[4..].Replace('_', ' ');
        }
        if (err is "-" or "") err = null;
        if (err is { Length: > 120 }) err = err[..120];
        var why = reset == "wdt" ? L.T("Der Pico wurde vom Watchdog neu gestartet (Programm hing).") : null;
        if (why is null && err is null) return null;
        return string.Join(" ", new[] { why, err is null ? null : L.T("Letzter Fehler im Pico-Programm: {0}", err) }.Where(x => x is not null));
    }

    /// <summary>Bei „Aus“ musste die Sicherheitsregel einen Lüfter trotzdem einschalten → einmal melden.</summary>
    private void ReportSafetyOverrides(IReadOnlyList<FanTarget> targets)
    {
        if (FanOverride != FanOverride.Off || !Config.Notifications.Wants(NotifyCategory.Overheat)) return;
        foreach (var t in targets.Where(t => t.SafetyOverride))
            SendAlert(new Alert($"fan-safety:{t.Channel}", L.T("Lüfter K{0} läuft trotz „Aus“", t.Channel), t.Reason, NotifyPriority.High, TimeSpan.FromHours(1), NotifyCategory.Overheat));
    }

    /// <summary>
    /// Neustart (Taste 4 lange oder Browser): Benchmarks sauber beenden, Stand speichern, Pico neu starten,
    /// danach – falls erlaubt und verfügbar – den Rechner (Pi). Ohne Rechner-Neustart nur der Pico.
    /// </summary>
    public async Task<string> RebootAsync(string source)
    {
        if (_rebooting) return L.T("Neustart läuft bereits.");
        _rebooting = true;
        try
        {
            RaiseStatus(true, L.T("Neustart ausgelöst ({0}) – beende Benchmarks …", source));
            await Benchmarks.StopAllAsync();
            Config.Save();
            if (_fanDevice is { } pico)
            {
                try { await pico.ResetAsync(); } catch { /* Pico startet ohnehin neu oder ist weg */ }
                CloseFanDevice();
                _fanNextConnect = DateTime.Now.AddSeconds(5);
            }
            if (_displayDevice is { } display)
            {
                try { await display.ResetAsync(); } catch { /* Pico startet ohnehin neu oder ist weg */ }
                CloseDisplayDevice();
                _displayNextConnect = DateTime.Now.AddSeconds(5);
            }
            foreach (var x in _extras.Values.Where(x => x.Device is not null))
            {
                try { await x.Device!.ResetAsync(); } catch { /* Pico startet ohnehin neu oder ist weg */ }
                x.Device?.Dispose();
                x.Device = null;
                x.NextConnect = DateTime.Now.AddSeconds(5);
            }
            if (Config.Display.AllowSystemReboot && Options.SystemReboot is { } reboot)
            {
                RaiseStatus(true, L.T("Rechner wird neu gestartet …"));
                await reboot();
                return L.T("Pico und Rechner werden neu gestartet.");
            }
            return L.T("Pico neu gestartet (Neustart des Rechners ist hier nicht eingerichtet).");
        }
        finally
        {
            _rebooting = false;
        }
    }

    // ---------- Anzeige ----------

    /// <summary>Anzeige möglichst bald neu aufbauen (frühestens 3 Minuten nach dem letzten Mal).</summary>
    public void RequestDisplayRefresh() => _displayRequested = true;

    /// <summary>Taste 1 / Browser: Sonderanzeige quittieren, sonst nächste Seite – mit kurzer Wartezeit statt 3 Minuten.</summary>
    public void DisplayNextOrAcknowledge(string source)
    {
        AdvanceDisplayScene(source);
        _displayUserRequested = true;
    }

    /// <summary>Frühester Zeitpunkt für die nächste Aktualisierung.</summary>
    private DateTime DisplayEarliest(DateTime now)
    {
        if (_displayShown is not { } last) return now;
        if (!_displayUserRequested) return last + DisplayMinGap;
        while (_displayUserRefreshes.Count > 0 && now - _displayUserRefreshes.Peek() > TimeSpan.FromHours(1)) _displayUserRefreshes.Dequeue();
        return _displayUserRefreshes.Count >= DisplayUserRefreshPerHour ? last + DisplayMinGap : last + DisplayUserGap;
    }

    private async Task DisplayTickAsync(DateTime now)
    {
        var s = Config.Display;
        if (!s.Enabled)
        {
            DisplayStatus = new DisplayStatus(false, false, _displayShown, null, null, false);
            return;
        }
        var model = BuildDisplayModel(now);
        // Nur neue/weggefallene Warnungen lösen ein Neuzeichnen aus, nicht schwankende Werte darin (schont das Panel)
        var alarmKey = AlarmKey(model.Alerts) + "|" + FanOverride + "|" + IsPaused;
        var interval = TimeSpan.FromMinutes(Math.Max(DisplaySettings.MinIntervalMinutes, s.IntervalMinutes));
        var quiet = s.QuietEnabled && FanController.IsNight(new CaseFanSettings { NightFromHour = s.QuietFromHour, NightToHour = s.QuietToHour }, now);
        var routineDue = _displayShown is null || (!quiet && now - _displayShown >= interval);
        var due = routineDue || _displayRequested || _displayUserRequested || alarmKey != _displayAlarmKey;
        var earliest = DisplayEarliest(now);
        DateTime? next = due ? (earliest > now ? earliest : now) : _displayShown + interval;
        DisplayStatus = new DisplayStatus(true, DisplayPico is not null, _displayShown, next, _displayError ?? DisplayDeviceError, _displayRefreshing);

        if (!due || now < earliest || _displayBusy || DisplayPico is not { } pico) return;
        _displayBusy = true;
        try
        {
            // Regelmäßige Aktualisierung ohne besonderen Anlass: nächste Seite
            var rotate = routineDue && _displayShown is not null && !_displayRequested && !_displayUserRequested && alarmKey == _displayAlarmKey;
            var shown = ComposeDisplay(now, nextPage: rotate);
            var planes = await Task.Run(() => StatusRenderer.Render(shown));
            await pico.ShowImageAsync(planes);
            SceneShown(shown);
            if (_displayUserRequested && _displayShown is not null && now < _displayShown + DisplayMinGap) _displayUserRefreshes.Enqueue(now);
            _displayShown = now;
            _displayAlarmKey = alarmKey;
            _displayRequested = false;
            _displayUserRequested = false;
            _displayRefreshing = true;
            _displayError = null;
        }
        catch (Exception ex)
        {
            _displayError = L.T("Anzeige: ") + ex.Message;
            _displayShown = now; // nicht sofort erneut versuchen (Mindestpause)
        }
        finally
        {
            _displayBusy = false;
            DisplayStatus = new DisplayStatus(true, DisplayPico is not null, _displayShown, _displayShown + DisplayMinGap, _displayError ?? DisplayDeviceError, _displayRefreshing);
        }
    }

    /// <summary>Aktueller Inhalt der Anzeige (auch für die Vorschau im Browser).</summary>
    public DisplayModel BuildDisplayModel(DateTime now, string? group = null)
    {
        var devices = Devices.Where(d => !d.IsSimulated || Devices.All(x => x.IsSimulated))
            .Where(d => group is null || d.Config.Groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase))).ToList();
        var fans = FanStatus.Channels;
        var alerts = new List<string>();
        var miners = new List<DisplayMiner>();
        foreach (var d in devices)
        {
            var s = d.State;
            var i = s.Online ? s.Info : null;
            var fan = fans.FirstOrDefault(c => c.Role == "miner" && string.Equals(c.MinerHost?.Trim(), d.Config.Host.Trim(), StringComparison.OrdinalIgnoreCase));
            var chipHot = i is not null && i.temp >= Config.TempWarn;
            var vrHot = i is not null && i.vrTemp >= Math.Max(Config.TempWarn + 10, 80);
            var maintenance = d.Connection.InMaintenance;
            miners.Add(new DisplayMiner(d.Title, s.Online, maintenance, i?.hashRate, i?.temp, i?.vrTemp > 0 ? i.vrTemp : null,
                // Zusatzlüfter am Pico, sonst der eigene Lüfter des Miners (AxeOS)
                fan?.Percent ?? (i is { fanspeed: > 0 } ? (int)Math.Round(i.fanspeed) : null),
                chipHot, vrHot, fan?.Stalled == true, s.Online ? null : s.Error));
            if (!s.Online && !maintenance) alerts.Add(L.T("{0} offline", d.Title));
            if (chipHot) alerts.Add(L.T("{0} Chip {1} °C", d.Title, i!.temp.ToString("0", L.Culture)));
            if (vrHot) alerts.Add(L.T("{0} VR {1} °C", d.Title, i!.vrTemp.ToString("0", L.Culture)));
        }
        foreach (var c in fans.Where(c => c.Stalled)) alerts.Add(L.T("Lüfter K{0} steht", c.Channel));
        var temps = (FanStatus.Sensors ?? []).Where(s => s.ShowOnDisplay).Select(s => new DisplayTemp(s.Name, s.Temp, s.Hot)).ToList();
        foreach (var s in (FanStatus.Sensors ?? []).Where(s => s.Hot))
            alerts.Add($"{s.Name} {s.Temp!.Value.ToString("0.0", L.Culture)} °C");
        if (FanStatus.Connected)
            foreach (var s in (FanStatus.Sensors ?? []).Where(s => s.Temp is null)) alerts.Add(L.T("Fühler {0} fehlt", s.Name));
        if (Config.Fans.Enabled && !FanStatus.Connected) alerts.Add(L.T("Pico-Lüfter getrennt"));

        var online = devices.Where(d => d.State.Online && d.State.Info is not null).Select(d => d.State.Info!).ToList();
        var gh = online.Sum(x => x.hashRate);
        var w = online.Sum(x => x.power);
        var caseFan = fans.FirstOrDefault(c => c.Role == "case");
        var fanMode = FanOverride switch
        {
            FanOverride.Off => L.T("AUS (Taste)"),
            FanOverride.Full => L.T("100 % (Taste)"),
            _ => !Config.Fans.Enabled ? "–" : caseFan is not null ? L.T("Automatik · Gehäuse {0} %", caseFan.Percent) : L.T("Automatik"),
        };
        return new DisplayModel(group is null ? Ds.Title : $"{Ds.Title} · {group}", now, gh, w, gh > 1 ? w / (gh / 1000) : null, online.Count, devices.Count,
            Prices.PriceAt(now.ToUniversalTime()), fanMode, FanOverride != FanOverride.None, IsPaused, miners, alerts, temps)
        {
            Inverted = Ds.Inverted,
            PriceCent = Currencies.Of(Config).Cent,
        };
    }
}
