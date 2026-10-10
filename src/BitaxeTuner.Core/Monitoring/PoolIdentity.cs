using System.Net;
using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>Name und Seite des Pools, mit dem ein Miner gerade verbunden ist (Anzeige in der Übersicht).</summary>
/// <param name="Url">Statistikseite des Pool-Benutzers (Quick-Link), sonst Startseite des Pools; null bei IP-Adressen.</param>
public sealed record PoolIdentity(string Name, Uri? Url, string Host);

/// <summary>
/// 0.9.13: Pool erkennen – zuerst über die Quick-Links (Seite des Pool-Benutzers), dann über eine Liste bekannter
/// Pools, sonst Name = Domain. Nur Anzeige; abgefragt wird nichts.
/// </summary>
public static class PoolIdentities
{
    private sealed record Known(string Name, string Domain, string Home);

    private static readonly Known[] List =
    [
        new("Mining-Dutch", "mining-dutch.nl", "https://www.mining-dutch.nl/"),
        new("Braiins Pool", "braiins.com", "https://braiins.com/pool"),
        new("ViaBTC", "viabtc.com", "https://www.viabtc.com/"),
        new("F2Pool", "f2pool.com", "https://www.f2pool.com/"),
        new("AntPool", "antpool.com", "https://www.antpool.com/"),
        new("NiceHash", "nicehash.com", "https://www.nicehash.com/"),
        new("Luxor", "luxor.tech", "https://www.luxor.tech/"),
        new("Kano Pool", "kano.is", "https://kano.is/"),
        new("Poolin", "poolin.com", "https://www.poolin.com/"),
        new("EMCD", "emcd.io", "https://emcd.io/"),
        new("D-Central Solo", "d-central.tech", "https://d-central.tech/"),
    ];

    /// <summary>Gerade aktiver Pool (bei Fallback der Fallback-Pool); null ohne Pool-Adresse.</summary>
    public static PoolIdentity? For(SystemInfo? info)
    {
        if (info is null) return null;
        var fallback = info.isUsingFallbackStratum != 0;
        var url = fallback ? info.fallbackStratumURL : info.stratumURL;
        var host = PoolQuickLinks.NormalizeHost(url);
        if (host.Length == 0) return null;

        if (PoolQuickLinks.For(info) is { } link) return new PoolIdentity(link.Pool, link.Url, host);
        foreach (var k in List)
            if (host == k.Domain || host.EndsWith("." + k.Domain, StringComparison.Ordinal))
                return new PoolIdentity(k.Name, new Uri(k.Home), host);

        // Eigener Node im Heimnetz o. Ä.: Adresse als Name, kein Link
        if (IPAddress.TryParse(host, out _) || !host.Contains('.')) return new PoolIdentity(host, null, host);
        var domain = Domain(host);
        return new PoolIdentity(domain, Uri.TryCreate("https://" + domain + "/", UriKind.Absolute, out var u) ? u : null, host);
    }

    /// <summary>„eu.stratum.example.com“ → „example.com“ (bei zweistelligen Endungen wie „co.uk“ drei Teile).</summary>
    internal static string Domain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var take = parts[^1].Length == 2 && parts[^2] is "co" or "com" or "org" or "net" or "ac" ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }
}
