using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// Änderbare Einstellungen im Browser. Interne Felder (gemeldete Versionen, gesehene Auszahlungen, Migrationsstände)
/// und die Geräteliste (eigene Endpunkte) werden bewusst nicht überschrieben.
/// </summary>
public sealed class SettingsDto
{
    public int IntervalSeconds { get; set; }
    public int HistoryMinutes { get; set; }
    public int HistoryDays { get; set; }
    public int WalletPollMinutes { get; set; }
    public int TaxPollMinutes { get; set; }
    public double ElectricityCtPerKwh { get; set; }
    public bool ElectricityPriceIsNet { get; set; }
    public double VatPercent { get; set; } = 19;
    public string Currency { get; set; } = "€";
    public double TempWarn { get; set; }
    public bool RestartAfterApply { get; set; }
    public bool CheckForUpdates { get; set; }
    /// <summary>Sprache des Servers („auto“, „de“, „en“): Push, Tagesbericht, E-Paper, Statustexte.</summary>
    public string Language { get; set; } = "auto";
    public string BlockchairApiKey { get; set; } = "";
    public string CoinGeckoApiKey { get; set; } = "";
    public NotificationSettings Notifications { get; set; } = new();
    public WatchdogSettings Watchdog { get; set; } = new();
    public LogAlertSettings LogAlerts { get; set; } = new();
    public PoolWatchSettings PoolWatch { get; set; } = new();
    public DailyReportSettings DailyReport { get; set; } = new();
    public PriceSourceSettings PriceSource { get; set; } = new();

    /// <summary>Ist eine PIN für die Rolle „Nur ansehen“ gesetzt? (Die PIN selbst wird nie ausgeliefert.)</summary>
    public bool ViewerPinSet { get; set; }

    /// <summary>Neue PIN für „Nur ansehen“ (leer = unverändert, "-" = entfernen).</summary>
    public string? NewViewerPin { get; set; }

    public static SettingsDto From(AppConfig c) => new()
    {
        IntervalSeconds = c.IntervalSeconds,
        HistoryMinutes = c.HistoryMinutes,
        HistoryDays = c.HistoryDays,
        WalletPollMinutes = c.WalletPollMinutes,
        TaxPollMinutes = c.TaxPollMinutes,
        ElectricityCtPerKwh = c.ElectricityCtPerKwh,
        ElectricityPriceIsNet = c.ElectricityPriceIsNet,
        VatPercent = c.VatPercent,
        Currency = c.Currency,
        TempWarn = c.TempWarn,
        RestartAfterApply = c.RestartAfterApply,
        CheckForUpdates = c.CheckForUpdates,
        Language = c.Language,
        BlockchairApiKey = c.BlockchairApiKey,
        CoinGeckoApiKey = c.CoinGeckoApiKey,
        Notifications = c.Notifications,
        Watchdog = c.Watchdog,
        LogAlerts = c.LogAlerts,
        PoolWatch = c.PoolWatch,
        DailyReport = c.DailyReport,
        PriceSource = c.PriceSource,
        ViewerPinSet = c.WebView.PinHash.Length > 0,
    };

    public void ApplyTo(AppConfig c)
    {
        if (NewViewerPin is { Length: > 0 } pin)
        {
            if (pin == "-") c.WebView.PinHash = "";
            else if (pin.Length < 4 || !pin.All(char.IsDigit)) throw new InvalidOperationException(L.N("Die PIN braucht mindestens 4 Ziffern."));
            else c.WebView.PinHash = WebViewSettings.HashPin(pin);
        }
        c.IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 300);
        c.HistoryMinutes = Math.Clamp(HistoryMinutes, 5, 24 * 60);
        c.HistoryDays = Math.Clamp(HistoryDays, 1, 3650);
        c.WalletPollMinutes = Math.Clamp(WalletPollMinutes, 1, 1440);
        c.TaxPollMinutes = Math.Clamp(TaxPollMinutes, 1, 1440);
        c.ElectricityCtPerKwh = Math.Clamp(ElectricityCtPerKwh, 0, 500);
        c.ElectricityPriceIsNet = ElectricityPriceIsNet;
        c.VatPercent = Math.Clamp(VatPercent, 0, 50);
        c.Currency = string.IsNullOrWhiteSpace(Currency) ? "€" : Currency.Trim();
        c.TempWarn = Math.Clamp(TempWarn, 30, 120);
        c.RestartAfterApply = RestartAfterApply;
        c.CheckForUpdates = CheckForUpdates;
        c.Language = Language is "de" or "en" ? Language : "auto";
        c.BlockchairApiKey = BlockchairApiKey.Trim();
        c.CoinGeckoApiKey = CoinGeckoApiKey.Trim();
        c.Notifications = Notifications;
        c.Watchdog = Watchdog;
        c.LogAlerts = LogAlerts;
        c.PoolWatch = PoolWatch;
        // Datum des letzten Tagesberichts bleibt (sonst käme er doppelt)
        DailyReport.LastSent = c.DailyReport.LastSent;
        c.DailyReport = DailyReport;
        PriceSource.SurchargeCt = Math.Clamp(PriceSource.SurchargeCt, 0, 200);
        c.PriceSource = PriceSource;
    }
}
