using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.I18n;

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
        set { _eurPriceAtReceipt = value; Raise(); Raise(nameof(EurValue)); Raise(nameof(ViewPrice)); Raise(nameof(ViewValue)); }
    }

    /// <summary>Zeitpunkt des verwendeten Kurspunkts (UTC), null bei manueller Eingabe.</summary>
    public DateTime? PriceAtUtc { get; set; }

    private string _priceSource = string.Empty;
    /// <summary>Herkunft des Kurses, z. B. "CoinGecko Zeitreihe …" oder "manuell".</summary>
    public string PriceSource
    {
        get => _priceSource;
        set { _priceSource = value; Raise(); Raise(nameof(ViewSource)); }
    }

    /// <summary>Kurs wurde von Hand eingetragen und wird nicht automatisch überschrieben.</summary>
    [JsonIgnore]
    public bool IsManualPrice => PriceSource.StartsWith("manuell", StringComparison.OrdinalIgnoreCase)
                                 || PriceSource.StartsWith("manual", StringComparison.OrdinalIgnoreCase);   // englische Oberfläche

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set { _note = value; Raise(); }
    }

    [JsonIgnore]
    public decimal? EurValue => EurPriceAtReceipt.HasValue ? Amount * EurPriceAtReceipt.Value : null;

    /// <summary>
    /// 0.9.11: Kurse in weiteren Währungen (ISO-Code → Kurs). Der Euro-Kurs bleibt in <see cref="EurPriceAtReceipt"/>,
    /// damit vorhandene Daten und das deutsche Steuerrecht unverändert gelten.
    /// </summary>
    public Dictionary<string, FiatPrice>? OtherPrices { get; set; }

    private static bool IsEur(string? currency) => string.IsNullOrEmpty(currency) || currency.Equals("EUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kurs je Coin in der Währung, oder null.</summary>
    public decimal? PriceIn(string? currency) =>
        IsEur(currency) ? EurPriceAtReceipt : OtherPrices?.GetValueOrDefault(currency!.ToUpperInvariant())?.Price;

    /// <summary>Wert des Zuflusses in der Währung, oder null.</summary>
    public decimal? ValueIn(string? currency) => PriceIn(currency) is { } p ? Amount * p : null;

    /// <summary>Herkunft des Kurses in der Währung.</summary>
    public string SourceIn(string? currency) =>
        IsEur(currency) ? PriceSource : OtherPrices?.GetValueOrDefault(currency!.ToUpperInvariant())?.Source ?? "";

    /// <summary>Kurs in dieser Währung wurde von Hand eingetragen und wird nicht automatisch überschrieben.</summary>
    public bool IsManualIn(string? currency) =>
        IsEur(currency) ? IsManualPrice : FiatPrice.IsManualSource(SourceIn(currency));

    private string _viewCurrency = "EUR";
    /// <summary>Desktop-Tabelle: Währung, in der <see cref="ViewPrice"/> und <see cref="ViewValue"/> angezeigt werden.</summary>
    [JsonIgnore]
    public string ViewCurrency
    {
        get => _viewCurrency;
        set { if (_viewCurrency == value) return; _viewCurrency = value; Raise(); Raise(nameof(ViewPrice)); Raise(nameof(ViewValue)); Raise(nameof(ViewSource)); }
    }

    /// <summary>Kurs in <see cref="ViewCurrency"/> (in der Tabelle editierbar; als manuell markiert wird beim Speichern).</summary>
    [JsonIgnore]
    public decimal? ViewPrice
    {
        get => PriceIn(ViewCurrency);
        set { if (value != PriceIn(ViewCurrency)) SetPrice(ViewCurrency, value, null, SourceIn(ViewCurrency)); }
    }

    [JsonIgnore]
    public decimal? ViewValue => ValueIn(ViewCurrency);

    [JsonIgnore]
    public string ViewSource => SourceIn(ViewCurrency);

    /// <summary>Kurs in der Währung setzen (null = entfernen).</summary>
    public void SetPrice(string? currency, decimal? price, DateTime? atUtc, string source)
    {
        if (IsEur(currency))
        {
            EurPriceAtReceipt = price;
            PriceAtUtc = atUtc;
            PriceSource = source;
            return;
        }
        var code = currency!.ToUpperInvariant();
        if (price is null) OtherPrices?.Remove(code);
        else (OtherPrices ??= [])[code] = new FiatPrice { Price = price.Value, AtUtc = atUtc, Source = source };
        Raise(nameof(OtherPrices));
        Raise(nameof(ViewPrice));
        Raise(nameof(ViewValue));
        Raise(nameof(ViewSource));
    }

    [JsonIgnore]
    public DateTime ReceivedAtLocal => TaxTime.ToTax(ReceivedAtUtc);   // Steuer-Zeitzone (Audit F5)

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
            var days = (TaxFreeFrom - TaxTime.ToTax(DateTime.UtcNow).Date).Days;   // Audit F5: Steuerzeit, nicht Rechnerzeit
            if (days <= 0) return Remaining < Amount ? L.T("Rest haltefristfrei") : L.T("haltefristfrei");
            return L.T("noch {0} Tage", days);
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
