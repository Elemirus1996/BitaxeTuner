using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BitaxeTuner.Core.Tax.Models;

/// <summary>
/// Ein dokumentierter Zufluss (Blockfund oder Pool-Auszahlung) mit allen
/// Angaben für die steuerliche Erfassung. Kurs und Notiz sind editierbar,
/// damit ein fehlender Kurs manuell nachgetragen werden kann.
/// </summary>
public class MinedReward : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WalletAddressId { get; set; } = string.Empty;
    public string WalletLabel { get; set; } = string.Empty;
    public CoinType Coin { get; set; }
    public string TxId { get; set; } = string.Empty;

    /// <summary>Blockzeit in UTC.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>Menge in BTC/BCH.</summary>
    public decimal Amount { get; set; }

    /// <summary>Blockhöhe, in der der Zufluss bestätigt wurde.</summary>
    public long? BlockHeight { get; set; }

    private decimal? _eurPriceAtReceipt;
    /// <summary>EUR-Kurs je Coin möglichst nah am Zuflusszeitpunkt.</summary>
    public decimal? EurPriceAtReceipt
    {
        get => _eurPriceAtReceipt;
        set { _eurPriceAtReceipt = value; Raise(); Raise(nameof(EurValue)); }
    }

    /// <summary>Zeitpunkt des verwendeten Kurspunkts (UTC), null bei manueller Eingabe.</summary>
    public DateTime? PriceAtUtc { get; set; }

    private string _priceSource = string.Empty;
    /// <summary>Herkunft des Kurses, z. B. "CoinGecko Zeitreihe …" oder "manuell".</summary>
    public string PriceSource
    {
        get => _priceSource;
        set { _priceSource = value; Raise(); }
    }

    /// <summary>Kurs wurde von Hand eingetragen und wird nicht automatisch überschrieben.</summary>
    [JsonIgnore]
    public bool IsManualPrice => PriceSource.StartsWith("manuell", StringComparison.OrdinalIgnoreCase);

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set { _note = value; Raise(); }
    }

    [JsonIgnore]
    public decimal? EurValue => EurPriceAtReceipt.HasValue ? Amount * EurPriceAtReceipt.Value : null;

    [JsonIgnore]
    public DateTime ReceivedAtLocal => ReceivedAtUtc.ToLocalTime();

    /// <summary>
    /// Erster Tag, an dem ein Verkauf außerhalb der einjährigen Haltefrist liegt
    /// (Veräußerung mehr als ein Jahr nach Zufluss).
    /// </summary>
    [JsonIgnore]
    public DateTime TaxFreeFrom => ReceivedAtLocal.Date.AddYears(1).AddDays(1);

    private decimal _remaining;
    /// <summary>Noch nicht verkaufte Menge nach FIFO-Zuordnung.</summary>
    [JsonIgnore]
    public decimal Remaining
    {
        get => _remaining;
        set { _remaining = value; Raise(); Raise(nameof(HoldingStatus)); }
    }

    [JsonIgnore]
    public string HoldingStatus
    {
        get
        {
            if (Remaining <= 0) return "verkauft";
            var days = (TaxFreeFrom - DateTime.Now.Date).Days;
            if (days <= 0) return Remaining < Amount ? "Rest haltefristfrei" : "haltefristfrei";
            return $"noch {days} Tage";
        }
    }

    /// <summary>Nach Tageswechsel neu berechnen lassen.</summary>
    public void RefreshStatus() => Raise(nameof(HoldingStatus));

    [JsonIgnore]
    public string CoinSymbol => Coin.Symbol();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
