using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

public class MinerLogTests
{
    // Zeilen wie von AxeOS v2.15.3 (/api/ws bzw. /api/system/logs) geliefert
    private const string FanLine = "\u001b[0;32mI (35451) fan_controller: Temp: 48.7°C, SetPoint: 60.0°C, Output: 36.1%\u001b[0m";
    private const string WarnLine = "\u001b[0;33mW (120034) stratum_task: Stratum connection lost\u001b[0m";
    private const string ErrorLine = "\u001b[0;31mE (5) power: Overheat mode activated\u001b[0m";

    [Fact]
    public void Parses_esp_idf_lines_and_strips_ansi()
    {
        var l = LogParser.Parse(FanLine, DateTime.Now);
        Assert.Equal(LogLevel.Info, l.Level);
        Assert.Equal(35451, l.UptimeMs);
        Assert.Equal("fan_controller", l.Tag);
        Assert.Equal("Temp: 48.7°C, SetPoint: 60.0°C, Output: 36.1%", l.Message);

        Assert.Equal(LogLevel.Warning, LogParser.Parse(WarnLine, DateTime.Now).Level);
        Assert.Equal(LogLevel.Error, LogParser.Parse(ErrorLine, DateTime.Now).Level);
    }

    [Fact]
    public void Uptime_is_converted_to_wall_clock_with_boot_time()
    {
        var boot = new DateTime(2026, 9, 24, 10, 0, 0);
        Assert.Equal(boot.AddMilliseconds(35451), LogParser.Parse(FanLine, DateTime.Now, boot).Time);
    }

    [Fact]
    public void Unknown_format_is_kept_as_info_text()
    {
        var l = LogParser.Parse("\u001b[0mirgendwas ohne Format", DateTime.Now);
        Assert.Equal("irgendwas ohne Format", l.Message);
        Assert.Null(l.UptimeMs);
    }

    [Fact]
    public void Buffer_keeps_only_the_last_lines()
    {
        var buffer = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"\u001b[0;32mI ({i}) t: m{i}\u001b[0m")) + "\n\n";
        var lines = LogParser.ParseBuffer(buffer, DateTime.Now, null, 10);
        Assert.Equal(10, lines.Count);
        Assert.Equal("m99", lines[^1].Message);
    }

    [Theory]
    [InlineData("192.168.1.50", "ws://192.168.1.50/api/ws")]
    [InlineData("http://bitaxe.local/", "ws://bitaxe.local/api/ws")]
    public void Builds_websocket_uri(string address, string expected) =>
        Assert.Equal(expected, WebSocketLogSource.BuildUri(address).ToString());

    [Fact]
    public async Task Receives_live_lines_and_reconnects_after_the_miner_drops_the_connection()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        // Fake-Miner: sendet je Verbindung zwei Zeilen (eine davon in zwei Frames) und trennt dann
        var connections = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                if (!ctx.Request.IsWebSocketRequest || ctx.Request.Url!.AbsolutePath != "/api/ws") { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                Interlocked.Increment(ref connections);
                var bytes = Encoding.UTF8.GetBytes(FanLine);
                await ws.SendAsync(bytes.AsMemory(0, 10), WebSocketMessageType.Text, false, default);
                await ws.SendAsync(bytes.AsMemory(10), WebSocketMessageType.Text, true, default);
                await ws.SendAsync(Encoding.UTF8.GetBytes(WarnLine), WebSocketMessageType.Text, true, default);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "restart", default);
            }
        });

        var lines = new List<LogLine>();
        var statuses = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = new WebSocketLogSource($"http://localhost:{port}");
        var run = source.RunAsync(l => { lock (lines) lines.Add(l); if (Volatile.Read(ref connections) >= 2 && lines.Count >= 4) cts.Cancel(); },
                                  s => { lock (statuses) statuses.Add(s); }, cts.Token);
        await run;
        listener.Stop();

        Assert.True(connections >= 2, "nach der Trennung muss neu verbunden werden");
        Assert.Equal("fan_controller", lines[0].Tag);               // zusammengesetzt aus zwei Frames
        Assert.Equal(LogLevel.Warning, lines[1].Level);
        Assert.Contains(statuses, s => s.StartsWith("Getrennt"));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
