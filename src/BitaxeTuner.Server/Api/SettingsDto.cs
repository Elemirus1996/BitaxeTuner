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
    public int MinerLogKeepHours { get; set; } = 48;
    public int WalletPollMinutes { get; set; }
    public int TaxPollMinutes { get; set; }
    public double ElectricityCtPerKwh { get; set; }
    public bool ElectricityPriceIsNet { get; set; }
    public double VatPercent { get; set; } = 19;
    public string Currency { get; set; } = "€";
    /// <summary>0.9.11: eine Währung für alles (ISO-Code); das Zeichen <see cref="Currency"/> folgt.</summary>
    public string? CurrencyCode { get; set; }
    /// <summary>Nur zum Anzeigen: wählbare Währungen (Code, Name, Zeichen, Untereinheit) – beim Speichern ignoriert.</summary>
    public List<CurrencyOption>? CurrencyOptions { get; set; }
    public double TempWarn { get; set; }
    public bool RestartAfterApply { get; set; }
    public bool CheckForUpdates { get; set; }
    public bool? WalletLookupConsent { get; set; }
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

    /// <summary>PIN noch im alten, ungesalzenen Format – Hinweis „neu setzen“.</summary>
    public bool ViewerPinLegacy { get; set; }

    /// <summary>Neue PIN für „Nur ansehen“ (leer = unverändert, "-" = entfernen).</summary>
    public string? NewViewerPin { get; set; }

    public static SettingsDto From(AppConfig c) => new()
    {
        IntervalSeconds = c.IntervalSeconds,
        HistoryMinutes = c.HistoryMinutes,
        HistoryDays = c.HistoryDays,
        MinerLogKeepHours = c.MinerLogKeepHours,
        WalletPollMinutes = c.WalletPollMinutes,
        TaxPollMinutes = c.TaxPollMinutes,
        ElectricityCtPerKwh = c.ElectricityCtPerKwh,
        ElectricityPriceIsNet = c.ElectricityPriceIsNet,
        VatPercent = c.VatPercent,
        Currency = c.Currency,
        CurrencyCode = Currencies.Of(c).Code,
        CurrencyOptions = Currencies.All.Select(x => new CurrencyOption(x.Code, x.Name, x.Symbol, x.Cent)).ToList(),
        TempWarn = c.TempWarn,
        RestartAfterApply = c.RestartAfterApply,
        CheckForUpdates = c.CheckForUpdates,
        WalletLookupConsent = c.WalletLookupConsent,
        Language = c.Language,
        BlockchairApiKey = c.BlockchairApiKey,
        CoinGeckoApiKey = c.CoinGeckoApiKey,
        Notifications = c.Notifications.ForEditing(),
        Watchdog = c.Watchdog,
        LogAlerts = c.LogAlerts,
        PoolWatch = c.PoolWatch,
        DailyReport = c.DailyReport,
        PriceSource = c.PriceSource,
        ViewerPinSet = c.WebView.PinHash.Length > 0,
        ViewerPinLegacy = c.WebView.PinIsLegacy,
    };

    public void ApplyTo(AppConfig c)
    {
        if (NewViewerPin is { Length: > 0 } pin)
        {
            if (pin == "-") c.WebView.PinHash = "";
            else if (WebViewSettings.ValidateNewPin(pin) is { } error) throw new InvalidOperationException(error);
            else c.WebView.PinHash = WebViewSettings.HashPin(pin);
        }
        c.IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 300);
        c.HistoryMinutes = Math.Clamp(HistoryMinutes, 5, 24 * 60);
        c.HistoryDays = Math.Clamp(HistoryDays, 1, 3650);
        c.MinerLogKeepHours = Math.Clamp(MinerLogKeepHours, 1, 168);
        c.WalletPollMinutes = Math.Clamp(WalletPollMinutes, 1, 1440);
        c.TaxPollMinutes = Math.Clamp(TaxPollMinutes, 1, 1440);
        c.ElectricityCtPerKwh = Math.Clamp(ElectricityCtPerKwh, 0, 500);
        c.ElectricityPriceIsNet = ElectricityPriceIsNet;
        c.VatPercent = Math.Clamp(VatPercent, 0, 50);
        if (Currencies.IsKnown(CurrencyCode) && !string.Equals(CurrencyCode, Currencies.Of(c).Code, StringComparison.OrdinalIgnoreCase))
            Currencies.Set(c, CurrencyCode);
        else if (!Currencies.IsKnown(CurrencyCode))
            c.Currency = string.IsNullOrWhiteSpace(Currency) ? "€" : Currency.Trim();
        c.TempWarn = Math.Clamp(TempWarn, 30, 120);
        c.RestartAfterApply = RestartAfterApply;
        c.CheckForUpdates = CheckForUpdates;
        if (WalletLookupConsent is not null) c.WalletLookupConsent = WalletLookupConsent;
        c.Language = Language is "de" or "en" ? Language : "auto";
        c.BlockchairApiKey = BlockchairApiKey.Trim();
        c.CoinGeckoApiKey = CoinGeckoApiKey.Trim();
        Notifications.ApplyTargets(c.Devices.Select(d => d.Host));
        c.Notifications = Notifications;
        c.Watchdog = Watchdog;
        c.LogAlerts = LogAlerts;
        c.PoolWatch = PoolWatch;
        // Datum des letzten Tagesberichts bleibt (sonst käme er doppelt)
        DailyReport.LastSent = c.DailyReport.LastSent;
        DailyReport.LastMonthlySent = c.DailyReport.LastMonthlySent;
        c.DailyReport = DailyReport;
        PriceSource.SurchargeCt = Math.Clamp(PriceSource.SurchargeCt, 0, 200);
        c.PriceSource = PriceSource;
    }
}

/// <summary>Wählbare Währung für die Einstellungen im Browser.</summary>
public sealed record CurrencyOption(string Code, string Name, string Symbol, string Cent);
