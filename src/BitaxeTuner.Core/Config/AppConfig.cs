using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Gemeinsame Einstellungen (config.json im Datenordner). Übernommen aus BitaxeMonitor –
/// alle bisherigen Felder und ihre JSON-Namen bleiben unverändert, neue Felder kommen nur hinzu.
/// </summary>
public sealed class AppConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();

    /// <summary>0.9.7: Zeitplan/Strompreis-Regeln für Miner-Gruppen. Additiv.</summary>
    public List<GroupScheduleRule> GroupSchedules { get; set; } = [];

    /// <summary>0.9.11: automatische Pool-Umschaltung für Miner-Gruppen. Additiv.</summary>
    public List<GroupPoolRule> GroupPoolRules { get; set; } = [];

    /// <summary>0.9.8: Zeitzone für Haltefrist und Datumsangaben im Steuer-Bereich (Audit F5). Additiv.</summary>
    public string TaxTimeZone { get; set; } = Tax.TaxTime.DefaultZone;

    /// <summary>0.9.8: Designs der Kiosk-Anzeige (Farben, Panels, Animationen). Additiv.</summary>
    public List<KioskDesign> KioskDesigns { get; set; } = [];

    public int IntervalSeconds { get; set; } = 5;
    public int HistoryMinutes { get; set; } = 60;
    public int WalletPollMinutes { get; set; } = 10;

    /// <summary>
    /// Dürfen aus dem Pool-Benutzer des Miners erkannte Wallet-Adressen bei mempool.space/Blockchair abgefragt werden?
    /// null = noch nicht gefragt (dann keine Abfrage), true/false = Antwort. Selbst eingetragene Adressen werden immer
    /// abgefragt – die sind ausdrücklich dafür eingetragen (Datenschutz, Audit P1).
    /// </summary>
    public bool? WalletLookupConsent { get; set; }

    /// <summary>Strompreis in Cent je kWh.</summary>
    public double ElectricityCtPerKwh { get; set; } = 30;

    /// <summary>0.7.0: Arbeitspreis (und Aufschlag) ist netto, also zzgl. MwSt. Standard: brutto wie bisher.</summary>
    public bool ElectricityPriceIsNet { get; set; }

    /// <summary>MwSt.-Satz in Prozent für Nettopreise und den aWATTar-Börsenpreis.</summary>
    public double VatPercent { get; set; } = 19;

    /// <summary>Intervall der Steuer-Wallet-Überwachung (Blockchair) in Minuten.</summary>
    public int TaxPollMinutes { get; set; } = 15;

    /// <summary>Optionaler Blockchair-API-Key; ohne Key gelten 1.440 Requests/Tag.</summary>
    public string BlockchairApiKey { get; set; } = "";

    /// <summary>Aufbewahrung der Verlaufsdaten in der Datenbank.</summary>
    public int HistoryDays { get; set; } = 90;

    /// <summary>0.9.11: Aufbewahrung gespeicherter Miner-Logs in Stunden (1–168, Standard 48).</summary>
    public int MinerLogKeepHours { get; set; } = 48;

    /// <summary>Warnschwelle ASIC-Temperatur für Benachrichtigungen.</summary>
    public double TempWarn { get; set; } = 70;

    /// <summary>Beim Minimieren in den Infobereich statt in die Taskleiste.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public NotificationSettings Notifications { get; set; } = new();
    public WatchdogSettings Watchdog { get; set; } = new();

    /// <summary>Bereits gemeldete Firmware-Versionen je Repository.</summary>
    public Dictionary<string, string> NotifiedFirmware { get; set; } = new();

    /// <summary>Optionaler CoinGecko-Demo-Key (kostenlos); ohne Key gilt ein niedrigeres Rate-Limit.</summary>
    public string CoinGeckoApiKey { get; set; } = "";
    public string Currency { get; set; } = "€";

    /// <summary>0.9.11: ISO-Code der Währung für alles (Kurse, Erträge, Kosten, Steuer); leer = aus <see cref="Currency"/> abgeleitet.</summary>
    public string? CurrencyCode { get; set; }
    public bool StartMinimized { get; set; } = false;

    /// <summary>Zuletzt gesehene eingehende TX je Adresse - dient der Auszahlungs-Erkennung.</summary>
    public Dictionary<string, string> LastSeenPayoutTxids { get; set; } = new();

    // --- Neu durch die Zusammenführung mit BitaxeTuner ---

    /// <summary>"dark" oder "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Übertakten-Hinweis wurde bestätigt.</summary>
    public bool WarningAccepted { get; set; }

    /// <summary>0.8.0: Einführung „Erste Schritte“ ausgeblendet bzw. abgeschlossen.</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>0.8.0: Einführung läuft (neue Installation oder wieder eingeblendet) – bleibt, bis sie ausgeblendet wird.</summary>
    public bool OnboardingStarted { get; set; }

    /// <summary>0.8.0: zuletzt gesehene Version für „Neu in dieser Version“ (null = vor 0.8.0 oder neue Installation).</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Beim Start und alle 6 h auf neue BitaxeTuner-Version prüfen.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Für diese Version wurde bereits per Push auf ein Update hingewiesen.</summary>
    public string? NotifiedAppVersion { get; set; }

    /// <summary>
    /// Zuletzt per Push gemeldete Server-Version – getrennt von <see cref="NotifiedAppVersion"/> (Desktop-App), weil nach
    /// einer Datenübertragung vom PC sonst die Meldung des Servers für dieselbe Version ausbliebe.
    /// </summary>
    public string? NotifiedServerVersion { get; set; }

    /// <summary>Nach einer Frequenz-/Spannungsänderung neu starten (Standard; Wirkung ohne Neustart ist firmwareabhängig).</summary>
    public bool RestartAfterApply { get; set; } = true;

    /// <summary>Daten aus dem eigenständigen BitaxeTuner (%LocalAppData%\BitaxeTuner) wurden übernommen.</summary>
    public bool TunerDataMigrated { get; set; }

    /// <summary>Sicherung vor der ersten Schemaänderung durch die zusammengeführte App wurde angelegt.</summary>
    public bool IntegrationBackupDone { get; set; }

    /// <summary>Push bei bestimmten Zeilen in den Miner-Logs (nur für Geräte mit <see cref="DeviceConfig.LogAlerts"/>).</summary>
    public LogAlertSettings LogAlerts { get; set; } = new();

    /// <summary>Pool- und Share-Überwachung.</summary>
    public PoolWatchSettings PoolWatch { get; set; } = new();

    /// <summary>Täglicher Bericht per Push.</summary>
    public DailyReportSettings DailyReport { get; set; } = new();

    /// <summary>Strompreis-Quelle für preisabhängige Regeln.</summary>
    public PriceSourceSettings PriceSource { get; set; } = new();

    /// <summary>Nur-Lese-Ansicht fürs Handy im Heimnetz.</summary>
    public WebViewSettings WebView { get; set; } = new();

    /// <summary>Sprache der Oberfläche und der Meldungen: „auto“ (Systemsprache), „de“ oder „en“.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Betriebsart der Desktop-App: lokal (Standard) oder mit einem BitaxeTuner-Server verbunden.</summary>
    public ServerConnectionSettings Server { get; set; } = new();

    /// <summary>Zusatzlüfter am Server (Raspberry Pi Pico per USB).</summary>
    public FanSettings Fans { get; set; } = new();

    /// <summary>E-Paper-Anzeige und Taster am Pico.</summary>
    public DisplaySettings Display { get; set; } = new();

    /// <summary>Tägliche Sicherung (Datenordner, Ordner/USB, Netzlaufwerk).</summary>
    public BackupSettings Backup { get; set; } = new();

    /// <summary>Home Assistant / MQTT.</summary>
    public MqttSettings Mqtt { get; set; } = new();

    /// <summary>Prometheus-Export unter /metrics (Server); standardmäßig aus, nur mit Token.</summary>
    public MetricsSettings Metrics { get; set; } = new();

    /// <summary>0.6.1: Smart Plugs (Shelly) für Verbrauch an der Steckdose – rein additiv.</summary>
    public SmartPlugSettings Plugs { get; set; } = new();

    // --- Altlasten aus Version 1, nur zum Migrieren ---
    public string? Host { get; set; }
    public string? WalletAddress { get; set; }
    public string? LastSeenPayoutTxid { get; set; }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load() => Load(DataPaths.ConfigFile);

    public static AppConfig Load(string filePath)
    {
        AppConfig cfg;
        try
        {
            if (File.Exists(filePath))
            {
                // Tokens stehen als Verweis in config.json, die Werte in secrets.json (Audit P2)
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(filePath));
                var missing = root is null ? [] : ConfigSecrets.Resolve(root, new SecretStore(Path.GetDirectoryName(Path.GetFullPath(filePath))!));
                cfg = root?.Deserialize<AppConfig>() ?? new AppConfig();
                cfg.UnresolvedSecrets = missing;
            }
            else cfg = new AppConfig();
        }
        catch
        {
            // Defekte Datei nicht stillschweigend ersetzen: Sicherung anlegen, dann mit Standardwerten weiter
            try
            {
                if (File.Exists(filePath))
                    File.Copy(filePath, filePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
            }
            catch { /* nicht kritisch */ }
            cfg = new AppConfig();
        }

        // Migration: alte Einzelgeraet-Konfiguration uebernehmen
        if (cfg.Devices.Count == 0 && !string.IsNullOrWhiteSpace(cfg.Host))
        {
            cfg.Devices.Add(new DeviceConfig
            {
                Name = "Bitaxe",
                Host = cfg.Host!,
                WalletAddress = cfg.WalletAddress ?? ""
            });
            if (!string.IsNullOrWhiteSpace(cfg.LastSeenPayoutTxid) && !string.IsNullOrWhiteSpace(cfg.WalletAddress))
                cfg.LastSeenPayoutTxids[cfg.WalletAddress!] = cfg.LastSeenPayoutTxid!;
        }
        cfg.Host = null;
        cfg.WalletAddress = null;
        cfg.LastSeenPayoutTxid = null;

        return cfg;
    }

    /// <summary>
    /// Fester Speicherort (Server, Tests). Ohne Angabe gilt der aktuelle Datenordner – so landet die Datei
    /// nach einem Datenordner-Umzug automatisch am neuen Ort.
    /// </summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>Beim Laden nicht auflösbare Token-Verweise (z. B. Sicherung von einem anderen Rechner) – bleiben erhalten.</summary>
    [JsonIgnore]
    public Dictionary<string, string> UnresolvedSecrets { get; set; } = [];

    /// <summary>Letzter Fehler beim Speichern (Audit E2); null, sobald wieder erfolgreich gespeichert wurde.</summary>
    [JsonIgnore]
    public string? LastSaveError { get; private set; }

    /// <summary>Speichern fehlgeschlagen (Datei gesperrt, Datenträger voll …) – der Hub protokolliert und meldet es.</summary>
    public event Action<string>? SaveFailed;

    public void Save() => Save(FilePath ?? DataPaths.ConfigFile);

    public void Save(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            Directory.CreateDirectory(dir);
            var root = JsonSerializer.SerializeToNode(this, WriteOptions)!;
            // Tokens nach secrets.json; gelingt das nicht, bleiben sie wie bisher in config.json (nie verlieren)
            try { ConfigSecrets.Externalize(root, new SecretStore(dir), UnresolvedSecrets); }
            catch { root = JsonSerializer.SerializeToNode(this, WriteOptions)!; }
            var tmp = filePath + ".tmp";
            // Audit E2: erst vollständig auf den Datenträger schreiben (fsync), dann umbenennen – bei Stromausfall auf
            // SD-Karten sonst womöglich eine leere oder halbe config.json
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, filePath, overwrite: true);
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            // Nicht mehr still verschlucken (Audit E2): Änderungen wären beim nächsten Start sonst unbemerkt weg
            LastSaveError = ex.Message;
            SaveFailed?.Invoke(ex.Message);
        }
    }
}

