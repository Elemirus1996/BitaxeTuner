using System.Net;
using System.Net.Http;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

/// <summary>
/// Gemeinsame Drosselung für mempool.space (Audit I2): Antwortet die API mit 429, wird bis „Retry-After“ (sonst mit
/// wachsendem Abstand 1, 2, 4 … höchstens 30 min) nichts mehr angefragt – für Wallet- und Netzwerkabfragen gemeinsam.
/// </summary>
public static class MempoolLimit
{
    private static readonly object Gate = new();
    private static DateTime _blockedUntilUtc = DateTime.MinValue;
    private static int _step;

    /// <summary>Bis wann gerade nicht angefragt wird (UTC); MinValue = frei.</summary>
    public static DateTime BlockedUntilUtc { get { lock (Gate) return _blockedUntilUtc; } }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var until = BlockedUntilUtc;
        if (until > DateTime.UtcNow)
            throw new HttpRequestException(L.T("mempool.space drosselt Anfragen – nächster Versuch ab {0}.", L.Short(until.ToLocalTime())));
        var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfter(resp, DateTime.UtcNow);
            resp.Dispose();
            lock (Gate)
            {
                _step = Math.Min(_step + 1, 6);
                wait ??= TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, _step - 1)));
                _blockedUntilUtc = DateTime.UtcNow + Clamp(wait.Value);
                until = _blockedUntilUtc;
            }
            throw new HttpRequestException(L.T("mempool.space drosselt Anfragen – nächster Versuch ab {0}.", L.Short(until.ToLocalTime())));
        }
        lock (Gate) _step = 0;
        return resp;
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage resp, DateTime nowUtc)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date.UtcDateTime - nowUtc;
        return null;
    }

    private static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : t > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : t;

    /// <summary>Nur für Tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _blockedUntilUtc = DateTime.MinValue;
            _step = 0;
        }
    }
}
