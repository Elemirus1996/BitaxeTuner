using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Web;

/// <summary>
/// Kleiner Nur-Lese-Webserver fürs Handy im Heimnetz (ohne Adminrechte, daher TcpListener statt HttpListener).
/// <list type="bullet">
/// <item>Antwortet nur Clients aus privaten Adressbereichen (10/8, 172.16/12, 192.168/16, 127/8, IPv6 ULA/Link-Local).</item>
/// <item>Anmeldung per PIN (nur als SHA-256 gespeichert), danach Sitzungs-Cookie (HttpOnly, SameSite=Strict, 7 Tage).</item>
/// <item>Nach 5 Fehlversuchen ist die Adresse 5 Minuten gesperrt.</item>
/// <item>Es gibt keinen Endpunkt, der etwas am Miner ändert. Die Daten enthalten keine Wallet-Adressen.</item>
/// </list>
/// </summary>
public sealed class WebViewServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionTime = TimeSpan.FromDays(7);

    private readonly Func<string> _pinHash;
    private readonly Action<string>? _upgradePin;
    private readonly Func<string> _statusJson;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _failures = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <param name="pinHash">Liefert den gespeicherten PIN-Hash (<see cref="WebViewSettings.HashPin"/>).</param>
    /// <param name="statusJson">Liefert die aktuellen Werte als JSON (ohne Wallet-Adressen).</param>
    public WebViewServer(Func<string> pinHash, Func<string> statusJson, Action<string>? upgradePin = null)
    {
        _pinHash = pinHash;
        _upgradePin = upgradePin;
        _statusJson = statusJson;
    }

    public int? Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public string? LastError { get; private set; }

    public void Start(int port, IPAddress? bindTo = null)
    {
        Stop();
        try
        {
            _listener = new TcpListener(bindTo ?? IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            LastError = null;
        }
        catch (SocketException ex)
        {
            _listener = null;
            LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? L.T("Port {0} ist bereits belegt.", port)
                : ex.Message;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        Port = null;
        _sessions.Clear();
    }

    /// <summary>Adressen, unter denen das Handy die Ansicht öffnen kann.</summary>
    public static IEnumerable<string> LocalUrls(int port) =>
        Discovery.NetworkScanner.LocalIPv4Addresses().Where(IsPrivate).Select(ip => $"http://{ip}:{port}/");

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
        return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            var stream = client.GetStream();
            if (!IsPrivate(remote))
            {
                await WriteAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes(L.T("Nur aus dem Heimnetz erreichbar.")), timeout.Token);
                return;
            }
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (request is null) return;
            var response = Handle(request, remote.ToString(), DateTime.UtcNow);
            await WriteAsync(stream, response.Status, response.ContentType, response.Body, timeout.Token, response.Headers);
        }
        catch { /* Verbindung abgebrochen o. Ä. */ }
    }

    public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);
    public sealed record Response(int Status, string ContentType, byte[] Body, Dictionary<string, string>? Headers = null);

    /// <summary>Routing (öffentlich für Tests, ohne Netzwerk).</summary>
    public Response Handle(Request req, string client, DateTime now)
    {
        var path = req.Path.Split('?')[0];
        var authed = IsAuthenticated(req, now);

        switch (req.Method, path)
        {
            case ("GET", "/app.js"):
                return Text(200, "text/javascript", WebViewPage.Script);
            case ("GET", "/style.css"):
                return Text(200, "text/css", WebViewPage.Style);
            case ("GET", "/"):
                return Text(200, "text/html", authed ? WebViewPage.Dashboard : WebViewPage.Login(null));
            case ("POST", "/login"):
                return Login(req, client, now);
            case ("GET", "/logout"):
                if (Cookie(req) is { } c) _sessions.TryRemove(c, out _);
                return Redirect("/", "bt_session=; Max-Age=0; Path=/; HttpOnly; SameSite=Strict");
            case ("GET", "/api/status"):
                return authed ? Text(200, "application/json", _statusJson()) : Text(401, "application/json", "{\"error\":\"login\"}");
            default:
                return Text(404, "text/plain", L.T("Nicht gefunden"));
        }
    }

    private Response Login(Request req, string client, DateTime now)
    {
        if (_failures.TryGetValue(client, out var f) && f.Count >= MaxFailures && f.Until > now)
            return Text(429, "text/html", WebViewPage.Login(L.T("Zu viele Fehlversuche – bitte {0} min warten.", Math.Ceiling((f.Until - now).TotalMinutes))));

        var pin = FormValue(req.Body, "pin") ?? "";
        var expected = _pinHash();
        var ok = WebViewSettings.VerifyPin(pin, expected);
        if (!ok)
        {
            var count = f.Until > now || f.Count < MaxFailures ? f.Count + 1 : 1;
            _failures[client] = (count, now + LockTime);
            return Text(401, "text/html", WebViewPage.Login(L.T("Falsche PIN.")));
        }

        _failures.TryRemove(client, out _);
        if (!expected.StartsWith("pbkdf2-", StringComparison.Ordinal)) _upgradePin?.Invoke(pin.Trim());   // Audit S6
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = now + SessionTime;
        return Redirect("/", $"bt_session={token}; Max-Age={(int)SessionTime.TotalSeconds}; Path=/; HttpOnly; SameSite=Strict");
    }

    private bool IsAuthenticated(Request req, DateTime now)
    {
        if (Cookie(req) is not { } token || !_sessions.TryGetValue(token, out var until)) return false;
        if (until > now) return true;
        _sessions.TryRemove(token, out _);
        return false;
    }

    private static string? Cookie(Request req)
    {
        if (!req.Headers.TryGetValue("cookie", out var header)) return null;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0] == "bt_session" && kv[1].Length == 64) return kv[1];
        }
        return null;
    }

    private static string? FormValue(string body, string name)
    {
        foreach (var part in body.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }
        return null;
    }

    private static Response Text(int status, string type, string body) => new(status, type, Encoding.UTF8.GetBytes(body));

    private static Response Redirect(string to, string cookie) =>
        new(303, "text/plain", [], new Dictionary<string, string> { ["Location"] = to, ["Set-Cookie"] = cookie });

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var read = 0;
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, read, "\r\n\r\n"u8)) < 0)
        {
            if (read >= buffer.Length) return null;
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }

        var body = "";
        if (headers.TryGetValue("content-length", out var lenText) && int.TryParse(lenText, out var len) && len is > 0 and <= 1024)
        {
            var bodyBytes = new byte[len];
            var have = Math.Min(len, read - headerEnd - 4);
            Array.Copy(buffer, headerEnd + 4, bodyBytes, 0, have);
            while (have < len)
            {
                var n = await stream.ReadAsync(bodyBytes.AsMemory(have), ct);
                if (n == 0) break;
                have += n;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, have);
        }
        return new Request(first[0].ToUpperInvariant(), first[1], headers, body);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);

    private static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body, CancellationToken ct,
                                         Dictionary<string, string>? extra = null)
    {
        var reason = status switch { 200 => "OK", 303 => "See Other", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 429 => "Too Many Requests", _ => "OK" };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        sb.Append($"Content-Type: {type}; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Frame-Options: DENY\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append("Content-Security-Policy: default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:\r\n");
        sb.Append("Connection: close\r\n");
        if (extra is not null)
            foreach (var (k, v) in extra) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose() => Stop();
}
