using System.IO.Ports;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Fans;

/// <summary>Messwert eines Temperaturfühlers (DS18B20): Id = 1-Wire-Kennung (16 Hex-Zeichen), °C.</summary>
public sealed record TempReading(string Id, double Celsius);

/// <summary>Pico-Zubehör: Lüfter, E-Paper-Anzeige, Taster.</summary>
public interface IFanDevice : IDisposable
{
    string Description { get; }
    /// <summary>„fans“ (Lüfterplatine/Steckbrett) oder „display“ (Pico auf dem E-Paper aufgesteckt, keine Lüfter).</summary>
    string Role => PicoFanDevice.RoleFans;
    /// <summary>Prozentwerte für alle Kanäle setzen (setzt auch den Watchdog im Pico zurück); liefert U/min je Kanal.</summary>
    Task<int[]> ExchangeAsync(IReadOnlyList<int> percent, CancellationToken ct = default);
    /// <summary>Nur Drehzahlen lesen (Lebenszeichen, wenn keine Lüfter geregelt werden).</summary>
    Task<int[]> PollAsync(CancellationToken ct = default);
    /// <summary>Bild übertragen und anzeigen: 2 × 48 000 Byte (Schwarz-Ebene 1 = weiß, Rot-Ebene 1 = rot).</summary>
    Task ShowImageAsync(byte[] planes, CancellationToken ct = default);
    /// <summary>Pico neu starten.</summary>
    Task ResetAsync(CancellationToken ct = default);
    /// <summary>Seit dem letzten Aufruf gemeldete Ereignisse („BTN 1“, „BTN 4 LONG“, „EPD DONE“).</summary>
    IReadOnlyList<string> DrainEvents();
    /// <summary>Temperaturfühler (DS18B20) aus der letzten Antwort; leer ohne Fühler.</summary>
    IReadOnlyList<TempReading> Temperatures { get; }
}

/// <summary>Zeilenbasierte serielle Verbindung (austauschbar für Tests).</summary>
public interface ILineTransport : IDisposable
{
    void Write(string text);
    /// <summary>Nächste Zeile ohne Zeilenende; null bei Zeitüberschreitung.</summary>
    string? ReadLine(TimeSpan timeout);
    /// <summary>Liest, bis <paramref name="marker"/> empfangen wurde; liefert alles bis dahin oder null bei Zeitüberschreitung.</summary>
    string? ReadUntil(string marker, TimeSpan timeout);
    void Discard();
}

public sealed class SerialLineTransport : ILineTransport
{
    private readonly SerialPort _port;
    private readonly StringBuilder _pending = new();

    public SerialLineTransport(string portName)
    {
        _port = new SerialPort(portName, 115200) { DtrEnable = true, RtsEnable = true, ReadTimeout = 100, WriteTimeout = 2000, Encoding = Encoding.ASCII };
        _port.Open();
    }

    public void Write(string text) => _port.Write(text);

    public string? ReadLine(TimeSpan timeout)
    {
        var raw = ReadUntil("\n", timeout);
        return raw?.TrimEnd('\r', '\n');
    }

    public string? ReadUntil(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var text = _pending.ToString();
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                _pending.Remove(0, idx + marker.Length);
                return text[..(idx + marker.Length)];
            }
            if (DateTime.UtcNow > deadline) return null;
            try
            {
                var buffer = new byte[256];
                var n = _port.Read(buffer, 0, buffer.Length);
                _pending.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            catch (TimeoutException) { /* weiter warten */ }
        }
    }

    public void Discard()
    {
        _pending.Clear();
        _port.DiscardInBuffer();
    }

    public void Dispose() => _port.Dispose();
}

/// <summary>Pico wurde über WLAN auf die aktuelle Programmversion gebracht und startet neu – gleich erneut verbinden.</summary>
public sealed class PicoUpdatedException(string message) : IOException(message);

