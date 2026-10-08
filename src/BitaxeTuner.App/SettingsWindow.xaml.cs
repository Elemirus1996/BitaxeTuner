using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using Microsoft.Win32;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App;

/// <summary>
/// Das eine Einstellungsfenster der zusammengeführten App (aus BitaxeMonitor, ergänzt um Design,
/// Tuning-Optionen, Autostart und Datenordner-Umzug).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ObservableCollection<DeviceConfig> _devices = new();
    private DeviceConfig? _current;
    private readonly string _dataDirectory;
    private readonly Func<string, Task<DataDirectoryMigrator.Result>>? _moveDataDirectory;
    private readonly Func<Task<string?>>? _sendReportNow;

    public SettingsWindow(AppConfig config, string dataDirectory,
                          Func<string, Task<DataDirectoryMigrator.Result>>? moveDataDirectory = null,
                          Func<Task<string?>>? sendReportNow = null,
                          AppHost? host = null, string? section = null)
    {
        InitializeComponent();
        _host = host;
        Loaded += (_, _) =>
        {
            BuildToc();
            if (section is not null) ScrollToSection(section);
        };
        _config = config;
        _dataDirectory = dataDirectory;
        _moveDataDirectory = moveDataDirectory;
        _sendReportNow = sendReportNow;

        // Auf Kopien arbeiten, damit "Abbrechen" wirklich abbricht
        foreach (var d in config.Devices)
            _devices.Add(d.Clone());

        DeviceList.ItemsSource = _devices;
        if (_devices.Count > 0) DeviceList.SelectedIndex = 0;

        PriceBox.Text = config.ElectricityCtPerKwh.ToString("0.##", CultureInfo.CurrentCulture);
        SelectByTag(PriceNetBox, config.ElectricityPriceIsNet ? "net" : "gross");
        VatBox.Text = config.VatPercent.ToString("0.##", CultureInfo.CurrentCulture);
        foreach (var cur in Currencies.All)
            CurrencyBox.Items.Add(new ComboBoxItem { Content = $"{L.T(cur.Name)} ({cur.Symbol})", Tag = cur.Code });
        SelectByTag(CurrencyBox, Currencies.Of(config).Code);
        IntervalBox.Text = config.IntervalSeconds.ToString();
        HistoryBox.Text = config.HistoryMinutes.ToString();
        WalletIntervalBox.Text = config.WalletPollMinutes.ToString();
        MinimizedBox.IsChecked = config.StartMinimized;
        TaxIntervalBox.Text = config.TaxPollMinutes.ToString();
        BlockchairKeyBox.Text = config.BlockchairApiKey;

        TempWarnBox.Text = config.TempWarn.ToString("0.#", CultureInfo.CurrentCulture);
        HistoryDaysBox.Text = config.HistoryDays.ToString();
        MinerLogHoursBox.Text = config.MinerLogKeepHours.ToString();
        TrayBox.IsChecked = config.MinimizeToTray;
        WatchdogBox.IsChecked = config.Watchdog.Enabled;
        WatchdogMinutesBox.Text = config.Watchdog.ZeroHashMinutes.ToString();
        WatchdogCooldownBox.Text = config.Watchdog.CooldownMinutes.ToString();
        CoinGeckoKeyBox.Text = config.CoinGeckoApiKey;

        _notify = config.Notifications.ForEditing();
        _pushPanel = new Views.PushTargetsPanel(_notify.Targets,
            _devices.Select(d => (string.IsNullOrWhiteSpace(d.Name) ? d.Host : d.Name, d.Host)).ToList(), MinerGroups.All(_devices));
        PushHost.Content = _pushPanel;
        if (host is not null)
        {
            _plugsPanel = new Views.SmartPlugsPanel(host);
            PlugsHost.Content = _plugsPanel;
        }
        else PlugsCard.Visibility = Visibility.Collapsed;

        DeviceHint.Text = L.T("Host kann ein mDNS-Name (bitaxe.local) oder eine feste IP sein. ") +
                          L.T("Feste IP ist zuverlässiger.");

        SelectByTag(ThemeBox, config.Theme);
        SelectByTag(LanguageBox, config.Language);
        RestartAfterApplyBox.IsChecked = config.RestartAfterApply;
        WalletConsentBox.IsChecked = config.WalletLookupConsent == true;
        UpdateCheckBox.IsChecked = config.CheckForUpdates;
        AutostartBox.IsChecked = AutostartService.IsEnabled;
        DataDirText.Text = dataDirectory;
        BackupEnabledBox.IsChecked = config.Backup.Enabled;
        BackupHourBox.Text = config.Backup.Hour.ToString();
        BackupFolderEnabledBox.IsChecked = config.Backup.Folder.Enabled;
        BackupFolderBox.Text = config.Backup.Folder.Path;
        BackupKeepBox.Text = config.Backup.Keep.ToString();
        BackupNowButton.IsEnabled = BackupRestoreButton.IsEnabled = host is not null;
        ShowBackupStatus();

        LogErrorsBox.IsChecked = config.LogAlerts.OnErrors;
        LogCooldownBox.Text = config.LogAlerts.CooldownMinutes.ToString();
        LogPatternsBox.Text = string.Join(Environment.NewLine, config.LogAlerts.Patterns);
        PoolEnabledBox.IsChecked = config.PoolWatch.Enabled;
        PoolRejectBox.Text = config.PoolWatch.RejectPercent.ToString("0.##", CultureInfo.CurrentCulture);
        PoolWindowBox.Text = config.PoolWatch.WindowMinutes.ToString();
        PoolResponseBox.Text = config.PoolWatch.ResponseMs.ToString("0", CultureInfo.CurrentCulture);
        ReportEnabledBox.IsChecked = config.DailyReport.Enabled;
        MonthlyReportBox.IsChecked = config.DailyReport.Monthly;
        ReportHourBox.Text = config.DailyReport.Hour.ToString();
        ReportNowButton.IsEnabled = sendReportNow is not null;

        SelectByTag(PriceSourceBox, config.PriceSource.Source);
        TibberTokenBox.Password = config.PriceSource.TibberToken;
        DynamicCostsBox.IsChecked = config.PriceSource.DynamicCosts;
        SurchargeBox.Text = config.PriceSource.SurchargeCt.ToString("0.##", CultureInfo.CurrentCulture);
        UpdatePricePanels();
        WebEnabledBox.IsChecked = config.WebView.Enabled;
        WebPortBox.Text = config.WebView.Port.ToString();
        WebUrlText.Text = config.WebView.Enabled && config.WebView.PinHash.Length > 0
            ? L.T("Adresse fürs Handy: ") + string.Join("  oder  ", Core.Web.WebViewServer.LocalUrls(config.WebView.Port)) +
              L.T(" · Wand-Tablet: Adresse mit „?kiosk=1“ öffnen (große Anzeige).")
            : config.WebView.PinHash.Length > 0 ? L.T("PIN ist gesetzt.") : L.T("Noch keine PIN gesetzt.");
        if (config.WebView.PinIsLegacy)
            WebUrlText.Text += L.T(" Bitte die PIN einmal neu setzen (mind. {0} Ziffern) – sie wird dann sicherer gespeichert.", WebViewSettings.MinPinLength);
        MoveDataDirButton.IsEnabled = moveDataDirectory is not null;
    }

    // ---------- Geraete ----------

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StoreCurrent();

        _current = DeviceList.SelectedItem as DeviceConfig;
        NameBox.Text = _current?.Name ?? "";
        HostBox.Text = _current?.Host ?? "";
        WalletBox.Text = _current?.WalletAddress ?? "";
        SelectByTag(CoinBox, _current?.Coin ?? "Auto");
        RepoBox.Text = _current?.FirmwareRepo ?? "";
        GroupsBox.Text = _current is null ? "" : string.Join(", ", _current.Groups);
        DeviceLogAlertsBox.IsChecked = _current?.LogAlerts == true;
        DeviceLogArchiveBox.IsChecked = _current?.LogArchive == true;

        var enabled = _current is not null;
        NameBox.IsEnabled = HostBox.IsEnabled = WalletBox.IsEnabled = enabled;
        CoinBox.IsEnabled = RepoBox.IsEnabled = DeviceLogAlertsBox.IsEnabled = DeviceLogArchiveBox.IsEnabled = GroupsBox.IsEnabled = enabled;
    }

    /// <summary>Eingaben in das aktuell gewählte Gerät zurückschreiben.</summary>
    private void StoreCurrent()
    {
        if (_current is null) return;

        var name = NameBox.Text.Trim();
        _current.Name = name.Length > 0 ? name : (HostBox.Text.Trim().Length > 0 ? HostBox.Text.Trim() : L.T("Miner"));
        _current.Host = HostBox.Text.Trim();
        _current.WalletAddress = WalletBox.Text.Trim();
        _current.Coin = SelectedTag(CoinBox) ?? "Auto";
        _current.FirmwareRepo = RepoBox.Text.Trim();
        _current.Groups = MinerGroups.Normalize(GroupsBox.Text.Split(','));
        _current.LogAlerts = DeviceLogAlertsBox.IsChecked == true;
        _current.LogArchive = DeviceLogArchiveBox.IsChecked == true;

        DeviceList.Items.Refresh();
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        StoreCurrent();

        var device = new DeviceConfig { Name = L.T("Neuer Miner"), Host = "" };
        _devices.Add(device);
        DeviceList.SelectedItem = device;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        if (MessageBox.Show(this, L.T("\"{0}\" löschen?", _current.Name), L.T("Miner löschen"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var device = _current;
        _current = null;
        _devices.Remove(device);

        if (_devices.Count > 0) DeviceList.SelectedIndex = 0;
        else DeviceList_SelectionChanged(this, null!);
    }

    // ---------- Speichern ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StoreCurrent();
        ErrorText.Text = "";

        if (_devices.Any(d => d.Host.Length == 0))
        {
            ErrorText.Text = L.T("Bei jedem Miner Host oder IP eintragen.");
            return;
        }
        if (_devices.Select(d => d.Host.ToLowerInvariant()).Distinct().Count() != _devices.Count)
        {
            ErrorText.Text = L.T("Doppelter Host in der Liste.");
            return;
        }
        if (!TryParseNumber(PriceBox.Text, out var price) || price < 0)
        {
            ErrorText.Text = L.T("Strompreis ungültig.");
            return;
        }
        if (!TryParseNumber(VatBox.Text, out var vat) || vat is < 0 or > 50)
        {
            ErrorText.Text = L.T("MwSt.: Zahl zwischen 0 und 50 %.");
            return;
        }
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 1 || interval > 300)
        {
            ErrorText.Text = L.T("Abfrageintervall: 1 bis 300 Sekunden.");
            return;
        }
        if (!int.TryParse(HistoryBox.Text, out var history) || history < 1 || history > 1440)
        {
            ErrorText.Text = L.T("Verlauf: 1 bis 1440 Minuten.");
            return;
        }
        if (!int.TryParse(WalletIntervalBox.Text, out var walletInterval) || walletInterval < 1 || walletInterval > 1440)
        {
            ErrorText.Text = L.T("Wallet-Abfrage: 1 bis 1440 Minuten.");
            return;
        }

        if (!int.TryParse(TaxIntervalBox.Text, out var taxInterval) || taxInterval < 5 || taxInterval > 1440)
        {
            ErrorText.Text = L.T("Steuer-Abfrage: 5 bis 1440 Minuten.");
            return;
        }

        if (!TryParseNumber(TempWarnBox.Text, out var tempWarn) || tempWarn < 40 || tempWarn > 100)
        {
            ErrorText.Text = L.T("Temperatur-Warnung: 40 bis 100 °C.");
            return;
        }
        if (!int.TryParse(HistoryDaysBox.Text, out var historyDays) || historyDays < 1 || historyDays > 3650)
        {
            ErrorText.Text = L.T("Verlauf aufbewahren: 1 bis 3650 Tage.");
            return;
        }
        if (!int.TryParse(MinerLogHoursBox.Text, out var minerLogHours) || minerLogHours < 1 || minerLogHours > 168)
        {
            ErrorText.Text = L.T("Miner-Logs aufbewahren: 1 bis 168 Stunden.");
            return;
        }
        if (!int.TryParse(WatchdogMinutesBox.Text, out var watchdogMinutes) || watchdogMinutes < 3 || watchdogMinutes > 240)
        {
            ErrorText.Text = L.T("Watchdog: 3 bis 240 Minuten.");
            return;
        }
        if (!int.TryParse(WatchdogCooldownBox.Text, out var watchdogCooldown) || watchdogCooldown < 10 || watchdogCooldown > 1440)
        {
            ErrorText.Text = L.T("Watchdog-Sperrzeit: 10 bis 1440 Minuten.");
            return;
        }

        if (!int.TryParse(LogCooldownBox.Text, out var logCooldown) || logCooldown < 1 || logCooldown > 1440)
        {
            ErrorText.Text = L.T("Log-Alarm-Sperrzeit: 1 bis 1440 Minuten.");
            return;
        }
        var patterns = LogPatternsBox.Text.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var invalid = new LogAlertRules(new LogAlertSettings { Patterns = patterns }).InvalidPatterns;
        if (invalid.Count > 0)
        {
            ErrorText.Text = L.T("Ungültiges Log-Muster: ") + invalid[0];
            return;
        }
        if (!TryParseNumber(PoolRejectBox.Text, out var poolReject) || poolReject <= 0 || poolReject > 100)
        {
            ErrorText.Text = L.T("Max. abgelehnt: 0 bis 100 %.");
            return;
        }
        if (!int.TryParse(PoolWindowBox.Text, out var poolWindow) || poolWindow < 5 || poolWindow > 1440)
        {
            ErrorText.Text = L.T("Pool-Zeitfenster: 5 bis 1440 Minuten.");
            return;
        }
        if (!TryParseNumber(PoolResponseBox.Text, out var poolResponse) || poolResponse < 0 || poolResponse > 60000)
        {
            ErrorText.Text = L.T("Max. Antwortzeit: 0 bis 60000 ms.");
            return;
        }
        if (!int.TryParse(ReportHourBox.Text, out var reportHour) || reportHour < 0 || reportHour > 23)
        {
            ErrorText.Text = L.T("Tagesbericht: Stunde 0 bis 23.");
            return;
        }

        if (ReadBackup() is not { } backup)
            return;

        if (!int.TryParse(WebPortBox.Text, out var webPort) || webPort < 1024 || webPort > 65535)
        {
            ErrorText.Text = L.T("Port der Handy-Ansicht: 1024 bis 65535.");
            return;
        }
        var newPin = WebPinBox.Password.Trim();
        if (newPin.Length > 0 && WebViewSettings.ValidateNewPin(newPin) is { } pinError)
        {
            ErrorText.Text = pinError;
            return;
        }
        if (WebEnabledBox.IsChecked == true && newPin.Length == 0 && _config.WebView.PinHash.Length == 0)
        {
            ErrorText.Text = L.T("Für die Handy-Ansicht zuerst eine PIN festlegen.");
            return;
        }
        var priceSource = SelectedTag(PriceSourceBox) ?? "none";
        if (priceSource == "tibber" && TibberTokenBox.Password.Trim().Length == 0)
        {
            ErrorText.Text = L.T("Für Tibber den API-Token eintragen.");
            return;
        }

        if (!double.TryParse(SurchargeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var surcharge) || surcharge is < 0 or > 200)
        {
            ErrorText.Text = L.T("Aufschlag: Zahl zwischen 0 und 200 ct/kWh.");
            return;
        }
        _config.PriceSource = new PriceSourceSettings
        {
            Source = priceSource, TibberToken = TibberTokenBox.Password.Trim(),
            DynamicCosts = DynamicCostsBox.IsChecked == true, SurchargeCt = surcharge,
        };
        var web = _config.WebView.Clone();
        web.Enabled = WebEnabledBox.IsChecked == true;
        web.Port = webPort;
        if (newPin.Length > 0) web.PinHash = WebViewSettings.HashPin(newPin);
        _config.WebView = web;

        _config.LogAlerts = new LogAlertSettings { OnErrors = LogErrorsBox.IsChecked == true, Patterns = patterns, CooldownMinutes = logCooldown };
        var pool = _config.PoolWatch.Clone();
        pool.Enabled = PoolEnabledBox.IsChecked == true;
        pool.RejectPercent = poolReject;
        pool.WindowMinutes = poolWindow;
        pool.ResponseMs = poolResponse;
        _config.PoolWatch = pool;
        var report = _config.DailyReport.Clone();
        report.Enabled = ReportEnabledBox.IsChecked == true;
        report.Monthly = MonthlyReportBox.IsChecked == true;
        report.Hour = reportHour;
        _config.DailyReport = report;

        _config.Devices = _devices.ToList();
        _config.TaxPollMinutes = taxInterval;
        _config.BlockchairApiKey = BlockchairKeyBox.Text.Trim();
        _config.TempWarn = tempWarn;
        _config.HistoryDays = historyDays;
        _config.MinerLogKeepHours = minerLogHours;
        _config.MinimizeToTray = TrayBox.IsChecked == true;
        _config.Watchdog = new WatchdogSettings
        {
            Enabled = WatchdogBox.IsChecked == true,
            ZeroHashMinutes = watchdogMinutes,
            CooldownMinutes = watchdogCooldown
        };
        _config.CoinGeckoApiKey = CoinGeckoKeyBox.Text.Trim();
        if (_plugsPanel?.Validate() is { } plugError)
        {
            ErrorText.Text = plugError;
            ScrollToSection(L.T("Smart Plugs"));
            return;
        }
        _plugsPanel?.Apply(_config);
        _notify.Targets = _pushPanel.Targets;
        _notify.ApplyTargets(_config.Devices.Select(d => d.Host));
        _config.Notifications = _notify;
        _config.ElectricityCtPerKwh = price;
        _config.ElectricityPriceIsNet = SelectedTag(PriceNetBox) == "net";
        _config.VatPercent = vat;
        if (SelectedTag(CurrencyBox) is { } code && code != Currencies.Of(_config).Code) Currencies.Set(_config, code);
        _config.IntervalSeconds = interval;
        _config.HistoryMinutes = history;
        _config.WalletPollMinutes = walletInterval;
        _config.StartMinimized = MinimizedBox.IsChecked == true;
        _config.Theme = SelectedTag(ThemeBox) ?? "dark";
        _config.Language = SelectedTag(LanguageBox) ?? "auto";
        _config.RestartAfterApply = RestartAfterApplyBox.IsChecked == true;
        // nur speichern, wenn geändert – „noch nicht gefragt“ bleibt sonst erhalten
        if ((_config.WalletLookupConsent == true) != (WalletConsentBox.IsChecked == true)) _config.WalletLookupConsent = WalletConsentBox.IsChecked == true;
        _config.CheckForUpdates = UpdateCheckBox.IsChecked == true;
        _config.Backup = backup;
        _config.Save();

        ApplyAutostart(AutostartBox.IsChecked == true);

        DialogResult = true;
    }

    // ---------- Sicherung ----------

    /// <summary>Sicherungs-Einstellungen aus den Feldern (geprüft); null mit Fehlertext bei ungültigen Werten.</summary>
    private BackupSettings? ReadBackup()
    {
        if (!int.TryParse(BackupHourBox.Text, out var hour) || hour is < 0 or > 23)
        {
            ErrorText.Text = L.T("Sicherung: Stunde 0 bis 23.");
            ScrollToSection(L.T("Sicherung"));
            return null;
        }
        if (!int.TryParse(BackupKeepBox.Text, out var keep) || keep is < 1 or > 365)
        {
            ErrorText.Text = L.T("Sicherung: 1 bis 365 Sicherungen behalten.");
            ScrollToSection(L.T("Sicherung"));
            return null;
        }
        var folder = BackupFolderBox.Text.Trim();
        if (BackupFolderEnabledBox.IsChecked == true && !System.IO.Path.IsPathFullyQualified(folder))
        {
            ErrorText.Text = L.T("Sicherung: vollständigen Zielordner angeben (z. B. E:\\BitaxeTuner oder \\\\nas\\backup).");
            ScrollToSection(L.T("Sicherung"));
            return null;
        }
        var b = _config.Backup;
        return new BackupSettings
        {
            Enabled = BackupEnabledBox.IsChecked == true,
            Hour = hour,
            LocalKeep = b.LocalKeep,
            Keep = keep,
            Folder = new BackupFolderTarget { Enabled = BackupFolderEnabledBox.IsChecked == true, Path = folder },
            Smb = b.Smb,
            LastRun = b.LastRun,
        };
    }

    private void ShowBackupStatus()
    {
        if (_host is null) { BackupStatusText.Text = ""; return; }
        var st = _host.Hub.BackupStatus;
        BackupStatusText.Text = st.LastRun is { } last
            ? L.T("Letzte Sicherung: {0:g}{1}", last, st.LastOk == true ? " ✓" : L.T(" – mit Fehlern")) +
              string.Concat(st.Targets.Select(t => $"\n{(t.Ok ? "✓" : "✗")} {t.Target}: {t.Message}"))
            : L.T("Noch keine Sicherung erstellt.");
        if (st.LastOk == false) BackupStatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
        else BackupStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextBrush");
    }

    private void BackupBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Zielordner für Sicherungen"), InitialDirectory = BackupFolderBox.Text };
        if (dlg.ShowDialog(this) != true) return;
        BackupFolderBox.Text = dlg.FolderName;
        BackupFolderEnabledBox.IsChecked = true;
    }

    /// <summary>Übernimmt die Sicherungs-Einstellungen sofort und sichert.</summary>
    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || ReadBackup() is not { } backup) return;
        ErrorText.Text = "";
        _config.Backup = backup;
        _config.Save();
        BackupNowButton.IsEnabled = false;
        BackupStatusText.Text = L.T("Sicherung läuft …");
        try { await _host.Hub.RunBackupAsync(DateTime.Now); }
        finally { BackupNowButton.IsEnabled = true; }
        ShowBackupStatus();
    }

    /// <summary>Zweiter Ordner, wenn eingeschaltet und vorhanden; sonst auto-backups im Datenordner.</summary>
    private string BackupFolderToShow()
    {
        var folder = BackupFolderBox.Text.Trim();
        return BackupFolderEnabledBox.IsChecked == true && System.IO.Path.IsPathFullyQualified(folder) && System.IO.Directory.Exists(folder)
            ? folder
            : System.IO.Path.Combine(_dataDirectory, "auto-backups");
    }

    private void BackupOpen_Click(object sender, RoutedEventArgs e) => Services.BackupActions.OpenFolder(this, BackupFolderToShow());

    private async void BackupRestore_Click(object sender, RoutedEventArgs e) =>
        await Services.BackupActions.RestoreLocalAsync(this, _dataDirectory, BackupFolderToShow());

    // ---------- Strompreis ----------

    private void PriceSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePricePanels();

    private void UpdatePricePanels()
    {
        if (TibberPanel is null) return;
        TibberPanel.Visibility = SelectedTag(PriceSourceBox) == "tibber" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Tagesbericht ----------

    private async void ReportNow_Click(object sender, RoutedEventArgs e)
    {
        if (_sendReportNow is null) return;
        ReportResultText.Text = L.T("Sende …");
        ReportNowButton.IsEnabled = false;
        var error = await _sendReportNow();
        ReportNowButton.IsEnabled = true;
        ReportResultText.Text = error ?? L.T("Gesendet – kam er an? (mit den gespeicherten Push-Einstellungen)");
    }

    // ---------- Autostart ----------

    private void ApplyAutostart(bool enable)
    {
        try
        {
            if (!enable)
            {
                AutostartService.Disable();
                return;
            }
            if (AutostartService.IsEnabled) return;
            AutostartService.Enable();

            if (AutostartService.OldMonitorLinkExists &&
                MessageBox.Show(this,
                    L.T("Im Autostart liegt noch eine Verknüpfung auf den alten BitaxeMonitor.\n\n") +
                    L.T("Beide Programme würden sonst gleichzeitig starten und die Miner doppelt abfragen. Alte Verknüpfung entfernen?"),
                    "Autostart", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                AutostartService.RemoveOldMonitorLink();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Autostart konnte nicht geändert werden: ") + ex.Message, "Autostart",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Datenordner ----------

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true });

    private async void MoveDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (_moveDataDirectory is null) return;

        var dialog = new OpenFolderDialog { Title = L.T("Neuen Datenordner wählen (leer oder neu)") };
        if (dialog.ShowDialog(this) != true) return;

        if (MessageBox.Show(this,
                L.T("Daten von\n{0}\nnach\n{1}\nkopieren und danach dort weiterarbeiten?\n\n", _dataDirectory, dialog.FolderName) +
                L.T("Der alte Ordner bleibt unverändert erhalten. Die Abfragen pausieren während des Umzugs, ") +
                L.T("anschließend startet die App neu. Nicht gespeicherte Änderungen in diesem Fenster gehen verloren."),
                L.T("Datenordner umziehen"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        IsEnabled = false;
        var result = await _moveDataDirectory(dialog.FolderName);
        IsEnabled = true;

        MessageBox.Show(this, result.Message + "\n\n" + string.Join("\n", result.Log.TakeLast(12)),
            L.T("Datenordner umziehen"), MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        if (result.Success)
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------- Benachrichtigungen ----------

    /// <summary>Arbeitskopie der Push-Ziele; übernommen erst mit „Speichern“.</summary>
    private NotificationSettings _notify = new();
    private readonly Views.PushTargetsPanel _pushPanel;
    private readonly Views.SmartPlugsPanel? _plugsPanel;
    private readonly AppHost? _host;

    private async void TestNotify_Click(object sender, RoutedEventArgs e)
    {
        var settings = new NotificationSettings { Targets = _pushPanel.Targets };
        if (!settings.Targets.Any(t => t.Enabled))
        {
            TestResultText.Text = L.T("Erst einen Dienst auswählen.");
            return;
        }
        TestResultText.Text = L.T("Sende …");
        using var service = new NotificationService(() => settings);
        var error = await service.TestAsync(settings);
        TestResultText.Text = error is null ? L.T("Gesendet – kam sie an?") : L.T("Fehler: ") + error;
    }

    // ---------- Einführung ----------

    /// <summary>Nach dem Schließen die Einführung öffnen (Knopf unter „Programm und Tuning“).</summary>
    public bool ShowOnboardingRequested { get; private set; }

    private void ShowOnboarding_Click(object sender, RoutedEventArgs e)
    {
        Onboarding.SetVisible(_config, true);
        ShowOnboardingRequested = true;
        Save_Click(sender, e); // speichert und schließt – bei Eingabefehlern bleibt das Fenster offen
    }

    // ---------- Inhaltsverzeichnis ----------

    private readonly List<TextBlock> _sections = [];
    private bool _tocScrolling;

    /// <summary>Alle Abschnittsüberschriften (Stil „Section“) sichtbarer Karten als Einträge links.</summary>
    private void BuildToc()
    {
        _sections.Clear();
        var style = (Style)FindResource("Section");
        void Walk(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                if (child is FrameworkElement { Visibility: not Visibility.Visible }) continue;
                if (child is TextBlock tb && tb.Style == style) _sections.Add(tb);
                Walk(child);
            }
        }
        Walk(SettingsContent);
        TocList.ItemsSource = _sections.Select(t => t.Text).ToList();
    }

    private double OffsetOf(FrameworkElement element) =>
        element.TransformToAncestor(SettingsContent).Transform(new Point(0, 0)).Y;

    private void ScrollToSection(string title)
    {
        var index = _sections.FindIndex(t => string.Equals(t.Text, title, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) TocList.SelectedIndex = index;
    }

    private void TocList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tocScrolling || TocList.SelectedIndex < 0 || TocList.SelectedIndex >= _sections.Count) return;
        SettingsScroll.ScrollToVerticalOffset(Math.Max(0, OffsetOf(_sections[TocList.SelectedIndex]) - 18));
    }

    /// <summary>Beim Scrollen den aktuellen Abschnitt markieren (ohne selbst zu scrollen).</summary>
    private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_sections.Count == 0) return;
        var top = SettingsScroll.VerticalOffset + 40;
        var current = 0;
        for (var i = 0; i < _sections.Count; i++)
            if (OffsetOf(_sections[i]) <= top) current = i;
        if (SettingsScroll.VerticalOffset + SettingsScroll.ViewportHeight >= SettingsScroll.ExtentHeight - 2) current = _sections.Count - 1;
        if (TocList.SelectedIndex == current) return;
        _tocScrolling = true;
        TocList.SelectedIndex = current;
        _tocScrolling = false;
    }

    // ---------- Hilfen ----------

    private static void SelectByTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>Akzeptiert Komma und Punkt als Dezimaltrenner.</summary>
    private static bool TryParseNumber(string text, out double value)
    {
        text = text.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
