using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Zustand eines Lüfterkanals für Oberfläche und API.</summary>
public sealed record FanChannelStatus(int Channel, string Name, string Role, string? MinerHost, string Mode, int Percent, int? Rpm, string Reason, bool Stalled);

/// <summary>Zustand eines Temperaturfühlers. Temp null = gerade nicht gemeldet (Kabel, Fühler defekt, Pico getrennt).</summary>
public sealed record TempSensorStatus(string Id, string Name, double? Temp, double WarnTemp, bool Hot, bool CaseFans, bool ShowOnDisplay);

public sealed record FanStatus(bool Enabled, bool Connected, string? Device, string? Error, DateTime? Updated, IReadOnlyList<FanChannelStatus> Channels,
    FanOverride Override = FanOverride.None, IReadOnlyList<TempSensorStatus>? Sensors = null)
{
    /// <summary>Höchster Wert der Fühler für die Gehäuselüfter, null ohne (vollständige) Messung.</summary>
    public double? CaseTemp { get; init; }
}

/// <summary>
/// Zusatzlüfter (Raspberry Pi Pico per USB): alle 2 Sekunden Sollwerte berechnen und senden, Drehzahlen lesen.
/// Der Pico schaltet selbst auf 100 %, wenn 5 Sekunden lang nichts kommt (Server hängt, pausiert, Kabel ab).
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan FanInterval = TimeSpan.FromSeconds(2);
    private readonly FanController _fanController = new();
    private IFanDevice? _fanDevice;
    private DateTime _fanNextConnect = DateTime.MinValue;
    private string? _fanError;
    private int[]? _fanRpm;
    private readonly Dictionary<int, (int Count, DateTime Since)> _fanStall = new();
    private bool _fanBusy, _fanForceInstall;
    /// <summary>Letzter Messwert je Fühler-Id.</summary>
    private readonly Dictionary<string, (double Temp, DateTime Seen)> _sensorSeen = new();
    private DateTime? _fanConnectedSince;
    /// <summary>Ein Fühler gilt als vorhanden, solange sein letzter Wert höchstens so alt ist.</summary>
    public static readonly TimeSpan SensorStale = TimeSpan.FromSeconds(30);

    public FanStatus FanStatus { get; private set; } = new(false, false, null, null, null, []);

    /// <summary>Neuer Lüfterstand (alle 2 s, im Hub-Kontext).</summary>
    public event Action? FansUpdated;

    private CancellationTokenSource? _fanLoop;

    private void StartFanLoop()
    {
        _fanLoop?.Cancel();
        _fanLoop = new CancellationTokenSource();
        _ = RunLoopAsync(() => FanInterval, FanTickAsync, _fanLoop.Token);
    }

    /// <summary>Nach Änderung der Lüfter-Einstellungen: Port neu wählen, sofort anwenden.</summary>
    public async Task ApplyFanSettingsAsync(bool reinstallFirmware = false)
    {
        CloseFanDevice();
        _fanNextConnect = DateTime.MinValue;
        _fanForceInstall = reinstallFirmware;
        await FanTickAsync();
    }

    private void CloseFanDevice()
    {
        _fanDevice?.Dispose();
        _fanDevice = null;
        _fanRpm = null;
        _fanConnectedSince = null;
    }

    internal async Task FanTickAsync()
    {
        if (_fanBusy) return;
        _fanBusy = true;
        try
        {
            var settings = Config.Fans;
            var now = Options.Clock?.Invoke() ?? DateTime.Now;
            // Der Pico wird gebraucht für Lüfter und/oder Anzeige und Taster
            if (!settings.Enabled && !Config.Display.Enabled)
            {
                if (_fanDevice is not null) CloseFanDevice();
                FanStatus = new FanStatus(false, false, null, null, null, []);
                return;
            }

            if (_fanDevice is null && now >= _fanNextConnect)
            {
                try
                {
                    var force = _fanForceInstall;
                    _fanForceInstall = false;
                    _fanDevice = await Task.Run(() => OpenFanDevice(settings.Port, force));
                    _fanError = null;
                    _fanConnectedSince = now;
                }
                catch (Exception ex)
                {
                    _fanError = ex.Message;
                    _fanNextConnect = now.AddSeconds(10);
                }
            }

            var miners = Devices.Select(d => new MinerTemps(d.Host, d.Title, d.State.Online,
                d.State.Online ? d.State.Info?.vrTemp : null, d.State.Online ? d.State.Info?.temp : null, d.State.LastOk)).ToList();
            _fanController.Override = FanOverride;
            var sensors = SensorStatus(settings, now);
            _fanController.CaseTemperature = CaseTemperature(sensors);
            var targets = _fanController.Compute(settings, miners, now);

            if (_fanDevice is not null)
            {
                try
                {
                    // Ohne Lüftersteuerung nur Lebenszeichen/Tasten abfragen – der Pico lässt die Lüfter dann auf 100 %
                    _fanRpm = settings.Enabled
                        ? await _fanDevice.ExchangeAsync(targets.Select(t => t.Percent).ToList())
                        : await _fanDevice.PollAsync();
                    RecordSensors(_fanDevice.Temperatures, now);
                    await HandlePicoEventsAsync(_fanDevice.DrainEvents());
                }
                catch (Exception ex)
                {
                    _fanError = L.T("Verbindung zum Pico verloren: ") + ex.Message;
                    RaiseStatus(false, L.T("Lüfter: ") + _fanError);
                    CloseFanDevice();
                    _fanNextConnect = now.AddSeconds(5);
                }
            }

            var channels = targets.Select(t =>
            {
                var c = settings.Channel(t.Channel);
                int? rpm = _fanRpm is { } r && r.Length >= t.Channel ? r[t.Channel - 1] : null;
                var stalled = CheckStall(c, t, rpm, now);
                return new FanChannelStatus(t.Channel, c.Name.Length > 0 ? c.Name : DefaultFanName(c), c.Role, c.MinerHost,
                    c.Role == "case" ? settings.Case.Mode : c.Mode, t.Percent, rpm, t.Reason, stalled);
            }).ToList();
            if (!settings.Enabled) channels.Clear();
            LogFanChanges(channels);
            sensors = SensorStatus(settings, now);
            FanStatus = new FanStatus(settings.Enabled, _fanDevice is not null, _fanDevice?.Description, _fanError, now, channels, FanOverride, sensors)
            {
                CaseTemp = CaseTemperature(sensors),
            };
            CheckSensorAlerts(sensors, now);
            FansUpdated?.Invoke();
            ReportSafetyOverrides(targets);
            await DisplayTickAsync(now);
        }
        finally
        {
            _fanBusy = false;
        }
    }

    private readonly Dictionary<int, (int Percent, bool Stalled)> _fanLogged = new();

    /// <summary>
    /// Protokoll der Lüftersteuerung (VR je Miner und Gehäuse): nur deutliche Änderungen (±15 %) und „steht/läuft wieder“,
    /// sonst würde jede Regelstufe eine Zeile erzeugen.
    /// </summary>
    private void LogFanChanges(IReadOnlyList<FanChannelStatus> channels)
    {
        foreach (var c in channels)
        {
            if (c.Role is not ("miner" or "case")) continue;   // unbelegte Kanäle nicht protokollieren
            var known = _fanLogged.TryGetValue(c.Channel, out var last);
            if (known && Math.Abs(last.Percent - c.Percent) < 15 && last.Stalled == c.Stalled) continue;
            _fanLogged[c.Channel] = (c.Percent, c.Stalled);
            var text = c.Stalled ? L.T("Lüfter K{0} ({1}) steht!", c.Channel, c.Name)
                : known && last.Stalled ? L.T("Lüfter K{0} ({1}) läuft wieder: {2} %", c.Channel, c.Name, c.Percent)
                : L.T("Lüfter K{0} ({1}): {2} % – {3}", c.Channel, c.Name, c.Percent, c.Reason);
            LogEvent(c.Role == "miner" ? c.MinerHost : null, EventCategories.Fans, text);
        }
    }

    /// <summary>Messwerte übernehmen; unbekannte Fühler (feste 1-Wire-Kennung) in die Einstellungen eintragen.</summary>
    private void RecordSensors(IReadOnlyList<TempReading> readings, DateTime now)
    {
        var added = false;
        foreach (var r in readings)
        {
            _sensorSeen[r.Id] = (r.Celsius, now);
            // "#1" usw. (alte Pico-Firmware) nicht speichern: die Nummer hängt nur von der Reihenfolge ab
            if (r.Id.StartsWith('#') || Config.Fans.Sensors.Any(s => s.Id == r.Id)) continue;
            Config.Fans.Sensors.Add(new TempSensorSettings
            {
                Id = r.Id,
                Name = L.T("Fühler {0}", Config.Fans.Sensors.Count + 1),
                WarnTemp = Config.Fans.CaseTempWarn,
            });
            added = true;
        }
        if (added)
        {
            Config.Save();
            RaiseStatus(true, L.T("Neuer Temperaturfühler erkannt – Name und Warnschwelle unter „Lüfter & Anzeige“ festlegen."));
        }
    }

    /// <summary>Eingetragene Fühler plus vorübergehende ("#n") mit aktuellem Wert.</summary>
    private List<TempSensorStatus> SensorStatus(FanSettings settings, DateTime now)
    {
        double? Current(string id) => _fanDevice is not null && _sensorSeen.TryGetValue(id, out var v) && now - v.Seen <= SensorStale ? v.Temp : null;
        var list = settings.Sensors.Select(s =>
        {
            var t = Current(s.Id);
            return new TempSensorStatus(s.Id, s.Name.Length > 0 ? s.Name : s.Id, t, s.WarnTemp, t >= s.WarnTemp, s.CaseFans, s.ShowOnDisplay);
        }).ToList();
        var n = 0;
        foreach (var (id, _) in _sensorSeen.Where(x => x.Key.StartsWith('#')).OrderBy(x => x.Key))
        {
            n++;
            if (Current(id) is not { } t) continue;
            list.Add(new TempSensorStatus(id, L.T("Fühler {0}", n), t, settings.CaseTempWarn, t >= settings.CaseTempWarn, true, true));
        }
        return list;
    }

    /// <summary>
    /// Höchster Wert der Fühler für die Gehäuselüfter. Fehlt einer davon, ist der Wert unbekannt (null) –
    /// die Gehäuselüfter laufen dann mit dem Wert für „unbekannt“ (Standard 100 %).
    /// </summary>
    private static double? CaseTemperature(IReadOnlyList<TempSensorStatus> sensors)
    {
        var used = sensors.Where(s => s.CaseFans).ToList();
        if (used.Count == 0 || used.Any(s => s.Temp is null)) return null;
        return used.Max(s => s.Temp!.Value);
    }

    private void CheckSensorAlerts(IReadOnlyList<TempSensorStatus> sensors, DateTime now)
    {
        if (!Config.Notifications.Wants(NotifyCategory.Overheat)) return;
        var de = L.Culture;
        foreach (var s in sensors)
        {
            if (s.Hot)
                SendAlert(new Alert($"temp-hot:{s.Id}", L.T("{0} zu warm", s.Name), L.T("{0}: {1} °C (Grenze {2} °C).", s.Name, s.Temp!.Value.ToString("0.0", de), s.WarnTemp.ToString("0.#", de)),
                    NotifyPriority.High, TimeSpan.FromMinutes(30), NotifyCategory.Overheat));
            // Fühler fehlt, obwohl der Pico seit 2 Minuten verbunden ist
            else if (s.Temp is null && !s.Id.StartsWith('#') && _fanConnectedSince is { } since && now - since >= TimeSpan.FromMinutes(2)
                     && (!_sensorSeen.TryGetValue(s.Id, out var seen) || now - seen.Seen >= TimeSpan.FromMinutes(2)))
                SendAlert(new Alert($"temp-missing:{s.Id}", L.T("Temperaturfühler {0} fehlt", s.Name),
                    L.T("{0} meldet keinen Wert. Kabel prüfen – oder den Fühler unter „Lüfter & Anzeige“ entfernen.", s.Name) +
                    (s.CaseFans ? L.T(" Die Gehäuselüfter laufen bis dahin mit dem Wert für „unbekannt“.") : ""),
                    NotifyPriority.High, TimeSpan.FromHours(6), NotifyCategory.Overheat));
        }
    }

    private IFanDevice OpenFanDevice(string port, bool forceInstall)
    {
        if (Options.FanDeviceFactory is { } factory) return factory(port);
        if (port == "sim") return new SimulatedFanDevice(); // Vorführung/Test ohne Hardware
        var candidates = port is { Length: > 0 } p && p != "auto" ? [p] : PicoFanDevice.FindPorts();
        if (candidates.Count == 0) throw new IOException(L.T("Kein Pico gefunden (USB-Kabel? Datenkabel statt Ladekabel?)."));
        Exception? last = null;
        foreach (var name in candidates)
        {
            ILineTransport? io = null;
            try
            {
                io = new SerialLineTransport(name);
                return PicoFanDevice.Connect(io, name, msg => RaiseStatus(true, L.T("Lüfter: ") + msg), forceInstall);
            }
            catch (Exception ex)
            {
                io?.Dispose();
                last = ex;
            }
        }
        throw new IOException(last is UnauthorizedAccessException
            ? L.T("Kein Zugriff auf den seriellen Port (Linux: Benutzer in Gruppe „dialout“).")
            : last?.Message ?? L.T("Pico nicht erreichbar."));
    }

    /// <summary>„Lüfter steht“: Soll ≥ 20 %, Drehzahlsignal vorhanden, aber 0 U/min in 3 Messungen nach 10 s Anlaufzeit.</summary>
    private bool CheckStall(FanChannelSettings c, FanTarget t, int? rpm, DateTime now)
    {
        if (c.Role == "none" || !c.HasTach || rpm is null || t.Percent < 20 || rpm > 0)
        {
            _fanStall.Remove(t.Channel);
            return false;
        }
        var (count, since) = _fanStall.TryGetValue(t.Channel, out var s) ? s : (0, now);
        _fanStall[t.Channel] = (count + 1, since);
        var stalled = count + 1 >= 3 && now - since >= TimeSpan.FromSeconds(10);
        if (stalled && Config.Notifications.Wants(NotifyCategory.Overheat))
        {
            var name = c.Name.Length > 0 ? c.Name : DefaultFanName(c);
            SendAlert(new Alert($"fanstall:{t.Channel}", L.T("Lüfter K{0} steht", t.Channel), L.T("{0}: Soll {1} %, aber keine Drehzahl. Kabel und Lüfter prüfen.", name, t.Percent),
                NotifyPriority.High, TimeSpan.FromHours(6), NotifyCategory.Overheat));
        }
        return stalled;
    }

    private string DefaultFanName(FanChannelSettings c) => c.Role switch
    {
        "miner" => L.T("VR ") + (Device(c.MinerHost ?? "")?.Title ?? c.MinerHost ?? "?"),
        "case" => L.T("Gehäuse"),
        _ => $"K{c.Channel}",
    };

    /// <summary>Änderungen an Lüftereinstellungen im Protokoll des zugeordneten Miners vermerken.</summary>
    public void LogFanChanges(FanSettings before, FanSettings after)
    {
        for (var ch = 1; ch <= FanSettings.ChannelCount; ch++)
        {
            var a = before.Channel(ch);
            var b = after.Channel(ch);
            if (b.Role != "miner" || Device(b.MinerHost ?? "") is not { } device) continue;
            if (a.Role != b.Role || a.MinerHost != b.MinerHost) device.AddLog(L.T("Lüfter K{0} zugeordnet ({1}).", ch, (b.Mode == "manual" ? L.T("manuell {0} %", b.ManualPercent) : L.T("Automatik"))), EventCategories.Fans);
            else if (a.Mode != b.Mode) device.AddLog(L.T("Lüfter K{0}: {1} → {2}", ch, (a.Mode == "manual" ? L.T("Manuell") : L.T("Automatik")), (b.Mode == "manual" ? L.T("Manuell {0} %", b.ManualPercent) : L.T("Automatik"))), EventCategories.Fans);
            else if (b.Mode == "manual" && a.ManualPercent != b.ManualPercent) device.AddLog(L.T("Lüfter K{0}: manuell {1} % → {2} %", ch, a.ManualPercent, b.ManualPercent), EventCategories.Fans);
        }
    }
}
