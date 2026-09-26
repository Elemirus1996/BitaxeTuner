namespace BitaxeTuner.Core.Tax.Models;

/// <summary>Eine überwachte Wallet-Adresse mit Coin und Anzeigename.</summary>
public class WalletAddress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Address { get; set; } = string.Empty;
    public CoinType Coin { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Wurde aus der Miner-Konfiguration übernommen (Gerätename als Label).</summary>
    public string? SourceDeviceHost { get; set; }

    public override string ToString() => $"{Label} ({Coin.Symbol()}) – {Address}";
}
