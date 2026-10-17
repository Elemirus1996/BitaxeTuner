using BitaxeTuner.Core.Fans;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.23 Pico-Übersicht: ein Eintrag je benutztem Pico. Slot „fans“ (Lüfter-Pico, ggf. mit Anzeige), „display“ (eigener
/// Display-Pico der Hauptanzeige), „extra-&lt;id&gt;“ (weitere Anzeige). Address = USB-Port bzw. Gerätename (keine Daten nach außen).
/// </summary>
public sealed record PicoInfo(string Slot, string Role, string Name, bool Connected, string Connection, string Address, string? Ip,
    string? Version, bool Outdated, DateTime? Since, string? Error, int? Rssi, long? Uptime, string? Panel, bool Pairing, string? PairCode);

public sealed partial class MinerHub
{
    private readonly Dictionary<string, DateTime> _picoSince = new();
    private readonly Dictionary<string, (PicoStat Stat, DateTime At)> _picoStats = new();
    private readonly Dictionary<string, (PicoPairing Pairing, DateTime Started)> _pairings = new();
    private readonly HashSet<string> _picoForceInstall = [];
    /// <summary>Kopplung muss innerhalb des Zeitfensters am Pico (2 min) bestätigt werden.</summary>
    public static readonly TimeSpan PairTimeout = TimeSpan.FromMinutes(2);

    private sealed record PicoSlot(string Slot, string Role, string Name, string Connection, string Port, string Host, string Ip, string SecretKey,
        string? Panel, Func<IFanDevice?> Device, Func<string?> Error, Action Close, Action<DateTime> NextConnect, Action<string> RememberIp);

    private IEnumerable<PicoSlot> PicoSlots()
    {
        var f = Config.Fans;
        var d = Config.Display;
        if (NeedFanPico)
            yield return new PicoSlot("fans", PicoFanDevice.RoleFans, L.T("Lüfter-Pico"), f.Connection, f.Port, f.NetworkHost, f.NetworkIp, PicoKeyFans,
                d.Enabled && !d.OwnDevice ? d.Panel : null, () => _fanDevice, () => _fanError, CloseFanDevice, t => _fanNextConnect = t, ip => f.NetworkIp = ip);
        if (d.Enabled && d.OwnDevice)
            yield return new PicoSlot("display", PicoFanDevice.RoleDisplay, L.T("Hauptanzeige"), d.Connection, d.Port, d.NetworkHost, d.NetworkIp, PicoKeyDisplay,
                d.Panel, () => _displayDevice, () => _displayDeviceError, CloseDisplayDevice, t => _displayNextConnect = t, ip => d.NetworkIp = ip);
        foreach (var r in ExtraRuntimes().Where(r => r.Config.Settings.Enabled))
        {
            var s = r.Config.Settings;
            yield return new PicoSlot("extra-" + r.Config.Id, PicoFanDevice.RoleDisplay, L.T("Anzeige „{0}“", ExtraName(r.Config)), s.Connection, s.Port,
                s.NetworkHost, s.NetworkIp, ExtraPicoKey(r.Config.Id), s.Panel, () => r.Device, () => r.DeviceError,
                () => { r.Device?.Dispose(); r.Device = null; }, t => r.NextConnect = t, ip => s.NetworkIp = ip);
        }
    }

    private PicoSlot Slot(string slot) =>
        PicoSlots().FirstOrDefault(s => s.Slot == slot) ?? throw new LocalizedException("Pico nicht gefunden.");

    private IFanDevice ConnectedPico(string slot) =>
        Slot(slot).Device() ?? throw new LocalizedException("Der Pico ist gerade nicht verbunden.");

    /// <summary>Alle benutzten Picos; mit <paramref name="refresh"/> wird der Zustand (WLAN-Signal, Laufzeit) neu abgefragt.</summary>
    public async Task<List<PicoInfo>> PicosAsync(bool refresh = false)
    {
        var now = Options.Clock?.Invoke() ?? DateTime.Now;
        ExpirePairings(now);
        var list = new List<PicoInfo>();
        foreach (var s in PicoSlots().ToList())
        {
            var dev = s.Device();
            if (dev is not null && refresh && (!_picoStats.TryGetValue(s.Slot, out var old) || now - old.At > TimeSpan.FromSeconds(15)))
            {
                try { if (await dev.StatAsync() is { } st) _picoStats[s.Slot] = (st, now); }
                catch { /* der nächste Lüfter-Takt merkt eine Trennung */ }
            }
            var stat = dev is not null && _picoStats.TryGetValue(s.Slot, out var cached) ? cached.Stat : null;
            var wlan = s.Connection == "wlan";
            var since = s.Slot == "fans" ? _fanConnectedSince : _picoSince.TryGetValue(s.Slot, out var t) ? t : null;
            list.Add(new PicoInfo(s.Slot, s.Role, s.Name, dev is not null, wlan ? "wlan" : "usb",
                wlan ? s.Host : (s.Port is { Length: > 0 } p && p != "auto" ? p : L.T("automatisch")), wlan && s.Ip.Length > 0 ? s.Ip : null,
                dev?.Version is { Length: > 0 } v ? v : null, dev is not null && dev.Version.Length > 0 && dev.Version != PicoFanDevice.FirmwareVersion,
                dev is not null ? since : null, dev is null ? s.Error() : null, stat?.Rssi, stat?.Uptime, s.Panel,
                _pairings.ContainsKey(s.Slot) || stat?.Pairing == true, _pairings.TryGetValue(s.Slot, out var pr) ? pr.Pairing.Code : null));
        }
        return list;
    }

    /// <summary>„Wo ist welcher?“: LED des Pico 10 Sekunden blinken lassen.</summary>
    public async Task PicoBlinkAsync(string slot, string source)
    {
        await ConnectedPico(slot).BlinkAsync(10);
        LogEvent(null, EventCategories.Fans, L.T("{0}: LED blinkt 10 s ({1}).", Slot(slot).Name, source));
    }

    /// <summary>Pico neu starten; die Verbindung wird nach einigen Sekunden neu aufgebaut.</summary>
    public async Task PicoRestartAsync(string slot, string source)
    {
        var s = Slot(slot);
        await ConnectedPico(slot).ResetAsync();
        s.Close();
        s.NextConnect((Options.Clock?.Invoke() ?? DateTime.Now).AddSeconds(8));
        LogEvent(null, EventCategories.Fans, L.T("{0} wird neu gestartet ({1}).", s.Name, source));
    }

    /// <summary>Programm neu aufspielen: WLAN per PUT/COMMIT, USB beim nächsten Verbinden über das Raw-REPL.</summary>
    public async Task PicoReinstallAsync(string slot, string source)
    {
        var s = Slot(slot);
        var now = Options.Clock?.Invoke() ?? DateTime.Now;
        if (s.Device() is PicoFanDevice { OverNetwork: true } pico)
        {
            await pico.ReinstallOverNetworkAsync();
            s.Close();
            s.NextConnect(now.AddSeconds(8));
        }
        else
        {
            s.Close();
            if (slot == "fans") _fanForceInstall = true;
            else _picoForceInstall.Add(slot);
            s.NextConnect(now);
        }
        LogEvent(null, EventCategories.Fans, L.T("{0}: Programm v{1} wird neu aufgespielt ({2}).", s.Name, PicoFanDevice.FirmwareVersion, source));
        await FanTickAsync();
    }

    /// <summary>
    /// Schlüssel erneuern, solange der alte noch passt. Der neue liegt vorher als „…next“ in secrets.json – geht die Antwort
    /// verloren, meldet sich der Server beim nächsten Verbinden damit an (siehe <see cref="OpenNetworkPico"/>).
    /// </summary>
    public async Task PicoRotateKeyAsync(string slot, string source)
    {
        var s = Slot(slot);
        if (ConnectedPico(slot) is not PicoFanDevice { OverNetwork: true } pico)
            throw new LocalizedException("Den Schlüssel gibt es nur bei Picos im WLAN.");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(key).ToLowerInvariant();
        Secrets.Set(s.SecretKey + ".next", hex);
        await pico.RotateKeyAsync(key);
        Secrets.Set(s.SecretKey, hex);
        Secrets.Set(s.SecretKey + ".next", null);
        LogEvent(null, EventCategories.Fans, L.T("{0}: neuer WLAN-Schlüssel gesetzt ({1}).", s.Name, source));
    }

    /// <summary>Kopplung beginnen (Pico muss im Kopplungsmodus sein); liefert den Code zum Vergleichen.</summary>
    public async Task<string> PicoPairStartAsync(string slot, string source)
    {
        var s = Slot(slot);
        if (s.Connection != "wlan" || s.Host.Length == 0) throw new LocalizedException("Koppeln geht nur bei Picos im WLAN – per USB „Für WLAN einrichten“.");
        PicoPairCancel(slot);
        // während der Kopplung nicht mit dem alten Schlüssel verbinden (Lüfter-Pico: Lüfter laufen solange auf 100 %)
        s.Close();
        s.NextConnect(DateTime.MaxValue);
        try
        {
            var (name, port) = SplitHostPort(s.Host);
            var pairing = await Task.Run(() =>
            {
                try { return PicoPairing.Start(name, port); }
                catch (IOException) when (s.Ip.Length > 0 && s.Ip != name) { return PicoPairing.Start(s.Ip, port); }
            });
            if (pairing.Role != s.Role)
            {
                pairing.Dispose();
                throw new LocalizedException(s.Role == PicoFanDevice.RoleDisplay ? "Dieser Pico ist als Lüfter-Pico eingerichtet, nicht als Display-Pico."
                    : "Dieser Pico ist als Display-Pico eingerichtet, nicht für Lüfter.");
            }
            _pairings[slot] = (pairing, Options.Clock?.Invoke() ?? DateTime.Now);
            LogEvent(null, EventCategories.Fans, L.T("{0}: Kopplung begonnen, Code {1} ({2}).", s.Name, pairing.Code, source));
            return pairing.Code;
        }
        catch
        {
            s.NextConnect(Options.Clock?.Invoke() ?? DateTime.Now);
            throw;
        }
    }

    /// <summary>Code stimmt überein: Pico speichert den neuen Schlüssel, der Server verbindet sich gleich damit.</summary>
    public async Task PicoPairConfirmAsync(string slot, string source)
    {
        var s = Slot(slot);
        if (!_pairings.Remove(slot, out var p)) throw new LocalizedException("Keine Kopplung offen – bitte neu beginnen.");
        try
        {
            await Task.Run(() => p.Pairing.Confirm());
            Secrets.Set(s.SecretKey, Convert.ToHexString(p.Pairing.Key).ToLowerInvariant());
            Secrets.Set(s.SecretKey + ".next", null);
            if (p.Pairing.RemoteIp is { } ip) s.RememberIp(ip);
            Config.Save();
            LogEvent(null, EventCategories.Fans, L.T("{0}: neu gekoppelt, neuer WLAN-Schlüssel gespeichert ({1}).", s.Name, source));
        }
        finally
        {
            p.Pairing.Dispose();
            s.NextConnect((Options.Clock?.Invoke() ?? DateTime.Now).AddSeconds(1));
        }
        await FanTickAsync();
    }

    /// <summary>Kopplung abbrechen (Code passt nicht oder Nutzer bricht ab).</summary>
    public void PicoPairCancel(string slot)
    {
        if (!_pairings.Remove(slot, out var p)) return;
        p.Pairing.Dispose();
        if (PicoSlots().FirstOrDefault(s => s.Slot == slot) is { } s) s.NextConnect(Options.Clock?.Invoke() ?? DateTime.Now);
    }

    private void ExpirePairings(DateTime now)
    {
        foreach (var slot in _pairings.Where(x => now - x.Value.Started > PairTimeout).Select(x => x.Key).ToList())
            PicoPairCancel(slot);
    }

    /// <summary>USB: Programm beim nächsten Verbinden neu aufspielen (Hauptanzeige, weitere Anzeigen).</summary>
    private bool TakeForceInstall(string slot) => _picoForceInstall.Remove(slot);

    /// <summary>8.5-Ereignisse „PAIR …“ ins Protokoll.</summary>
    private void LogPairEvent(string e, string who)
    {
        var text = e switch
        {
            "PAIR ON" => L.T("{0}: Kopplungsmodus an (2 Minuten) – unter „Lüfter & Anzeige → Picos“ koppeln.", who),
            "PAIR OFF" => L.T("{0}: Kopplungsmodus beendet.", who),
            "PAIR DONE" => L.T("{0}: Kopplung abgeschlossen.", who),
            "PAIR NOWLAN" => L.T("{0}: Kopplung nur bei Picos mit WLAN.", who),
            _ => null,
        };
        if (text is not null) LogEvent(null, EventCategories.Fans, text);
    }
}
