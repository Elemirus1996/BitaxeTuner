using System.Text.Json;
using System.Threading.Channels;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// Live-Logs eines Miners als Server-Sent Events. Nutzt die gemeinsame <see cref="LogHub"/>-Verbindung des Miners –
/// egal wie viele Browser zusehen, der Miner hat höchstens eine WebSocket-Verbindung zum Server.
/// </summary>
public static class MinerLogStream
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task ServeAsync(HttpContext http, MinerConnection connection)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var ct = http.RequestAborted;
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

        // Zuerst der Puffer des Miners (letzte Zeilen), danach live
        try
        {
            var buffer = await connection.GetLogBufferAsync(ct);
            foreach (var line in LogParser.ParseBuffer(buffer, DateTime.Now, connection.BootTime, 300))
                channel.Writer.TryWrite(Frame("line", line));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            channel.Writer.TryWrite(Frame("status", new { text = "Puffer nicht verfügbar: " + ex.Message }));
        }

        using var subscription = connection.Logs.Subscribe(
            line => channel.Writer.TryWrite(Frame("line", line)),
            status => channel.Writer.TryWrite(Frame("status", new { text = status })));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                string frame;
                try { frame = await channel.Reader.ReadAsync(heartbeat.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = ": ping\n\n"; }
                await http.Response.WriteAsync(frame, ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* Browser geschlossen */ }
        catch (IOException) { /* Browser geschlossen */ }
    }

    private static string Frame(string type, object payload) =>
        $"event: {type}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n";
}
