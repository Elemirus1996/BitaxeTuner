using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Discovery;

public sealed record DiscoveredMiner(string Address, MinerInfo Info);

/// <summary>Sucht im lokalen /24-Netz nach Geräten, die auf <c>/api/system/info</c> antworten.</summary>
public static class NetworkScanner
{
    public static IReadOnlyList<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                        && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address)
            .Distinct()
            .ToList();

    public static async Task<IReadOnlyList<DiscoveredMiner>> ScanAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var targets = LocalIPv4Addresses()
            .SelectMany(ip =>
            {
                var b = ip.GetAddressBytes();
                return Enumerable.Range(1, 254).Select(i => $"{b[0]}.{b[1]}.{b[2]}.{i}");
            })
            .Distinct()
            .ToList();

        var found = new List<DiscoveredMiner>();
        var done = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, token) =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}/api/system/info");
                using var resp = await http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                    var info = AxeOsClient.Parse(doc.RootElement);
                    if (info.FrequencyMhz > 0 || info.HashRateGh > 0 || info.AsicModel is not null)
                        lock (found) found.Add(new DiscoveredMiner(ip, info));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested) { }
            finally
            {
                progress?.Report(Interlocked.Increment(ref done) / (double)targets.Count);
            }
        }).ConfigureAwait(false);

        return found.OrderBy(f => IPAddress.Parse(f.Address).GetAddressBytes()[3]).ToList();
    }
}
