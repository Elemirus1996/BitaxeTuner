using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>Zustand eines Lüfterkanals für Oberfläche und API.</summary>
public sealed record FanChannelStatus(int Channel, string Name, string Role, string? MinerHost, string Mode, int Percent, int? Rpm, string Reason, bool Stalled);

public sealed record FanStatus(bool Enabled, bool Connected, string? Device, string? Error, DateTime? Updated, IReadOnlyList<FanChannelStatus> Channels,
    FanOverride Override = FanOverride.None, IReadOnlyList<double>? CaseTemps = null)
{
    /// <summary>Höchster Wert der Gehäusefühler, null ohne Sensor.</summary>
    public double? CaseTemp => CaseTemps is { Count: > 0 } t ? t.Max() : null;
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
            _fanController.CaseTemperature = _fanDevice?.Temperatures is { Count: > 0 } ct ? ct.Max() : null;
            var targets = _fanController.Compute(settings, miners, now);

            if (_fanDevice is not null)
            {
                try
                {
                    // Ohne Lüftersteuerung nur Lebenszeichen/Tasten abfragen – der Pico lässt die Lüfter dann auf 100 %
                    _fanRpm = settings.Enabled
                        ? await _fanDevice.ExchangeAsync(targets.Select(t => t.Percent).ToList())
                        : await _fanDevice.PollAsync();
                    await HandlePicoEventsAsync(_fanDevice.DrainEvents());
                }
                catch (Exception ex)
                {
                    _fanError = "Verbindung zum Pico verloren: " + ex.Message;
                    RaiseStatus(false, "Lüfter: " + _fanError);
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
            var caseTemps = _fanDevice?.Temperatures.ToList() ?? [];
            FanStatus = new FanStatus(settings.Enabled, _fanDevice is not null, _fanDevice?.Description, _fanError, now, channels, FanOverride, caseTemps);
            if (FanStatus.CaseTemp is { } hot && hot >= settings.CaseTempWarn && Config.Notifications.OnOverheat)
                SendAlert(new Alert("case-hot", "Gehäuse zu warm", $"Gehäusetemperatur {hot.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} °C (Grenze {settings.CaseTempWarn:0} °C).",
                    NotifyPriority.High, TimeSpan.FromMinutes(30)));
            FansUpdated?.Invoke();
            ReportSafetyOverrides(targets);
            await DisplayTickAsync(now);
        }
        finally
        {
            _fanBusy = false;
        }
    }

    private IFanDevice OpenFanDevice(string port, bool forceInstall)
    {
        if (Options.FanDeviceFactory is { } factory) return factory(port);
        if (port == "sim") return new SimulatedFanDevice(); // Vorführung/Test ohne Hardware
        var candidates = port is { Length: > 0 } p && p != "auto" ? [p] : PicoFanDevice.FindPorts();
        if (candidates.Count == 0) throw new IOException("Kein Pico gefunden (USB-Kabel? Datenkabel statt Ladekabel?).");
        Exception? last = null;
        foreach (var name in candidates)
        {
            ILineTransport? io = null;
            try
            {
                io = new SerialLineTransport(name);
                return PicoFanDevice.Connect(io, name, msg => RaiseStatus(true, "Lüfter: " + msg), forceInstall);
            }
            catch (Exception ex)
            {
                io?.Dispose();
                last = ex;
            }
        }
        throw new IOException(last is UnauthorizedAccessException
            ? "Kein Zugriff auf den seriellen Port (Linux: Benutzer in Gruppe „dialout“)."
            : last?.Message ?? "Pico nicht erreichbar.");
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
        if (stalled && Config.Notifications.OnOverheat)
        {
            var name = c.Name.Length > 0 ? c.Name : DefaultFanName(c);
            SendAlert(new Alert($"fanstall:{t.Channel}", $"Lüfter K{t.Channel} steht", $"{name}: Soll {t.Percent} %, aber keine Drehzahl. Kabel und Lüfter prüfen.",
                NotifyPriority.High, TimeSpan.FromHours(6)));
        }
        return stalled;
    }

    private string DefaultFanName(FanChannelSettings c) => c.Role switch
    {
        "miner" => "VR " + (Device(c.MinerHost ?? "")?.Title ?? c.MinerHost ?? "?"),
        "case" => "Gehäuse",
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
            if (a.Role != b.Role || a.MinerHost != b.MinerHost) device.AddLog($"Lüfter K{ch} zugeordnet ({(b.Mode == "manual" ? $"manuell {b.ManualPercent} %" : "Automatik")}).");
            else if (a.Mode != b.Mode) device.AddLog($"Lüfter K{ch}: {(a.Mode == "manual" ? "Manuell" : "Automatik")} → {(b.Mode == "manual" ? $"Manuell {b.ManualPercent} %" : "Automatik")}");
            else if (b.Mode == "manual" && a.ManualPercent != b.ManualPercent) device.AddLog($"Lüfter K{ch}: manuell {a.ManualPercent} % → {b.ManualPercent} %");
        }
    }
}