public sealed class DeviceConfig
{
    public string Name { get; set; } = "Miner";
    public string Host { get; set; } = "";

    /// <summary>0.9.1: Gruppen/Tags, z. B. „Community“, „Keller“ (Filter, Summen, Push-Ziele je Gruppe). Additiv.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Leer = Adresse automatisch aus dem Stratum-User des Geräts.</summary>
    public string WalletAddress { get; set; } = "";

    /// <summary>"Auto" (aus der Wallet-Adresse), "BTC" oder "BCH".</summary>
    public string Coin { get; set; } = "Auto";

    /// <summary>GitHub-Repository der Firmware für den Update-Check, leer = kein Check.</summary>
    public string FirmwareRepo { get; set; } = "bitaxeorg/ESP-Miner";

    /// <summary>Manuell gewähltes Tuning-Profil; null = automatisch erkennen.</summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// Miner-Logs dauerhaft mitlesen und bei passenden Zeilen melden. Belegt dauerhaft einen der wenigen
    /// WebSocket-Plätze des Miners, daher standardmäßig aus.
    /// </summary>
    public bool LogAlerts { get; set; }

    /// <summary>
    /// 0.9.11: Miner-Logs speichern (<see cref="AppConfig.MinerLogKeepHours"/>, Standard 48 h). Liest über dieselbe
    /// Verbindung wie die Log-Alarme mit. Additiv, standardmäßig aus.
    /// </summary>
    public bool LogArchive { get; set; }