/// <summary>
/// Raspberry Pi Pico mit dem BitaxeTuner-Lüfterprogramm (btfan.py). Läuft auf dem Pico nur MicroPython ohne
/// unser Programm (oder eine ältere Version), wird es über das Raw-REPL als main.py aufgespielt (USB) bzw. per
/// PUT/COMMIT aktualisiert (WLAN, ab Programm v7).
/// </summary>
public sealed class PicoFanDevice : IFanDevice
{
    public const string FirmwareVersion = "8";
    public const string RoleFans = "fans";
    public const string RoleDisplay = "display";
    public const int ImageBytes = 2 * 800 * 480 / 8;
    private readonly ILineTransport _io;
    private readonly object _lock = new();
    private readonly List<string> _events = new();

    public IReadOnlyList<TempReading> Temperatures { get; private set; } = [];

    private PicoFanDevice(ILineTransport io, string description, string role)
    {
        _io = io;
        Description = description;
        Role = role;
    }

    public string Description { get; }
    public string Role { get; }

    public static string Firmware
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("BitaxeTuner.Core.Fans.btfan.py")
                          ?? throw new InvalidOperationException(L.T("Lüfterprogramm fehlt in der Programmdatei."));
            using var r = new StreamReader(s);
            return r.ReadToEnd().Replace("\r\n", "\n");
        }
    }

    /// <summary>Verbinden (USB), Programm prüfen und bei Bedarf aufspielen.</summary>
    public static PicoFanDevice Connect(ILineTransport io, string portName, Action<string>? log = null, bool forceInstall = false)
    {
        var (version, role) = forceInstall ? (null, RoleFans) : HelloInfo(io);
        if (version != FirmwareVersion)
        {
            log?.Invoke(version is null
                ? L.T("Pico an {0}: Lüfterprogramm fehlt – wird aufgespielt …", portName)
                : L.T("Pico an {0}: Lüfterprogramm v{1} → v{2} wird aufgespielt …", portName, version, FirmwareVersion));
            Install(io, Firmware);
            (version, role) = HelloInfo(io, TimeSpan.FromSeconds(4));
            if (version is null)
                throw new IOException(L.T("Pico antwortet nach dem Aufspielen nicht. Ist MicroPython installiert (UF2 von micropython.org)?"));
            log?.Invoke(L.T("Pico an {0}: Lüfterprogramm v{1} läuft.", portName, version));
        }
        return new PicoFanDevice(io, role == RoleDisplay ? L.T("Display-Pico an {0} (Programm v{1})", portName, version)
            : L.T("Pico an {0} (Programm v{1})", portName, version), role);
    }

    /// <summary>
    /// Verbinden über WLAN (Anmeldung schon erfolgt). Ältere Programmversion → per PUT/COMMIT aktualisieren, Pico neu
    /// starten und <see cref="PicoUpdatedException"/> werfen (der Aufrufer verbindet einige Sekunden später neu).
    /// </summary>
    public static PicoFanDevice ConnectNetwork(NetworkLineTransport io, Action<string>? log = null)
    {
        if (io.Version != FirmwareVersion)
        {
            log?.Invoke(L.T("Pico {0}: Lüfterprogramm v{1} → v{2} wird über WLAN aktualisiert …", io.Endpoint, io.Version, FirmwareVersion));
            UpdateOverNetwork(io, Firmware);
            io.Dispose();
            throw new PicoUpdatedException(L.T("Pico {0} aktualisiert (v{1}), startet neu.", io.Endpoint, FirmwareVersion));
        }
        var role = io.Role == RoleDisplay ? RoleDisplay : RoleFans;
        return new PicoFanDevice(io, role == RoleDisplay ? L.T("Display-Pico {0} über WLAN (Programm v{1})", io.Endpoint, io.Version)
            : L.T("Pico {0} über WLAN (Programm v{1})", io.Endpoint, io.Version), role);
    }

    /// <summary>Neues Programm über eine angemeldete Verbindung: PUT, F-Zeilen, COMMIT (prüft Größe und SHA-256), RESET.</summary>
    internal static void UpdateOverNetwork(ILineTransport io, string source)
    {
        var bytes = Encoding.ASCII.GetBytes(source);
        io.Write($"PUT main.py {bytes.Length} {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\r\n");
        ExpectLine(io, "OK PUT", TimeSpan.FromSeconds(3));
        var sb = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 192)
        {
            sb.Append("F ").Append(Convert.ToBase64String(bytes, i, Math.Min(192, bytes.Length - i))).Append("\r\n");
            if (sb.Length > 8000) { io.Write(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) io.Write(sb.ToString());
        io.Write("COMMIT\r\n");
        ExpectLine(io, "OK COMMIT", TimeSpan.FromSeconds(10));
        io.Write("RESET\r\n");
        try { ExpectLine(io, "OK RESET", TimeSpan.FromSeconds(2)); } catch (IOException) { /* startet ohnehin neu */ }
    }

    private static void ExpectLine(ILineTransport io, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = io.ReadLine(deadline - DateTime.UtcNow)?.Trim();
            if (line is null) break;
            if (line == expected) return;
            if (line.StartsWith("ERR", StringComparison.Ordinal)) throw new IOException("Pico: " + line[3..].Trim());
        }
        throw new IOException(L.T("Pico antwortet nicht."));
    }

    /// <summary>
    /// Pico per USB für WLAN einrichten: Programm und btcfg.json (Rolle, WLAN, Schlüssel, Gerätename) über das Raw-REPL
    /// schreiben, neu starten, Rolle prüfen. Danach kann der Pico an ein eigenes Netzteil.
    /// </summary>
    public static void Provision(ILineTransport io, string portName, PicoNetworkConfig config, Action<string>? log = null)
    {
        log?.Invoke(L.T("Pico an {0}: Programm und WLAN-Einstellungen werden aufgespielt …", portName));
        Install(io, Firmware, new Dictionary<string, string> { ["btcfg.json"] = config.ToJson() });
        var (version, role) = HelloInfo(io, TimeSpan.FromSeconds(4));
        if (version != FirmwareVersion || role != config.Role)
            throw new IOException(L.T("Pico antwortet nach dem Einrichten nicht wie erwartet (Version {0}, Rolle {1}).", version ?? "?", role));
        log?.Invoke(L.T("Pico an {0}: eingerichtet als „{1}“ für WLAN „{2}“.", portName, config.Role, config.Ssid));
    }

    /// <summary>Nach dem Einrichten: bis zu <paramref name="timeout"/> warten, bis der Pico im WLAN ist; liefert seine IP oder null.</summary>
    public static string? WaitForNetwork(ILineTransport io, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            io.Discard();
            io.Write("NET\r\n");
            var lineDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
            while (DateTime.UtcNow < lineDeadline)
            {
                var parts = io.ReadLine(lineDeadline - DateTime.UtcNow)?.Trim().Split(' ');
                if (parts is null) break;
                if (parts.Length == 4 && parts[0] == "OK" && parts[1] == "NET")
                {
                    if (parts[2] == "1" && System.Net.IPAddress.TryParse(parts[3], out _)) return parts[3];
                    break;
                }
            }
            Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>"HELLO" → "OK BTFAN &lt;version&gt; &lt;kanäle&gt; [rolle]"; null, wenn unser Programm nicht läuft.</summary>
    internal static string? Hello(ILineTransport io, TimeSpan? timeout = null) => HelloInfo(io, timeout).Version;

    internal static (string? Version, string Role) HelloInfo(ILineTransport io, TimeSpan? timeout = null)
    {
        io.Discard();
        io.Write("\r\nHELLO\r\n");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1.5));
        while (DateTime.UtcNow < deadline)
        {
            var line = io.ReadLine(deadline - DateTime.UtcNow);
            if (line is null) return (null, RoleFans);
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0] == "OK" && parts[1] == "BTFAN")
                return (parts[2], parts.Length >= 5 && parts[4] == RoleDisplay ? RoleDisplay : RoleFans);
        }
        return (null, RoleFans);
    }

    /// <summary>
    /// Programm als main.py (und weitere Dateien, z. B. btcfg.json) über das MicroPython-Raw-REPL schreiben, dann
    /// Soft-Reset (startet main.py).
    /// </summary>
    internal static void Install(ILineTransport io, string source, IReadOnlyDictionary<string, string>? files = null)
    {
        io.Write("\r\x03\x03"); // laufendes Programm unterbrechen (Lüfter gehen dabei auf 100 %)
        Thread.Sleep(300);
        io.Discard();
        io.Write("\r\x01"); // Raw-REPL
        if (io.ReadUntil("raw REPL; CTRL-B to exit\r\n>", TimeSpan.FromSeconds(3)) is null)
            throw new IOException(L.T("Kein MicroPython auf dem Pico gefunden. Bitte zuerst MicroPython installieren (BOOTSEL + UF2)."));

        void Exec(string code)
        {
            io.Write(code + "\x04");
            var reply = io.ReadUntil("\x04>", TimeSpan.FromSeconds(5)) ?? throw new IOException(L.T("Pico antwortet beim Aufspielen nicht."));
            // Antwort: "OK" <Ausgabe> \x04 <Fehler> \x04>
            var parts = reply.Split('\x04');
            if (!reply.StartsWith("OK", StringComparison.Ordinal) || parts.Length < 2 || parts[1].Trim().Length > 0)
                throw new IOException(L.T("Fehler beim Aufspielen: ") + (parts.Length > 1 ? parts[1].Trim() : reply));
        }

        // Ab Programm v8 läuft der Hardware-Watchdog des Pico (8 s) und lässt sich nicht anhalten – auch nicht im
        // Raw-REPL. Deshalb bei jedem Schritt füttern, sonst startet der Pico mitten im Schreiben neu. Auf einem Pico
        // ohne laufenden Watchdog startet ihn diese Zeile; das neue Programm füttert ihn nach dem Soft-Reset weiter.
        Exec("import machine;w=machine.WDT(timeout=8000)");

        void WriteFile(string name, string content)
        {
            Exec($"w.feed();f=open('{name}','w')");
            for (var i = 0; i < content.Length; i += 192)
                Exec("w.feed();f.write(" + PyString(content.Substring(i, Math.Min(192, content.Length - i))) + ")");
            Exec("w.feed();f.close()");
        }

        foreach (var (name, content) in files ?? new Dictionary<string, string>()) WriteFile(name, content);
        WriteFile("main.py", source);
        io.Write("\x02"); // Raw-REPL verlassen
        Thread.Sleep(100);
        io.Write("\x04"); // Soft-Reset → main.py startet
        Thread.Sleep(1500);
        io.Discard();
    }

    /// <summary>Python-Stringliteral (nur ASCII, Sonderzeichen escaped).</summary>
    internal static string PyString(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 32 || c > 126 => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('\'').ToString();
    }

    public Task<int[]> ExchangeAsync(IReadOnlyList<int> percent, CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_lock)
        {
            _io.Write("SET " + string.Join(' ', percent.Select(p => Math.Clamp(p, 0, 100))) + "\r\n");
            return ParseRpm(Expect("RPM", TimeSpan.FromSeconds(1.5)));
        }
    }, ct);

    public Task<int[]> PollAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_lock)
        {
            _io.Write("GET\r\n");
            return ParseRpm(Expect("RPM", TimeSpan.FromSeconds(1.5)));
        }
    }, ct);

    public Task ShowImageAsync(byte[] planes, CancellationToken ct = default) => Task.Run(() =>
    {
        if (planes.Length != ImageBytes) throw new ArgumentException(L.T("Bildgröße passt nicht zur Anzeige."));
        lock (_lock)
        {
            _io.Write($"IMG {planes.Length}\r\n");
            Expect("OK IMG", TimeSpan.FromSeconds(2));
            // 192 Byte je Zeile (256 Zeichen Base64); jede Zeile setzt auch den Watchdog im Pico zurück
            var sb = new StringBuilder();
            for (var i = 0; i < planes.Length; i += 192)
            {
                sb.Append("D ").Append(Convert.ToBase64String(planes, i, Math.Min(192, planes.Length - i))).Append("\r\n");
                if (sb.Length > 8000)
                {
                    _io.Write(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0) _io.Write(sb.ToString());
            _io.Write("SHOW\r\n");
            Expect("OK SHOW", TimeSpan.FromSeconds(20));
        }
    }, ct);

    public Task ResetAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_lock)
        {
            _io.Write("RESET\r\n");
            try { Expect("OK RESET", TimeSpan.FromSeconds(1)); } catch (IOException) { /* startet ohnehin neu */ }
        }
    }, ct);

    public IReadOnlyList<string> DrainEvents()
    {
        lock (_events)
        {
            var list = _events.ToList();
            _events.Clear();
            return list;
        }
    }

    /// <summary>Zeilen lesen, bis eine mit <paramref name="prefix"/> kommt; Ereignisse unterwegs einsammeln.</summary>
    private string Expect(string prefix, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = _io.ReadLine(deadline - DateTime.UtcNow);
            if (line is null) break;
            line = line.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
            if (line.StartsWith("BTN ", StringComparison.Ordinal) || line == "EPD DONE")
            {
                lock (_events) _events.Add(line);
                continue;
            }
            if (line.StartsWith("ERR", StringComparison.Ordinal)) throw new IOException("Pico: " + line[3..].Trim());
        }
        throw new IOException(L.T("Pico antwortet nicht."));
    }

    /// <summary>"RPM r1 .. r6 [T id=t id=t ..]" – Drehzahlen und optional Temperaturfühler; Display-Pico: "RPM [T …]".</summary>
    private int[] ParseRpm(string line)
    {
        var rest = " " + line[3..].Trim();
        var parts = rest.StartsWith(" T ", StringComparison.Ordinal) ? ["", rest[3..]] : rest.Split(" T ", 2);
        Temperatures = parts.Length > 1 ? ParseTemperatures(parts[1]) : [];
        return parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => int.TryParse(v, out var r) ? r : 0).ToArray();
    }

    /// <summary>"id=t id=t" (ab Firmware 4) oder "t1 t2" (Firmware 3, Ids dann "#1", "#2" nach Reihenfolge).</summary>
    public static List<TempReading> ParseTemperatures(string text)
    {
        var result = new List<TempReading>();
        var items = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < items.Length; i++)
        {
            var eq = items[i].IndexOf('=');
            var id = eq > 0 ? items[i][..eq].ToLowerInvariant() : $"#{i + 1}";
            var value = eq > 0 ? items[i][(eq + 1)..] : items[i];
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)
                && t is > -40 and < 120)
                result.Add(new TempReading(id, t));
        }
        return result;
    }

    public void Dispose() => _io.Dispose();

    // ---------- Pico finden ----------

    /// <summary>
    /// Serielle Ports mit Raspberry-Pi-USB-Kennung (VID 2E8A). Linux: /sys/class/tty, Windows: Registry.
    /// Nur diese Ports werden automatisch angesprochen – andere Geräte (z. B. 3D-Drucker) bleiben unberührt.
    /// </summary>
    public static List<string> FindPorts() => FindPorts("/sys/class/tty");

    /// <param name="ttyClass">Linux: /sys/class/tty (Tests: nachgebaute Ordnerstruktur).</param>
    internal static List<string> FindPorts(string ttyClass)
    {
        var result = new List<string>();
        try
        {
            if (OperatingSystem.IsLinux() && Directory.Exists(ttyClass))
            {
                foreach (var dir in Directory.GetDirectories(ttyClass, "ttyACM*"))
                    if (UsbVendorOf(dir)?.Equals("2e8a", StringComparison.OrdinalIgnoreCase) == true)
                        result.Add("/dev/" + Path.GetFileName(dir));
            }
            else if (OperatingSystem.IsWindows())
            {
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                foreach (var name in usb?.GetSubKeyNames().Where(n => n.StartsWith("VID_2E8A", StringComparison.OrdinalIgnoreCase)) ?? [])
                {
                    using var dev = usb!.OpenSubKey(name);
                    foreach (var inst in dev?.GetSubKeyNames() ?? [])
                    {
                        using var p = dev!.OpenSubKey(inst + @"\Device Parameters");
                        if (p?.GetValue("PortName") is string port && SerialPort.GetPortNames().Contains(port)) result.Add(port);
                    }
                }
            }
        }
        catch { /* ohne Rechte oder ungewöhnliches System: dann nur fester Port */ }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// USB-Herstellerkennung eines tty-Geräts. /sys/class/tty/ttyACM0 ist selbst ein Link nach
    /// /sys/devices/…/&lt;usb-gerät&gt;/&lt;schnittstelle&gt;/tty/ttyACM0 – erst diesen echten Pfad auflösen, dann nach oben bis
    /// zum Ordner mit „idVendor“. (Der relative Link „device“ darin lässt sich vom Link-Pfad aus nicht richtig auflösen.)
    /// </summary>
    internal static string? UsbVendorOf(string ttyDir)
    {
        var info = new DirectoryInfo(ttyDir);
        var real = info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo : info;
        for (var d = real; d is not null; d = d.Parent)
        {
            var vendor = Path.Combine(d.FullName, "idVendor");
            if (File.Exists(vendor)) return File.ReadAllText(vendor).Trim();
        }
        return null;
    }
}

