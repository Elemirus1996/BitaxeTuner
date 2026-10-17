using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Fans;

/// <summary>
/// 0.9.23 Kopplung über WLAN (Pico-Programm 8.5), wenn der Schlüssel nicht mehr passt: Am Pico wird BOOTSEL bzw. KEY0
/// 3 s gehalten, danach nimmt er 2 Minuten lang einen neuen Schlüssel an. Beide Seiten handeln ihn per X25519 aus (kein
/// Mitlesen im WLAN) und zeigen denselben 4-stelligen Code – der Nutzer vergleicht und bestätigt (kein Dazwischenschalten).
/// Protokoll siehe btfan.py („Pairing“).
/// </summary>
public sealed class PicoPairing : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly List<byte> _buffer;
    private readonly string _nonce;

    public string Code { get; }
    public string Role { get; }
    public string Version { get; }
    /// <summary>Neuer Schlüssel (32 Byte) – gilt erst nach <see cref="Confirm"/>.</summary>
    public byte[] Key { get; }
    public string? RemoteIp => (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString();

    private PicoPairing(TcpClient client, List<byte> buffer, string nonce, string role, string version, byte[] key, string code)
    {
        _client = client;
        _stream = client.GetStream();
        _buffer = buffer;
        _nonce = nonce;
        Role = role;
        Version = version;
        Key = key;
        Code = code;
    }

    /// <summary>Verbinden und Schlüssel aushandeln; der Code muss dann am Pico verglichen werden.</summary>
    public static PicoPairing Start(string host, int port, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(8);
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
            var greeting = NetworkLineTransport.ReadRawLine(stream, buffer, limit) ?? throw new IOException(L.T("{0} meldet sich nicht.", host));
            var g = greeting.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (g.Length != 4 || g[0] != "BTFAN" || g[3].Length != 32)
                throw new IOException(L.T("{0} ist kein BitaxeTuner-Pico.", host));
            var (version, role, nonce) = (g[1], g[2], g[3]);
            var secret = RandomNumberGenerator.GetBytes(32);
            var pub = X25519.ScalarMult(secret, X25519.BasePoint);
            stream.Write(Encoding.ASCII.GetBytes($"PAIR {Hex(pub)}\n"));
            string? answer;
            try { answer = NetworkLineTransport.ReadRawLine(stream, buffer, limit); }
            catch (IOException) { answer = null; }   // nicht im Kopplungsmodus: der Pico trennt sofort
            var a = answer?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a is not { Length: 2 } || a[0] != "PAIRB" || a[1].Length != 64)
                throw new PicoNotPairingException(L.T("{0} ist nicht im Kopplungsmodus – am Pico BOOTSEL (beim Display-Pico auch KEY0) 3 Sekunden halten, bis die LED schnell blinkt, dann erneut versuchen.", host));
            var picoPub = Convert.FromHexString(a[1]);
            var shared = X25519.ScalarMult(secret, picoPub);
            if (shared.All(b => b == 0)) throw new IOException(L.T("{0}: ungültiger Schlüssel vom Pico.", host));
            var k = SHA256.HashData([.. Encoding.ASCII.GetBytes($"BTPAIR|{nonce}|"), .. pub, .. picoPub, .. shared]);
            var key = HMACSHA256.HashData(k, "key"u8.ToArray());
            var c = HMACSHA256.HashData(k, "code"u8.ToArray());
            var code = (((uint)c[0] << 24 | (uint)c[1] << 16 | (uint)c[2] << 8 | c[3]) % 10000).ToString("D4");
            return new PicoPairing(client, buffer, nonce, role, version, key, code);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Nutzer hat den Code bestätigt: Pico speichert den Schlüssel und bestätigt ihn seinerseits.</summary>
    public void Confirm(TimeSpan? timeout = null)
    {
        _stream.Write(Encoding.ASCII.GetBytes($"PAIROK {Hex(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes("OK|" + _nonce)))}\n"));
        string? answer;
        try { answer = NetworkLineTransport.ReadRawLine(_stream, _buffer, timeout ?? TimeSpan.FromSeconds(8)); }
        catch (IOException) { answer = null; }
        var a = answer?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = Hex(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes("DONE|" + _nonce)));
        if (a is not { Length: 2 } || a[0] != "PAIRED"
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a[1]), Encoding.ASCII.GetBytes(expected)))
            throw new IOException(L.T("Der Pico hat die Kopplung nicht bestätigt (Kopplungsmodus abgelaufen?)."));
    }

    public void Dispose() => _client.Dispose();

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>Pico ist nicht im Kopplungsmodus (Taste nicht gehalten oder Zeitfenster vorbei).</summary>
public sealed class PicoNotPairingException(string message) : IOException(message);

/// <summary>Anmeldung am Pico abgelehnt: der gespeicherte Schlüssel passt nicht (mehr).</summary>
public sealed class PicoKeyMismatchException(string message) : IOException(message);
