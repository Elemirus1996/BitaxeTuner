namespace BitaxeTuner.Core.Tax.Models;

/// <summary>Unterstützte Coins. Erweiterbar um weitere Bitcoin-Forks.</summary>
public enum CoinType
{
    Bitcoin,
    BitcoinCash
}

public static class CoinTypeExtensions
{
    public static string Symbol(this CoinType coin) => coin switch
    {
        CoinType.Bitcoin => "BTC",
        CoinType.BitcoinCash => "BCH",
        _ => coin.ToString()
    };

    public static string CoinGeckoId(this CoinType coin) => coin switch
    {
        CoinType.Bitcoin => "bitcoin",
        CoinType.BitcoinCash => "bitcoin-cash",
        _ => throw new ArgumentOutOfRangeException(nameof(coin))
    };

    public static string BlockchairSlug(this CoinType coin) => coin switch
    {
        CoinType.Bitcoin => "bitcoin",
        CoinType.BitcoinCash => "bitcoin-cash",
        _ => throw new ArgumentOutOfRangeException(nameof(coin))
    };

    /// <summary>
    /// Coin aus dem Adressformat ableiten. Legacy-Adressen (1…/3…) gibt es bei
    /// BTC und BCH gleichermaßen; dort ist das Ergebnis nur eine Vorgabe.
    /// </summary>
    public static CoinType GuessFromAddress(string address, CoinType fallbackForLegacy = CoinType.BitcoinCash)
    {
        var a = address.Trim();
        if (a.StartsWith("bitcoincash:", StringComparison.OrdinalIgnoreCase)) return CoinType.BitcoinCash;
        if (a.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)) return CoinType.Bitcoin;
        // CashAddr ohne Präfix: 42 Zeichen, beginnt mit q oder p
        if (a.Length == 42 && (a[0] == 'q' || a[0] == 'p')) return CoinType.BitcoinCash;
        return fallbackForLegacy;
    }

    /// <summary>Erkennt, ob eine Adresse eindeutig einem Coin zuzuordnen ist.</summary>
    public static bool IsUnambiguous(string address)
    {
        var a = address.Trim();
        return a.StartsWith("bitcoincash:", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
            || (a.Length == 42 && (a[0] == 'q' || a[0] == 'p'));
    }
}
