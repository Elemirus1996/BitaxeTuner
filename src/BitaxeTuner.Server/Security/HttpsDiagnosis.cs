using System.Net;
using System.Net.Sockets;

namespace BitaxeTuner.Server.Security;

/// <summary>Ergebnis der Server-Seite des HTTPS-Assistenten.</summary>
/// <param name="Resolved">IPv4-Adressen, auf die der Name laut Server-DNS zeigt (leer = löst nicht auf).</param>
/// <param name="PointsHere">Zeigt der Name auf eine Adresse dieses Servers?</param>
/// <param name="Private">Ist die Adresse eine Heimnetz-Adresse (dann braucht der Router meist eine Rebind-Ausnahme)?</param>
public sealed record HttpsCheck(string Host, IReadOnlyList<string> Resolved, IReadOnlyList<string> ServerIps, bool PointsHere, bool Private, string? Error);

/// <summary>
/// 0.9.21 HTTPS-Assistent: prüft serverseitig, ob der DuckDNS-Name auflöst und auf diesen Server zeigt. Ob das Gerät des
/// Nutzers den Namen erreicht (DNS-Rebind-Schutz des Routers!), prüft der Browser selbst – das kann nur er.
/// </summary>
public static class HttpsDiagnosis
{
    public static async Task<HttpsCheck> CheckAsync(string host, Func<string, Task<IPAddress[]>> resolve, IEnumerable<IPAddress> serverIps)
    {
        var mine = serverIps.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
        if (host.Length == 0) return new HttpsCheck(host, [], mine, false, false, null);
        IPAddress[] found;
        string? error = null;
        try
        {
            var task = resolve(host);
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8))) != task) throw new TimeoutException();
            found = await task;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            found = [];
            error = ex is TimeoutException ? "timeout" : ex.Message;
        }
        var v4 = found.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
        return new HttpsCheck(host, v4, mine, v4.Any(mine.Contains), v4.Any(a => IsPrivate(IPAddress.Parse(a))), error);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork &&
               (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}
