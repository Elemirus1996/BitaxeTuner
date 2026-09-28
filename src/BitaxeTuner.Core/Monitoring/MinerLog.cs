using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Monitoring;

public enum LogLevel
{
    Error,
    Warning,
    Info,
    Debug,
    Verbose,
    /// <summary>Von der App eingefügte Zeile (z. B. Tuning-Änderung, Verbindungsstatus).</summary>
    App,
}

/// <summary>Eine Log-Zeile des Miners (ESP-IDF-Format, ANSI-Farbcodes entfernt).</summary>
public sealed record LogLine(DateTime Time, LogLevel Level, long? UptimeMs, string Tag, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Error => "E",
        LogLevel.Warning => "W",
        LogLevel.Info => "I",
        LogLevel.Debug => "D",
        LogLevel.Verbose => "V",
        _ => "»",
    };

    public override string ToString() =>
        $"{Time:HH:mm:ss} {LevelText} {(UptimeMs is { } u ? $"({u}) " : "")}{(Tag.Length > 0 ? Tag + ": " : "")}{Message}";
}

/// <summary>
/// Zerlegt ESP-IDF-Logzeilen, wie sie AxeOS (geprüft v2.15.3) über /api/ws und /api/system/logs liefert:
/// <c>ESC[0;32mI (35451) fan_controller: Temp: 48.7°C ...ESC[0m</c>.
/// </summary>
public static partial class LogParser
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^([EWIDV]) \((\d+)\) ([^:]{1,40}): ?(.*)$")]
    private static partial Regex LineRegex();

    public static string StripAnsi(string s) => AnsiRegex().Replace(s, "");

    /// <param name="bootTime">Startzeit des Miners (jetzt − Uptime); rechnet die ms seit Boot in Uhrzeit um.</param>
    public static LogLine Parse(string raw, DateTime received, DateTime? bootTime = null)
    {
        var text = StripAnsi(raw).TrimEnd('\r', '\n', ' ');
        var m = LineRegex().Match(text);
        if (!m.Success)
            return new LogLine(received, LogLevel.Info, null, "", text);

        var uptime = long.Parse(m.Groups[2].Value);
        var time = bootTime is { } boot ? boot.AddMilliseconds(uptime) : received;
        var level = m.Groups[1].Value switch
        {
            "E" => LogLevel.Error,
            "W" => LogLevel.Warning,
            "D" => LogLevel.Debug,
            "V" => LogLevel.Verbose,
            _ => LogLevel.Info,
        };
        return new LogLine(time, level, uptime, m.Groups[3].Value.Trim(), m.Groups[4].Value);
    }

    /// <summary>Kompletten Puffer (GET /api/system/logs) zerlegen; nur die letzten <paramref name="maxLines"/> Zeilen.</summary>
    public static List<LogLine> ParseBuffer(string buffer, DateTime received, DateTime? bootTime, int maxLines)
    {
        var lines = buffer.Split('\n').Where(l => !string.IsNullOrWhiteSpace(StripAnsi(l))).ToList();
        return lines.Skip(Math.Max(0, lines.Count - maxLines)).Select(l => Parse(l, received, bootTime)).ToList();
    }
}

/// <summary>Quelle für Live-Logzeilen eines Miners.</summary>
public interface IMinerLogSource
{
    /// <summary>Läuft bis zum Abbruch; verbindet nach Trennung (z. B. Neustart des Miners) selbst neu.</summary>
    Task RunAsync(Action<LogLine> onLine, Action<string> onStatus, CancellationToken ct);
}

/// <summary>
/// Live-Logs über den AxeOS-WebSocket <c>ws://&lt;host&gt;/api/ws</c>. Jede Nachricht ist eine Logzeile
/// (Text-Frame), es werden nur neue Zeilen gesendet. Der Miner hat nur wenige WebSocket-Plätze – die Verbindung
/// soll deshalb nur offen sein, solange jemand mitliest.
/// </summary>
public sealed class WebSocketLogSource(string address, Func<DateTime?>? bootTime = null) : IMinerLogSource
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public Uri Uri { get; } = BuildUri(address);

    public static Uri BuildUri(string address)
    {
        var a = address.Trim().TrimEnd('/');
        if (a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) a = "wss://" + a[8..];
        else if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) a = "ws://" + a[7..];
        else a = "ws://" + a;
        return new Uri(a + "/api/ws");
    }

    public async Task RunAsync(Action<LogLine> onLine, Action<string> onStatus, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                onStatus("Verbinde …");
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(8));
                    await ws.ConnectAsync(Uri, connectCts.Token).ConfigureAwait(false);
                }
                attempt = 0;
                onStatus(L.T("Live verbunden"));
                await ReceiveLoopAsync(ws, onLine, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                onStatus("Verbindung vom Miner getrennt");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onStatus(ex is WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket }
                    ? L.T("Diese Firmware bietet keine Live-Logs über /api/ws")
                    : L.T("Keine Verbindung: ") + ex.Message);
            }
            finally
            {
                // Platz auf dem Miner sauber freigeben
                if (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
                    }
                    catch { /* egal */ }
                }
            }

            var wait = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            onStatus(L.T("Getrennt – neuer Versuch in {0:0} s", wait.TotalSeconds));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        onStatus(L.T("Gestoppt"));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, Action<LogLine> onLine, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Close-Handshake bestätigen, damit der Miner seinen WebSocket-Platz sofort freigibt
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
                }
                catch { /* Verbindung ist ohnehin weg */ }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var now = DateTime.Now;
            foreach (var part in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(LogParser.StripAnsi(part))) continue;
                onLine(LogParser.Parse(part, now, bootTime?.Invoke()));
            }
        }
    }
}

/// <summary>Erzeugt plausible Logzeilen für simulierte Geräte (Demo-Modus, keine Netzwerkzugriffe).</summary>
public sealed class SimulatedLogSource(SimulatedMinerClient sim) : IMinerLogSource
{
    public async Task RunAsync(Action<LogLine> onLine, Action<string> onStatus, CancellationToken ct)
    {
        onStatus("Live (Simulation)");
        var start = DateTime.Now;
        var random = new Random(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var info = await sim.GetInfoAsync(ct).ConfigureAwait(false);
                var uptime = (long)(DateTime.Now - start).TotalMilliseconds;
                onLine(new LogLine(DateTime.Now, LogLevel.Info, uptime, "fan_controller",
                    $"Temp: {info.MaxChipTempC:0.0}°C, SetPoint: 60.0°C, Output: {info.FanPercent ?? 50:0.0}%"));
                if (random.NextDouble() < 0.6)
                    onLine(new LogLine(DateTime.Now, LogLevel.Info, uptime + 3, "asic_result",
                        $"ID: 0000dd01, ASIC nr: 0, Core: {random.Next(0, 128)}/{random.Next(0, 16)}, diff {random.Next(100, 5000)} of 1000"));
                if (info.ErrorPercent > 3 && random.NextDouble() < 0.3)
                    onLine(new LogLine(DateTime.Now, LogLevel.Warning, uptime + 5, "asic_result", "Hash error rate high"));
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        onStatus(L.T("Gestoppt"));
    }
}