    /// <summary>
    /// 0.9.11 Wartungsmodus: Am Miner wird gearbeitet – keine Meldungen (außer Blockfunden), kein Watchdog-Neustart,
    /// keine Automatik, Ausfallzeit zählt nicht in die Verfügbarkeit. Additiv, standardmäßig aus.
    /// </summary>
    public bool MaintenanceMode { get; set; }

    /// <summary>Beginn des Wartungsmodus (Ortszeit).</summary>
    public DateTime? MaintenanceSince { get; set; }

    /// <summary>Wartungsmodus endet automatisch zu diesem Zeitpunkt (Ortszeit); null = erst von Hand.</summary>
    public DateTime? MaintenanceUntil { get; set; }

    /// <summary>Benannte Einstellungen (z. B. "Hashrate", "Effizienz") für Zeitplan und Strompreis-Regel.</summary>
    public List<TuningPreset> Presets { get; set; } = [];

    /// <summary>Temperaturschutz (automatisch eine Stufe herunter).</summary>
    public ThermalGuardRule ThermalGuard { get; set; } = new();

    /// <summary>Voreinstellung nach Uhrzeit oder Strompreis.</summary>
    public PresetScheduleRule Schedule { get; set; } = new();

    /// <summary>Laufender Dauertest (übersteht einen Neustart der App); null = keiner.</summary>
    public SoakTestState? Soak { get; set; }

    /// <summary>
    /// 0.9.11: „Heimat-Pool“ (URL:Port) – der Pool, der vor der ersten Umschaltung durch BitaxeTuner Haupt-Pool war.
    /// „Zurück zum Haupt-Pool“ schaltet hierhin. Leer = der aktuelle Haupt-Pool des Miners gilt.
    /// </summary>
    public string? HomePool { get; set; }

    /// <summary>0.9.11: automatische Pool-Umschaltung (Zeitplan, zurück zum Haupt-Pool, bei schlechten Shares).</summary>
    public PoolSwitchRule PoolAuto { get; set; } = new();

    /// <summary>Tiefe Kopie (das Einstellungsfenster arbeitet auf Kopien, "Abbrechen" verwirft alles).</summary>
    public DeviceConfig Clone()
    {
        var copy = (DeviceConfig)MemberwiseClone();
        copy.Groups = [.. Groups];
        copy.Presets = Presets.Select(p => p with { }).ToList();
        copy.ThermalGuard = ThermalGuard.Clone();
        copy.Schedule = Schedule.Clone();
        copy.PoolAuto = PoolAuto.Clone();
        copy.Soak = Soak is null ? null : Soak with { };
        return copy;
    }
}

public sealed class NotificationSettings
{
    /// <summary>"none", "ntfy", "telegram", "discord", "pushover" oder "webhook".</summary>
    public string Provider { get; set; } = "none";

    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";

    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";

    /// <summary>Discord: Webhook-URL eines Kanals (Servereinstellungen → Integrationen → Webhooks).</summary>
    public string DiscordWebhookUrl { get; set; } = "";

    public string PushoverUserKey { get; set; } = "";
    public string PushoverAppToken { get; set; } = "";

    /// <summary>Allgemeiner Webhook: JSON-POST {title, message, priority, …} an diese URL (z. B. Home Assistant, n8n).</summary>
    public string WebhookUrl { get; set; } = "";

