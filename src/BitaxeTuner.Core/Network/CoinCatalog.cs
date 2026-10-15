using System.Text.RegularExpressions;

namespace BitaxeTuner.Core.Network;

/// <summary>
/// 0.9.16: Kürzel → CoinGecko-ID für die frei wählbaren Kurse („weitere Coins“). Gängige Coins sind eingetragen; alles
/// andere geht als „KÜRZEL=coingecko-id“ (die ID steht in der Adresse der CoinGecko-Seite des Coins).
/// </summary>
public static partial class CoinCatalog
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "bitcoin", ["BCH"] = "bitcoin-cash", ["BSV"] = "bitcoin-cash-sv", ["XEC"] = "ecash", ["ETH"] = "ethereum",
        ["ETC"] = "ethereum-classic", ["LTC"] = "litecoin", ["DOGE"] = "dogecoin", ["XMR"] = "monero", ["KAS"] = "kaspa",
        ["SOL"] = "solana", ["XRP"] = "ripple", ["ADA"] = "cardano", ["DGB"] = "digibyte", ["NMC"] = "namecoin",
        ["ELA"] = "elastos", ["PPC"] = "peercoin", ["EMC"] = "emercoin", ["SYS"] = "syscoin", ["FB"] = "fractal-bitcoin",
        ["HTR"] = "hathor", ["RVN"] = "ravencoin", ["ZEC"] = "zcash", ["DASH"] = "dash", ["ALPH"] = "alephium",
    };

    [GeneratedRegex("^[A-Z0-9]{1,10}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex("^[a-z0-9-]{2,60}$")]
    private static partial Regex IdPattern();

    /// <summary>Eintrag prüfen: „ETH“ oder „XYZ=xyz-coin“. Null = ungültig bzw. unbekanntes Kürzel ohne ID.</summary>
    public static (string Symbol, string Id)? Parse(string? entry)
    {
        var e = (entry ?? "").Trim();
        if (e.Length == 0) return null;
        var parts = e.Split('=', 2, StringSplitOptions.TrimEntries);
        var symbol = parts[0].ToUpperInvariant();
        if (!SymbolPattern().IsMatch(symbol)) return null;
        if (parts.Length == 2) return IdPattern().IsMatch(parts[1].ToLowerInvariant()) ? (symbol, parts[1].ToLowerInvariant()) : null;
        return Known.TryGetValue(symbol, out var id) ? (symbol, id) : null;
    }

    /// <summary>CoinGecko-ID eines Kürzels (eigene Einträge zuerst, dann die bekannten).</summary>
    public static string? IdFor(string symbol, IEnumerable<string>? own = null)
    {
        foreach (var e in own ?? [])
            if (Parse(e) is { } p && string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) return p.Id;
        return Known.TryGetValue(symbol, out var id) ? id : null;
    }
}
