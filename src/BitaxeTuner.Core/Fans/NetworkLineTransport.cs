using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Fans;

/// <summary>
/// Verbindung zum Pico über WLAN (TCP, Port 8490) mit gemeinsamem Schlüssel. Beide Seiten weisen beim Verbinden den
/// Schlüssel nach (Challenge-Response), danach trägt jede Zeile einen Zähler und eine HMAC-Signatur mit einem
/// Sitzungsschlüssel – fremde Geräte im WLAN können weder Befehle senden noch Antworten unterschieben oder wiederholen.
/// Protokoll siehe btfan.py („WLAN“). Aufspielen über das Raw-REPL geht hier nicht – dafür gibt es PUT/COMMIT.
/// </summary>
public sealed class NetworkLineTransport : ILineTransport
{
    public const int DefaultPort = 8490;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _sessionKey;
    private readonly List<byte> _raw = [];
    private long _tx, _rx;

    /// <summary>Programmversion und Rolle („fans“/„display“) laut Begrüßung des Pico.</summary>
    public string Version { get; }
    public string Role { get; }
    public string Endpoint { get; }

    /// <summary>Tatsächlich erreichte IP-Adresse (für den Rückfall, falls der Gerätename später nicht auflöst).</summary>
    public string? RemoteIp => (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString();

    private NetworkLineTransport(TcpClient client, string endpoint, byte[] sessionKey, string version, string role)
    {
        _client = client;
        _stream = client.GetStream();
        _sessionKey = sessionKey;
        Endpoint = endpoint;
        Version = version;
        Role = role;
    }

    /// <summary>Verbinden und gegenseitig anmelden. Wirft <see cref="IOException"/> bei falschem Schlüssel oder fremdem Gerät.</summary>
    public static NetworkLineTransport Connect(string host, int port, byte[] key, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = (int)limit.TotalMilliseconds, SendTimeout = (int)limit.TotalMilliseconds };
        try
        {
            using (var cts = new CancellationTokenSource(limit))
            {
                try { client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new IOException(L.T("{0} antwortet nicht (WLAN?).", host)); }
                catch (SocketException ex) { throw new IOException(L.T("{0} nicht erreichbar: {1}", host, ex.Message)); }
            }
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var greeting = ReadRawLine(stream, buffer, limit) ?? throw new IOException(L.T("{0} meldet sich nicht.", host));
            var g = greeting.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (g.Length != 4 || g[0] != "BTFAN" || g[3].Length != 32)
                throw new IOException(L.T("{0} ist kein BitaxeTuner-Pico.", host));
            var (version, role, nonceP) = (g[1], g[2], g[3]);
            var nonceC = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var proof = Hex(Hmac(key, $"C|{nonceP}|{nonceC}"));
            stream.Write(Encoding.ASCII.GetBytes($"AUTH {nonceC} {proof}\n"));
            string? answer;
            try { answer = ReadRawLine(stream, buffer, limit); }
            catch (IOException) { answer = null; }   // falscher Schlüssel: der Pico trennt sofort
            var a = answer?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a is not { Length: 2 } || a[0] != "AUTHOK"
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a[1]), Encoding.ASCII.GetBytes(Hex(Hmac(key, $"P|{nonceC}|{nonceP}")))))
                throw new IOException(L.T("{0}: Schlüssel passt nicht – Pico neu für WLAN einrichten.", host));
            var transport = new NetworkLineTransport(client, $"{host}:{port}", Hmac(key, $"S|{nonceP}|{nonceC}"), version, role);
            transport._raw.AddRange(buffer);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Write(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var payload = line.TrimEnd('\r');
            if (payload.Length == 0 || payload.Any(c => c < 32 || c > 126)) continue;   // Steuerzeichen (Raw-REPL) nie über WLAN
            _tx++;
            sb.Append(payload).Append(" ~").Append(_tx).Append(' ').Append(Tag('c', _tx, payload)).Append('\n');
        }
        if (sb.Length > 0) _stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    public string? ReadLine(TimeSpan timeout)
    {
        var raw = ReadRawLine(_stream, _raw, timeout);
        return raw is null ? null : Verify(raw);
    }

    /// <summary>Signatur und Zähler prüfen; bei Fehler Verbindung beenden (IOException).</summary>
    private string Verify(string raw)
    {
        var i = raw.LastIndexOf(" ~", StringComparison.Ordinal);
        var rest = i >= 0 ? raw[(i + 2)..].Split(' ') : [];
        if (i < 0 || rest.Length != 2 || !long.TryParse(rest[0], out var ctr) || ctr != _rx + 1
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(rest[1]), Encoding.ASCII.GetBytes(Tag('p', ctr, raw[..i]))))
        {
            Dispose();
            throw new IOException(L.T("Pico-Verbindung: ungültige Signatur – Verbindung beendet."));
        }
        _rx = ctr;
        return raw[..i];
    }

    public string? ReadUntil(string marker, TimeSpan timeout) =>
        throw new NotSupportedException(L.T("Über WLAN lässt sich das Programm nur per Update (PUT) aufspielen."));

    /// <summary>Bereits eingetroffene Zeilen verwerfen (Signaturen werden trotzdem geprüft, damit der Zähler stimmt).</summary>
    public void Discard()
    {
        while (_client.Available > 0 || _raw.Contains((byte)'\n'))
            if (ReadLine(TimeSpan.FromMilliseconds(50)) is null) break;
    }

    public void Dispose() => _client.Dispose();

    private string Tag(char direction, long counter, string payload) =>
        Hex(Hmac(_sessionKey, $"{direction}{counter}|{payload}"))[..16];

    private static byte[] Hmac(byte[] key, string message) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(message));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Eine Zeile (ohne Zeilenende) aus dem Datenstrom; null bei Zeitüberschreitung.</summary>
    private static string? ReadRawLine(NetworkStream stream, List<byte> buffer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var chunk = new byte[4096];
        while (true)
        {
            var nl = buffer.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                var line = Encoding.ASCII.GetString(buffer.GetRange(0, nl).ToArray()).TrimEnd('\r');
                buffer.RemoveRange(0, nl + 1);
                return line;
            }
            if (buffer.Count > 8192) throw new IOException(L.T("Pico-Verbindung: Zeile zu lang."));
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            stream.ReadTimeout = Math.Max(1, (int)left.TotalMilliseconds);
            int n;
            try { n = stream.Read(chunk, 0, chunk.Length); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { return null; }
            if (n == 0) throw new IOException(L.T("Pico hat die Verbindung beendet."));
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
        }
    }
}