    public bool OnOffline { get; set; } = true;
    public bool OnOverheat { get; set; } = true;
    /// <summary>Blockfund und dokumentierter Zufluss.</summary>
    public bool OnFinds { get; set; } = true;
    /// <summary>Watchdog-Neustart und Firmware-Update.</summary>
    public bool OnMaintenance { get; set; } = true;
    /// <summary>Neuer Best-Diff-Rekord.</summary>
    public bool OnRecord { get; set; } = false;
    /// <summary>Treffer in den Miner-Logs.</summary>
    public bool OnLogAlerts { get; set; } = true;
    /// <summary>Fallback-Pool, hohe Ablehnungsquote, langsamer Pool.</summary>
    public bool OnPool { get; set; } = true;
    /// <summary>0.6.1: Smart Plug nicht erreichbar, Mehrverbrauch gestiegen.</summary>
    public bool OnPlugs { get; set; } = true;
    /// <summary>0.7.0: Gesundheits-Frühwarnung (Kühlung, Effizienz, Lüfter, Shares, Verfügbarkeit).</summary>
    public bool OnHealth { get; set; } = true;

    /// <summary>
    /// 0.8.0: mehrere Push-Ziele mit eigener Auswahl. Leer = die Einzel-Einstellung oben gilt (bisheriges Verhalten).
    /// Beim Speichern wird das erste Ziel zusätzlich in die Einzel-Felder geschrieben (ältere Versionen senden weiter).
    /// </summary>
    public List<PushTarget> Targets { get; set; } = [];

    /// <summary>Tatsächlich genutzte Ziele: die Liste oder – ohne Liste – die Einzel-Einstellung als ein Ziel.</summary>
    public IReadOnlyList<PushTarget> EffectiveTargets() => Targets.Count > 0 ? Targets : LegacyTarget() is { } t ? [t] : [];

    /// <summary>
    /// Möchte irgendein aktives Ziel Meldungen dieses Bereichs (vor aufwendiger Vorbereitung prüfen)?
    /// Ohne Zielliste genau der bisherige Schalter – ob ein Dienst eingerichtet ist, prüft der Versand selbst.
    /// </summary>
    public bool Wants(NotifyCategory category) => Targets.Count > 0
        ? Targets.Any(t => t.Enabled && t.Wants(category))
        : category is NotifyCategory.Other or NotifyCategory.DailyReport or NotifyCategory.MonthlyReport
          || LegacyCategories().Contains(category.ToString());

    private List<string> LegacyCategories()
    {
        var cats = new List<string> { nameof(NotifyCategory.DailyReport), nameof(NotifyCategory.MonthlyReport) };
        void Add(bool on, NotifyCategory c) { if (on) cats.Add(c.ToString()); }
        Add(OnOffline, NotifyCategory.Offline);
        Add(OnOverheat, NotifyCategory.Overheat);
        Add(OnFinds, NotifyCategory.Finds);
        Add(OnMaintenance, NotifyCategory.Maintenance);
        Add(OnRecord, NotifyCategory.Record);
        Add(OnLogAlerts, NotifyCategory.LogAlerts);
        Add(OnPool, NotifyCategory.Pool);
        Add(OnPlugs, NotifyCategory.Plugs);
        Add(OnHealth, NotifyCategory.Health);
        return cats;
    }

    /// <summary>Die Einzel-Einstellung als Ziel; null, wenn kein Dienst gewählt ist.</summary>
    public PushTarget? LegacyTarget()
    {
        if (Provider is not ("ntfy" or "telegram" or "discord" or "pushover" or "webhook")) return null;
        var cats = LegacyCategories();
        return new PushTarget
        {
            Id = "legacy", Name = "", Provider = Provider, NtfyServer = NtfyServer, NtfyTopic = NtfyTopic,
            TelegramBotToken = TelegramBotToken, TelegramChatId = TelegramChatId, DiscordWebhookUrl = DiscordWebhookUrl,
            PushoverUserKey = PushoverUserKey, PushoverAppToken = PushoverAppToken, WebhookUrl = WebhookUrl, Categories = cats,
        };
    }

    /// <summary>
    /// Nach Bearbeiten der Liste: Ziele prüfen (Dienst, Kennung, Bereiche, nur bekannte Miner), erstes Ziel in die
    /// Einzel-Felder spiegeln. Eine leere Liste schaltet auch die Einzel-Einstellung ab – sonst sendete sie weiter.
    /// </summary>
    public void ApplyTargets(IEnumerable<string> knownHosts)
    {
        var hosts = knownHosts.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>();
        foreach (var t in Targets)
        {
            if (t.Provider is not ("ntfy" or "telegram" or "discord" or "pushover" or "webhook"))
                throw new I18n.LocalizedException("Unbekannter Push-Dienst: {0}", t.Provider);
            if (string.IsNullOrWhiteSpace(t.Id) || !System.Text.RegularExpressions.Regex.IsMatch(t.Id, "^[a-z0-9]{4,32}$") || !ids.Add(t.Id))
            {
                t.Id = Guid.NewGuid().ToString("N")[..8];
                ids.Add(t.Id);
            }
            t.Name = (t.Name ?? "").Trim();
            (t.NtfyServer, t.NtfyTopic) = (string.IsNullOrWhiteSpace(t.NtfyServer) ? "https://ntfy.sh" : t.NtfyServer.Trim(), (t.NtfyTopic ?? "").Trim());
            (t.TelegramBotToken, t.TelegramChatId) = ((t.TelegramBotToken ?? "").Trim(), (t.TelegramChatId ?? "").Trim());
            (t.DiscordWebhookUrl, t.WebhookUrl) = ((t.DiscordWebhookUrl ?? "").Trim(), (t.WebhookUrl ?? "").Trim());
            (t.PushoverUserKey, t.PushoverAppToken) = ((t.PushoverUserKey ?? "").Trim(), (t.PushoverAppToken ?? "").Trim());
            t.Categories = (t.Categories ?? []).Where(c => Enum.TryParse<NotifyCategory>(c, true, out _))
                .Select(c => Enum.Parse<NotifyCategory>(c, true).ToString()).Distinct().ToList();
            t.Miners = (t.Miners ?? []).Where(h => hosts.Contains(h.Trim())).Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            t.ReportExclude = (t.ReportExclude ?? []).Where(x => ReportParts.All.Contains(x)).Distinct().ToList();
            t.Groups = MinerGroups.Normalize(t.Groups);
        }
        if (Targets.Count == 0) Provider = "none";
        else SyncLegacyFromTargets();
    }