/// <summary>Simulierter Pico für Tests und Vorführung: Drehzahl folgt dem Sollwert (≈ 60 U/min je %).</summary>
public sealed class SimulatedFanDevice(string role = PicoFanDevice.RoleFans) : IFanDevice
{
    private readonly List<string> _events = new();
    public string Description => role == PicoFanDevice.RoleDisplay ? L.T("Simulierter Display-Pico") : L.T("Simulierter Pico");
    public string Role => role;
    public List<byte[]> Images { get; } = [];
    public int Resets { get; private set; }
    /// <summary>Simulierte Temperaturfühler; leer = kein Fühler.</summary>
    public List<TempReading> Sensors { get; } = [];
    public IReadOnlyList<TempReading> Temperatures => Sensors.ToList();

    /// <summary>Tastendruck nachbilden („BTN 1“ … „BTN 4 LONG“).</summary>
    public void Press(string evt)
    {
        lock (_events) _events.Add(evt);
    }

    public Task<int[]> PollAsync(CancellationToken ct = default) => ExchangeAsync(LastPercent, ct);

    public Task ShowImageAsync(byte[] planes, CancellationToken ct = default)
    {
        if (Fail) throw new IOException(L.T("Simulierter Pico getrennt"));
        Images.Add(planes);
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        Resets++;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> DrainEvents()
    {
        lock (_events)
        {
            var list = _events.ToList();
            _events.Clear();
            return list;
        }
    }
    public int[] LastPercent { get; private set; } = [.. Enumerable.Repeat(100, 6)];
    /// <summary>Kanäle ohne Drehzahl (für den Test „Lüfter steht“).</summary>
    public HashSet<int> Stalled { get; } = [];
    public bool Fail { get; set; }

    public Task<int[]> ExchangeAsync(IReadOnlyList<int> percent, CancellationToken ct = default)
    {
        if (Fail) throw new IOException(L.T("Simulierter Pico getrennt"));
        LastPercent = percent.ToArray();
        return Task.FromResult(percent.Select((p, i) => Stalled.Contains(i + 1) || p == 0 ? 0 : 900 + p * 60).ToArray());
    }

    public void Dispose() { }
}
