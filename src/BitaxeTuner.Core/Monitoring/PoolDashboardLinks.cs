namespace BitaxeTuner.Core.Monitoring;

public static class PoolDashboardLinks
{
    private const string BtcPowLabHost = "stratum.btcpowlab-pool.com";

    public static Uri? ForMiner(string? stratumHost, string? walletAddress)
    {
        var host = NormalizeHost(stratumHost);
        var address = walletAddress?.Trim();

        if (!string.Equals(host, BtcPowLabHost, StringComparison.OrdinalIgnoreCase) ||
            !LooksLikeBitcoinMainnetAddress(address))
            return null;

        return new Uri($"https://btcpowlab-pool.com/miner/{Uri.EscapeDataString(address!)}");
    }

    private static string NormalizeHost(string? value)
    {
        var host = value?.Trim() ?? "";
        if (host.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(host, UriKind.Absolute, out var uri))
            host = uri.Host;
        else
        {
            host = host.Replace("stratum+tcp://", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("stratum+ssl://", "", StringComparison.OrdinalIgnoreCase);
            var slash = host.IndexOf('/');
            if (slash >= 0) host = host[..slash];
            var colon = host.LastIndexOf(':');
            if (colon >= 0) host = host[..colon];
        }
        return host.TrimEnd('.');
    }

    private static bool LooksLikeBitcoinMainnetAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace)) return false;
        return address.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) && address.Length is >= 42 and <= 62 ||
               address[0] is '1' or '3' && address.Length is >= 26 and <= 35;
    }
}