    /// <summary>Kopie für die Bearbeitung: ohne Liste wird die Einzel-Einstellung als erstes Ziel angeboten.</summary>
    public NotificationSettings ForEditing()
    {
        var c = Clone();
        if (c.Targets.Count == 0 && LegacyTarget() is { } t)
        {
            t.Id = Guid.NewGuid().ToString("N")[..8];
            c.Targets = [t];
        }
        return c;
    }

    /// <summary>Erstes Ziel in die Einzel-Felder spiegeln (Rückweg auf ältere Versionen); ohne Ziele: Dienst aus.</summary>
    public void SyncLegacyFromTargets()
    {
        if (Targets.Count == 0) return;
        var t = Targets.FirstOrDefault(x => x.Enabled) ?? Targets[0];
        Provider = t.Enabled ? t.Provider : "none";
        (NtfyServer, NtfyTopic, TelegramBotToken, TelegramChatId) = (t.NtfyServer, t.NtfyTopic, t.TelegramBotToken, t.TelegramChatId);
        (DiscordWebhookUrl, PushoverUserKey, PushoverAppToken, WebhookUrl) = (t.DiscordWebhookUrl, t.PushoverUserKey, t.PushoverAppToken, t.WebhookUrl);
        OnOffline = t.Wants(NotifyCategory.Offline);
        OnOverheat = t.Wants(NotifyCategory.Overheat);
        OnFinds = t.Wants(NotifyCategory.Finds);
        OnMaintenance = t.Wants(NotifyCategory.Maintenance);
        OnRecord = t.Wants(NotifyCategory.Record);
        OnLogAlerts = t.Wants(NotifyCategory.LogAlerts);
        OnPool = t.Wants(NotifyCategory.Pool);
        OnPlugs = t.Wants(NotifyCategory.Plugs);
        OnHealth = t.Wants(NotifyCategory.Health);
    }

    public NotificationSettings Clone()
    {
        var c = (NotificationSettings)MemberwiseClone();
        c.Targets = Targets.Select(t => t.Clone()).ToList();
        return c;
    }
}

public sealed class WatchdogSettings
{
    /// <summary>Standardmäßig aus: automatische Eingriffe nur auf ausdrücklichen Wunsch.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Minuten mit Hashrate unter 1 GH/s, bevor neu gestartet wird.</summary>
    public int ZeroHashMinutes { get; set; } = 10;

    /// <summary>Mindestabstand zwischen zwei automatischen Neustarts desselben Miners.</summary>
    public int CooldownMinutes { get; set; } = 60;

    public WatchdogSettings Clone() => (WatchdogSettings)MemberwiseClone();
}

public sealed class LogAlertSettings
{
    /// <summary>Jede Zeile mit Stufe E (Fehler) melden.</summary>
    public bool OnErrors { get; set; } = true;

    /// <summary>Weitere Muster (regulärer Ausdruck, Groß-/Kleinschreibung egal) – je Treffer eine Meldung.</summary>
    public List<string> Patterns { get; set; } =
    [
        @"stratum.*(lost|disconnect|fail|timed? ?out|reconnect)",
        @"overheat",
        @"power.?fault|vcore.*(fail|error)|voltage.*(fault|error)",
        @"fallback",
    ];

    /// <summary>Dieselbe Regel für denselben Miner höchstens einmal in diesem Zeitraum.</summary>
    public int CooldownMinutes { get; set; } = 30;

    public LogAlertSettings Clone() => new() { OnErrors = OnErrors, Patterns = [.. Patterns], CooldownMinutes = CooldownMinutes };
}

public sealed class PoolWatchSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Warnung, wenn im Zeitfenster mehr als dieser Anteil der Shares abgelehnt wird.</summary>
    public double RejectPercent { get; set; } = 5;
    public int WindowMinutes { get; set; } = 30;
    /// <summary>Mindestanzahl Shares im Fenster, bevor die Quote bewertet wird.</summary>
    public int MinShares { get; set; } = 20;
    /// <summary>Warnung bei mittlerer Pool-Antwortzeit darüber (ms); 0 = aus.</summary>
    public double ResponseMs { get; set; } = 500;

    public PoolWatchSettings Clone() => (PoolWatchSettings)MemberwiseClone();
}

