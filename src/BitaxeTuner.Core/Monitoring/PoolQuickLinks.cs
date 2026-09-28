using System.Text.RegularExpressions;
using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Link zur Statistikseite eines Pools für den Pool-Benutzer (meist die Wallet-Adresse).</summary>
public sealed record PoolQuickLink(string Pool, Uri Url);

/// <summary>
/// Quick-Links zu den Nutzerseiten bekannter Pools – wie in AxeOS (ESP-Miner, quicklink.service.ts),
/// ergänzt um weitere Pools. Nur Anzeige: es wird nichts abgefragt, geöffnet wird erst per Klick.
/// </summary>
public static class PoolQuickLinks
{
    /// <summary>Ports: nur bei diesen Stratum-Ports (Server, die mehrere Coins über verschiedene Ports bedienen).</summary>
    private sealed record Entry(string Pool, Regex Host, string Url, int[]? Ports = null);

    /// <summary>Ganze Domain oder Subdomain davon (z. B. „stratum.ocean.xyz“ für „ocean.xyz“).</summary>
    private static Entry Domain(string pool, string domain, string url, int[]? ports = null) =>
        new(pool, new Regex($@"^(?:.+\.)?{Regex.Escape(domain)}$", RegexOptions.CultureInvariant), url, ports);

    // {user} = Pool-Benutzer bis zum ersten Punkt, {1} = erste Gruppe des Host-Musters
    private static readonly Entry[] Pools =
    [
        Domain("public-pool", "public-pool.io", "https://web.public-pool.io/#/app/{user}"),
        Domain("NerdMiner Pool", "nerdminer.de", "https://pool.nerdminer.de/#/app/{user}"),
        Domain("solomining.de", "solomining.de", "https://pool.solomining.de/#/app/{user}"),
        Domain("Blitzpool", "yourdevice.ch", "https://blitzpool.yourdevice.ch/#/app/{user}"),
        Domain("OCEAN", "ocean.xyz", "https://ocean.xyz/stats/{user}"),
        Domain("Noderunners", "pool.noderunners.network", "https://noderunners.network/en/pool/user/{user}"),
        Domain("Satoshi Radio", "satoshiradio.nl", "https://pool.satoshiradio.nl/user/{user}"),
        Domain("SoloHash", "solohash.co.uk", "https://solohash.co.uk/user/{user}"),
        Domain("Braiins Solo", "solo.stratum.braiins.com", "https://solo.braiins.com/stats/{user}"),
        Domain("Parasite", "parasite.wtf", "https://parasite.space/user/{user}"),
        Domain("SoloLuck", "sololuck.io", "https://sololuck.io/users/{user}"),
        new("CKPool Solo", new Regex(@"^(?:.{0,2}solo|stratum)\.ckpool\.org$", RegexOptions.CultureInvariant), "https://stats.ckpool.org/users/{user}"),
        Domain("Atlas Pool", "atlaspool.io", "https://atlaspool.io/dashboard.html?wallet={user}"),
        new("M45Core", new Regex(@"^(eu\.|tinyminer\.)?m45core\.com$", RegexOptions.CultureInvariant), "https://{1}m45core.com/user/{user}"),
        Domain("BTC PoW Lab", "btcpowlab-pool.com", "https://btcpowlab-pool.com/miner/{user}"),
        // Weitere Solo-Pools (Statistikseite je Adresse geprüft am 28.09.2026)
        Domain("SoloFury", "btc.solofury.com", "https://solofury.com/miner/?coin=btc&addr={user}"),
        Domain("NerdMiners.org", "pool.nerdminers.org", "https://pool.nerdminers.org/users/{user}"),
        Domain("Mineshop Solo", "solo.mineshop.eu", "https://solo.mineshop.eu/miner/?wallet={user}"),
        // SoloPool.org: dieselben Server für mehrere Coins – Bitcoin nur an den BTC-Ports
        Domain("SoloPool.org", "solopool.org", "https://btc.solopool.org/miner/{user}", [8005, 7005, 9005]),
    ];

    /// <summary>Pool-Benutzer: Adressen und einfache Namen; alles andere bekommt keinen Link.</summary>
    private static readonly Regex User = new(@"^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>Link für den gerade aktiven Pool (bei Fallback der Fallback-Pool) oder null.</summary>
    public static PoolQuickLink? For(SystemInfo? info)
    {
        if (info is null) return null;
        return info.isUsingFallbackStratum != 0
            ? For(info.fallbackStratumURL, info.fallbackStratumUser, info.fallbackStratumPort)
            : For(info.stratumURL, info.stratumUser, info.stratumPort);
    }

    /// <param name="port">Stratum-Port, falls nicht in der Adresse enthalten (AxeOS führt ihn getrennt).</param>
    public static PoolQuickLink? For(string? stratumUrl, string? stratumUser, int port = 0)
    {
        var host = NormalizeHost(stratumUrl);
        if (PortOf(stratumUrl) is { } p) port = p;
        var user = (stratumUser ?? "").Split('.')[0].Trim();
        if (host.Length == 0 || !User.IsMatch(user)) return null;

        foreach (var pool in Pools)
        {
            var match = pool.Host.Match(host);
            if (!match.Success) continue;
            if (pool.Ports is { } ports && !ports.Contains(port)) continue;
            var url = pool.Url
                .Replace("{1}", match.Groups.Count > 1 ? match.Groups[1].Value : "")
                .Replace("{user}", Uri.EscapeDataString(user));
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? new PoolQuickLink(pool.Pool, uri)
                : null;
        }
        return null;
    }

    /// <summary>„stratum+tcp://Host:3333/…“ → 3333, ohne Port null.</summary>
    internal static int? PortOf(string? value)
    {
        var host = (value ?? "").Trim();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        var end = host.IndexOfAny(['/', '?', '#']);
        if (end >= 0) host = host[..end];
        var colon = host.LastIndexOf(':');
        return colon >= 0 && int.TryParse(host[(colon + 1)..], out var port) ? port : null;
    }

    /// <summary>„stratum+tcp://Host:3333/…“ → „host“.</summary>
    internal static string NormalizeHost(string? value)
    {
        var host = (value ?? "").Trim();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        var end = host.IndexOfAny(['/', '?', '#']);
        if (end >= 0) host = host[..end];
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        var colon = host.LastIndexOf(':');
        if (colon >= 0) host = host[..colon];
        return host.TrimEnd('.').ToLowerInvariant();
    }
}
