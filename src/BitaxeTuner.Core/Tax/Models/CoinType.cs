namespace BitaxeTuner.Core.Tax.Models;

/// <summary>
/// Unterstützte Coins. Werte werden als Text gespeichert (rewards.json, disposals.json) – neue Coins nur hinten anfügen.
/// 0.9.12: DigiByte, Namecoin, Elastos, Peercoin, Emercoin (SHA-256 bzw. Merged Mining) – kommen über ein Pool-Konto,
/// nicht über Wallet-Abfragen.
/// </summary>
public enum CoinType
{
    Bitcoin,
    BitcoinCash,
    DigiByte,
    Namecoin,
    Elastos,
    Peercoin,
    Emercoin,
}

public static class CoinTypeExtensions
{
    /// <summary>Coins mit Wallet-Abfrage über die Blockchain (mempool.space/Blockchair) und Solo-Chance.</summary>
    public static readonly IReadOnlyList<CoinType> WalletCoins = [CoinType.Bitcoin, CoinType.BitcoinCash];

    public static bool HasWalletSupport(this CoinType coin) => coin is CoinType.Bitcoin or CoinType.BitcoinCash;

    public static string Symbol(this CoinType coin) => coin switch
    {
        CoinType.Bitcoin => "BTC",
        CoinType.BitcoinCash => "BCH",
        CoinType.DigiByte => "DGB",
        CoinType.Namecoin => "NMC",
        CoinType.Elastos => "ELA",
        CoinType.Peercoin => "PPC",
        CoinType.Emercoin => "EMC",
        _ => coin.ToString()
    };

    /// <summary>Coin zu einem Kürzel (BTC, DGB …) oder Namen (bitcoin, digibyte …); null = unbekannt.</summary>
    public static CoinType? FromSymbolOrName(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        foreach (var c in Enum.GetValues<CoinType>())
            if (v == c.Symbol().ToLowerInvariant() || v == c.ToString().ToLowerInvariant() || v == c.CoinGeckoId()) return c;
        return v switch { "bitcoincash" or "bcash" => CoinType.BitcoinCash, _ => null };
    }

    public static string CoinGeckoId(this CoinType coin) => coin switch
    {
        CoinType.Bitcoin => "bitcoin",
        CoinType.BitcoinCash => "bitcoin-cash",
        CoinType.DigiByte => "digibyte",
        CoinType.Namecoin => "namecoin",
        CoinType.Elastos => "elastos",
        CoinType.Peercoin => "peercoin",
        CoinType.Emercoin => "emercoin",
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