public sealed class DailyReportSettings
{
    public bool Enabled { get; set; }
    /// <summary>Uhrzeit (Stunde, 0–23), ab der der Bericht für die letzten 24 h gesendet wird.</summary>
    public int Hour { get; set; } = 20;
    /// <summary>Datum des zuletzt gesendeten Berichts (yyyy-MM-dd).</summary>
    public string? LastSent { get; set; }
    /// <summary>0.7.0: am Monatsersten Zusammenfassung des Vormonats per Push.</summary>
    public bool Monthly { get; set; }
    /// <summary>Zuletzt gemeldeter Monat (yyyy-MM).</summary>
    public string? LastMonthlySent { get; set; }

    public DailyReportSettings Clone() => (DailyReportSettings)MemberwiseClone();
}

/// <summary>Benannte Frequenz/Spannung eines Miners.</summary>
public sealed record TuningPreset(string Name, int FrequencyMhz, int CoreVoltageMv)
{
    public override string ToString() => $"{Name} ({FrequencyMhz} MHz / {CoreVoltageMv} mV)";
}

/// <summary>
/// Grundlage aller Automatik-Regeln: eine Regel handelt nur, wenn sie ausdrücklich freigegeben wurde
/// UND seitdem nicht verändert wurde (<see cref="ApprovedSignature"/> passt zum aktuellen Inhalt).
/// </summary>
public abstract class AutomationRule
{
    public bool Enabled { get; set; }
    /// <summary>Prüfsumme des Regelinhalts zum Zeitpunkt der Freigabe.</summary>
    public string? ApprovedSignature { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>Inhalt, der freigegeben wird (ohne Freigabe-Felder).</summary>
    public abstract string Describe();

    public string Signature(string host)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant() + "|" + Describe());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public bool IsApproved(string host) => Enabled && ApprovedSignature == Signature(host);

    public void Approve(string host)
    {
        ApprovedSignature = Signature(host);
        ApprovedAt = DateTime.Now;
    }
}

public sealed class ThermalGuardRule : AutomationRule
{
    /// <summary>Chiptemperatur, ab der eingegriffen wird.</summary>
    public double MaxChipTempC { get; set; } = 65;
    /// <summary>VR-Temperatur, ab der eingegriffen wird.</summary>
    public double MaxVrTempC { get; set; } = 85;
    /// <summary>So lange muss die Grenze durchgehend überschritten sein.</summary>
    public int Minutes { get; set; } = 5;
    /// <summary>Frequenzsenkung je Eingriff.</summary>
    public int StepMhz { get; set; } = 25;
    /// <summary>Nie unter diese Frequenz.</summary>
    public int MinFrequencyMhz { get; set; } = 400;
    /// <summary>Schrittweise zurück zur ursprünglichen Frequenz, wenn so lange mindestens 5 °C unter der Grenze.</summary>
    public int RecoverMinutes { get; set; } = 60;
    public bool Recover { get; set; } = true;

    public override string Describe() => FormattableString.Invariant(
        $"thermal|{MaxChipTempC}|{MaxVrTempC}|{Minutes}|{StepMhz}|{MinFrequencyMhz}|{Recover}|{RecoverMinutes}");

    public ThermalGuardRule Clone() => (ThermalGuardRule)MemberwiseClone();
}

public sealed class ScheduleEntry
{
    /// <summary>Wochentage als Bitmaske (Bit 0 = Sonntag … Bit 6 = Samstag); 127 = täglich.</summary>
    public int Days { get; set; } = 127;
    /// <summary>Beginn (Stunde, 0–23, einschließlich).</summary>
    public int FromHour { get; set; }
    /// <summary>Ende (Stunde, 1–24, ausschließlich). Kleiner als FromHour = über Mitternacht.</summary>
    public int ToHour { get; set; } = 24;
    public string Preset { get; set; } = "";

    public bool Matches(DateTime local)
    {
        bool Day(DayOfWeek d) => (Days & (1 << (int)d)) != 0;
        if (FromHour <= ToHour)
            return Day(local.DayOfWeek) && local.Hour >= FromHour && local.Hour < ToHour;
        // Über Mitternacht (z. B. 22–6): der Morgen gehört zum Vortag
        return (local.Hour >= FromHour && Day(local.DayOfWeek)) ||
               (local.Hour < ToHour && Day(local.AddDays(-1).DayOfWeek));
    }

    public ScheduleEntry Clone() => (ScheduleEntry)MemberwiseClone();

    private static readonly string[] DayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    private static readonly string[] EnglishDayNames = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

    /// <summary>Lesbare Form der Wochentage für die Bearbeitung: "täglich", "Mo-Fr", "Sa,So", "Mo,Mi,Fr" (in der Sprache der App).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DaysText
    {
        get
        {
            var names = Loc.Current.Language == "en" ? EnglishDayNames : DayNames;
            if ((Days & 127) == 127) return L.T("täglich");
            if ((Days & 127) == 0b0111110) return $"{names[1]}-{names[5]}";
            if ((Days & 127) == 0b1000001) return $"{names[6]},{names[0]}";
            return string.Join(",", Enumerable.Range(1, 7).Select(i => i % 7).Where(d => (Days & (1 << d)) != 0).Select(d => names[d]));
        }
        set => Days = ParseDays(value) ?? Days;
    }

    /// <summary>Deutsche und englische Kürzel („Mo-Fr“, „Sa,So“, „Sa,Su“, „täglich“, „daily“); null = nicht erkannt.</summary>
    public static int? ParseDays(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (t is "" or "täglich" or "taeglich" or "alle" or "mo-so" or "daily" or "everyday" or "all" or "mo-su") return 127;
        static int Day(string name)
        {
            var i = Array.FindIndex(DayNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : Array.FindIndex(EnglishDayNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        var mask = 0;
        foreach (var part in t.Split(',', ';'))
        {
            var range = part.Split('-');
            var a = Day(range[0]);
            var b = range.Length > 1 ? Day(range[1]) : a;
            if (a < 0 || b < 0) return null;
            for (var d = a; ; d = (d + 1) % 7)
            {
                mask |= 1 << d;
                if (d == b) break;
            }
        }
        return mask;
    }
}

public sealed class PresetScheduleRule : AutomationRule
{
    /// <summary>"time" (Wochenplan) oder "price" (Strompreis).</summary>
    public string Mode { get; set; } = "time";
    public List<ScheduleEntry> Entries { get; set; } = [];
    /// <summary>Voreinstellung, wenn kein Eintrag passt (leer = nichts tun).</summary>
    public string DefaultPreset { get; set; } = "";
    /// <summary>Preis-Modus: unter/gleich dieser Schwelle (ct/kWh) → <see cref="CheapPreset"/>, sonst <see cref="ExpensivePreset"/>.</summary>
    public double ThresholdCt { get; set; } = 25;
    public string CheapPreset { get; set; } = "";
    public string ExpensivePreset { get; set; } = "";

    public override string Describe() => FormattableString.Invariant(
        $"schedule|{Mode}|{DefaultPreset}|{ThresholdCt}|{CheapPreset}|{ExpensivePreset}|") +
        string.Join(";", Entries.Select(e => FormattableString.Invariant($"{e.Days},{e.FromHour},{e.ToHour},{e.Preset}")));

    /// <summary>
    /// Nutzt der Zeitplan diese Voreinstellung? Die Freigabe merkt sich nur den Namen – wer eine benutzte Voreinstellung
    /// überschreibt, ändert also, was der freigegebene Zeitplan setzt. Darauf weisen Desktop und Browser vorher hin.
    /// </summary>
    public bool UsesPreset(string name) =>
        new[] { DefaultPreset, CheapPreset, ExpensivePreset }.Concat(Entries.Select(e => e.Preset))
            .Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    public PresetScheduleRule Clone()
    {
        var copy = (PresetScheduleRule)MemberwiseClone();
        copy.Entries = Entries.Select(e => e.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// 0.9.7: Zeitplan oder Strompreis-Regel für eine ganze Miner-Gruppe. Jeder Miner nutzt seine eigene Voreinstellung
/// gleichen Namens (gegen sein Profil geprüft); fehlt sie oder liegt sie außerhalb der Grenzen, wird er übersprungen.
/// Eine eigene Regel eines Miners hat Vorrang. Die Freigabe umfasst die Mitglieder, die Werte ihrer Voreinstellungen und
/// ihre Profilgrenzen (Audit N-S4) – ändert sich davon etwas, ist eine neue Freigabe nötig.
/// </summary>
public sealed class GroupScheduleRule
{
    public string Group { get; set; } = "";
    public PresetScheduleRule Schedule { get; set; } = new();

    /// <summary>Schlüssel für die Freigabe: Gruppe und Mitglieder (sortiert).</summary>
    public static string ApprovalKey(string group, IEnumerable<string> hosts) =>
        "group:" + group.Trim().ToLowerInvariant() + "|" + string.Join(",", hosts.Select(h => h.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal));

    public GroupScheduleRule Clone() => new() { Group = Group, Schedule = Schedule.Clone() };
}

/// <summary>
/// 0.9.11: automatische Pool-Umschaltung zwischen Haupt- und Ersatz-Pool des Miners. Jede Umschaltung schreibt nur die
/// Pool-Auswahl (bzw. bei alter Firmware die Pool-Felder) und startet den Miner neu; Frequenz/Spannung bleiben unberührt.
/// </summary>
public sealed class PoolSwitchRule : AutomationRule
{
    /// <summary>Zeitfenster, in denen der Ersatz-Pool genutzt wird (Preset-Feld bleibt leer); außerhalb → Haupt-Pool.</summary>
    public List<ScheduleEntry> Entries { get; set; } = [];
    /// <summary>Zurück zum Haupt-Pool, sobald er wieder erreichbar ist (nach <see cref="ReturnAfterMinutes"/>).</summary>
    public bool ReturnHome { get; set; } = true;
    public int ReturnAfterMinutes { get; set; } = 30;
    /// <summary>Auf den Ersatz-Pool wechseln, wenn die Pool-Überwachung so lange zu viele Ablehnungen oder zu lange Antwortzeiten meldet.</summary>
    public bool OnBadShares { get; set; }
    public int BadMinutes { get; set; } = 15;

    public override string Describe() => FormattableString.Invariant(
        $"pool|{ReturnHome}|{ReturnAfterMinutes}|{OnBadShares}|{BadMinutes}|") +
        string.Join(";", Entries.Select(e => FormattableString.Invariant($"{e.Days},{e.FromHour},{e.ToHour}")));

    /// <summary>Soll nach Zeitplan gerade der Ersatz-Pool laufen?</summary>
    public bool BackupScheduled(DateTime local) => Entries.Any(e => e.Matches(local));

    public PoolSwitchRule Clone()
    {
        var copy = (PoolSwitchRule)MemberwiseClone();
        copy.Entries = Entries.Select(e => e.Clone()).ToList();
        return copy;
    }
}

/// <summary>0.9.11: Pool-Automatik für alle Miner einer Gruppe. Eine eigene Regel eines Miners hat Vorrang.</summary>
public sealed class GroupPoolRule
{
    public string Group { get; set; } = "";
    public PoolSwitchRule Rule { get; set; } = new();

    public GroupPoolRule Clone() => new() { Group = Group, Rule = Rule.Clone() };
}

public sealed class PriceSourceSettings
{
    /// <summary>"none", "awattar-de", "awattar-at" oder "tibber".</summary>
    public string Source { get; set; } = "none";
    public string TibberToken { get; set; } = "";

    /// <summary>0.6.1: Stromkosten stundenweise mit dem Preis der Quelle rechnen (sonst fester ct/kWh-Wert).</summary>
    public bool DynamicCosts { get; set; }

    /// <summary>Aufschlag in ct/kWh für die Kostenrechnung (aWATTar: Netzentgelte, Steuern, Umlagen; Tibber: 0).</summary>
    public double SurchargeCt { get; set; }

    public PriceSourceSettings Clone() => (PriceSourceSettings)MemberwiseClone();
}

/// <summary>Laufender Dauertest einer Einstellung.</summary>
public sealed record SoakTestState(DateTime StartedAt, DateTime Until, int FrequencyMhz, int CoreVoltageMv);

public sealed class WebViewSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8484;
    /// <summary>
    /// PIN als PBKDF2-Hash mit Salz (ab 0.9.5, Audit S6); ältere PINs als SHA-256 bleiben gültig, bis sie neu gesetzt werden.
    /// Liegt wie die Tokens in secrets.json, nicht in config.json (<see cref="ConfigSecrets"/>).
    /// </summary>
    public string PinHash { get; set; } = "";

    public const int MinPinLength = 6;

    /// <summary>Neue PIN speichern: PBKDF2 mit Salz (wie Admin-Passwort und Ansicht-Zugänge).</summary>
    public static string HashPin(string pin) => Transfer.Secrets.HashPassword(pin.Trim());

    /// <summary>PIN prüfen – neues Format (PBKDF2) und das bisherige (SHA-256 mit festem Präfix).</summary>
    public static bool VerifyPin(string pin, string stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(pin)) return false;
        if (stored.StartsWith("pbkdf2-", StringComparison.Ordinal)) return Transfer.Secrets.VerifyPassword(pin.Trim(), stored);
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("bitaxetuner|" + pin.Trim())));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(legacy), System.Text.Encoding.ASCII.GetBytes(stored));
    }

    /// <summary>Gespeichert im alten Format (ungesalzen) – Hinweis „PIN neu setzen“.</summary>
    public bool PinIsLegacy => PinHash.Length > 0 && !PinHash.StartsWith("pbkdf2-", StringComparison.Ordinal);

    /// <summary>Neue PIN gültig? Ergebnis: Fehlermeldung oder null.</summary>
    public static string? ValidateNewPin(string pin) =>
        pin.Length < MinPinLength || pin.Length > 12 || !pin.All(char.IsAsciiDigit)
            ? I18n.L.T("PIN: {0} bis 12 Ziffern.", MinPinLength) : null;

    public WebViewSettings Clone() => (WebViewSettings)MemberwiseClone();
}

/// <summary>
/// Prometheus-Export (/metrics am Server). Gespeichert wird nur der SHA-256-Hash des Tokens; das Token selbst wird beim
/// Erzeugen einmal angezeigt und in Prometheus als „bearer_token“ eingetragen.
/// </summary>
public sealed class MetricsSettings
{
    public bool Enabled { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime? TokenCreatedUtc { get; set; }
}

/// <summary>Verbindung der Desktop-App zu einem BitaxeTuner-Server (Betriebsart „Server“).</summary>
public sealed class ServerConnectionSettings
{
    /// <summary>true: die App fragt keine Miner ab, alles läuft auf dem Server.</summary>
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    /// <summary>API-Token (btk_…) aus der Server-Oberfläche.</summary>
    public string Token { get; set; } = "";
    /// <summary>SHA-256-Fingerabdruck des selbst signierten HTTPS-Zertifikats (bestätigt beim Verbinden).</summary>
    public string? CertificateFingerprint { get; set; }

    /// <summary>Täglich eine geprüfte Sicherung vom Server auf diesen PC holen.</summary>
    public bool BackupPickup { get; set; }
    /// <summary>Zielordner (leer = Dokumente\BitaxeTuner-Sicherungen).</summary>
    public string BackupFolder { get; set; } = "";
    public int BackupKeep { get; set; } = 14;
    public string? BackupLastPickup { get; set; }
    /// <summary>Nach dem ersten Verbinden einmal gefragt, ob die tägliche Sicherung auf diesen PC geholt werden soll.</summary>
    public bool BackupPickupAsked { get; set; }

    /// <summary>Benutzer für SSH auf den Pi (wie bei „Raspberry Pi vorbereiten“ angelegt).</summary>
    public string SshUser { get; set; } = "pi";

    /// <summary>Rechner für SSH; leer = Host der Server-Adresse.</summary>
    public string SshHost { get; set; } = "";
}

/// <summary>Miner-Gruppen: Namen bereinigen und Mitglieder finden (Groß-/Kleinschreibung egal).</summary>
public static class MinerGroups
{
    public const int MaxLength = 40;

    /// <summary>Getrimmt, ohne leere und doppelte Einträge, höchstens 40 Zeichen, höchstens 10 je Miner.</summary>
    public static List<string> Normalize(IEnumerable<string>? groups) =>
        (groups ?? []).Select(g => (g ?? "").Trim()).Where(g => g.Length > 0)
            .Select(g => g.Length > MaxLength ? g[..MaxLength] : g)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();

    /// <summary>Alle vorkommenden Gruppen, alphabetisch.</summary>
    public static List<string> All(IEnumerable<DeviceConfig> devices) =>
        devices.SelectMany(d => d.Groups).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool Contains(DeviceConfig device, string group) => device.Groups.Contains(group, StringComparer.OrdinalIgnoreCase);
}
