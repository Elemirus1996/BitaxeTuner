using System.Text.Json;
using System.Text.Json.Serialization;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Gemeinsame Einstellungen (config.json im Datenordner). Übernommen aus BitaxeMonitor –
/// alle bisherigen Felder und ihre JSON-Namen bleiben unverändert, neue Felder kommen nur hinzu.
/// </summary>
public sealed class AppConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();

    public int IntervalSeconds { get; set; } = 5;
    public int HistoryMinutes { get; set; } = 60;
    public int WalletPollMinutes { get; set; } = 10;

    /// <summary>Strompreis in Cent je kWh.</summary>
    public double ElectricityCtPerKwh { get; set; } = 30;

    /// <summary>Intervall der Steuer-Wallet-Überwachung (Blockchair) in Minuten.</summary>
    public int TaxPollMinutes { get; set; } = 15;

    /// <summary>Optionaler Blockchair-API-Key; ohne Key gelten 1.440 Requests/Tag.</summary>
    public string BlockchairApiKey { get; set; } = "";

    /// <summary>Aufbewahrung der Verlaufsdaten in der Datenbank.</summary>
    public int HistoryDays { get; set; } = 90;

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
    public bool StartMinimized { get; set; } = false;

    /// <summary>Zuletzt gesehene eingehende TX je Adresse - dient der Auszahlungs-Erkennung.</summary>
    public Dictionary<string, string> LastSeenPayoutTxids { get; set; } = new();

    // --- Neu durch die Zusammenführung mit BitaxeTuner ---

    /// <summary>"dark" oder "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Übertakten-Hinweis wurde bestätigt.</summary>
    public bool WarningAccepted { get; set; }

    /// <summary>Beim Start und alle 6 h auf neue BitaxeTuner-Version prüfen.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Für diese Version wurde bereits per Push auf ein Update hingewiesen.</summary>
    public string? NotifiedAppVersion { get; set; }

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

    /// <summary>Betriebsart der Desktop-App: lokal (Standard) oder mit einem BitaxeTuner-Server verbunden.</summary>
    public ServerConnectionSettings Server { get; set; } = new();

    /// <summary>Zusatzlüfter am Server (Raspberry Pi Pico per USB).</summary>
    public FanSettings Fans { get; set; } = new();

    /// <summary>E-Paper-Anzeige und Taster am Pico.</summary>
    public DisplaySettings Display { get; set; } = new();

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
            cfg = File.Exists(filePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(filePath)) ?? new AppConfig()
                : new AppConfig();
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

    public void Save() => Save(FilePath ?? DataPaths.ConfigFile);

    public void Save(string filePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { /* nicht kritisch */ }
    }
}

public sealed class DeviceConfig
{
    public string Name { get; set; } = "Miner";
    public string Host { get; set; } = "";

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

    /// <summary>Benannte Einstellungen (z. B. "Hashrate", "Effizienz") für Zeitplan und Strompreis-Regel.</summary>
    public List<TuningPreset> Presets { get; set; } = [];

    /// <summary>Temperaturschutz (automatisch eine Stufe herunter).</summary>
    public ThermalGuardRule ThermalGuard { get; set; } = new();

    /// <summary>Voreinstellung nach Uhrzeit oder Strompreis.</summary>
    public PresetScheduleRule Schedule { get; set; } = new();

    /// <summary>Laufender Dauertest (übersteht einen Neustart der App); null = keiner.</summary>
    public SoakTestState? Soak { get; set; }

    /// <summary>Tiefe Kopie (das Einstellungsfenster arbeitet auf Kopien, "Abbrechen" verwirft alles).</summary>
    public DeviceConfig Clone()
    {
        var copy = (DeviceConfig)MemberwiseClone();
        copy.Presets = Presets.Select(p => p with { }).ToList();
        copy.ThermalGuard = ThermalGuard.Clone();
        copy.Schedule = Schedule.Clone();
        copy.Soak = Soak is null ? null : Soak with { };
        return copy;
    }
}

public sealed class NotificationSettings
{
    /// <summary>"none", "ntfy" oder "telegram".</summary>
    public string Provider { get; set; } = "none";

    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";

    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";

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

    public NotificationSettings Clone() => (NotificationSettings)MemberwiseClone();
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

    /// <summary>Lesbare Form der Wochentage für die Bearbeitung: "täglich", "Mo-Fr", "Sa,So", "Mo,Mi,Fr".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DaysText
    {
        get
        {
            if ((Days & 127) == 127) return "täglich";
            if ((Days & 127) == 0b0111110) return "Mo-Fr";
            if ((Days & 127) == 0b1000001) return "Sa,So";
            return string.Join(",", Enumerable.Range(1, 7).Select(i => i % 7).Where(d => (Days & (1 << d)) != 0).Select(d => DayNames[d]));
        }
        set => Days = ParseDays(value) ?? Days;
    }

    public static int? ParseDays(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (t is "" or "täglich" or "taeglich" or "alle" or "mo-so") return 127;
        var mask = 0;
        foreach (var part in t.Split(',', ';'))
        {
            var range = part.Split('-');
            var a = Array.FindIndex(DayNames, n => n.Equals(range[0], StringComparison.OrdinalIgnoreCase));
            var b = range.Length > 1 ? Array.FindIndex(DayNames, n => n.Equals(range[1], StringComparison.OrdinalIgnoreCase)) : a;
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

    public PresetScheduleRule Clone()
    {
        var copy = (PresetScheduleRule)MemberwiseClone();
        copy.Entries = Entries.Select(e => e.Clone()).ToList();
        return copy;
    }
}

public sealed class PriceSourceSettings
{
    /// <summary>"none", "awattar-de", "awattar-at" oder "tibber".</summary>
    public string Source { get; set; } = "none";
    public string TibberToken { get; set; } = "";

    public PriceSourceSettings Clone() => (PriceSourceSettings)MemberwiseClone();
}

/// <summary>Laufender Dauertest einer Einstellung.</summary>
public sealed record SoakTestState(DateTime StartedAt, DateTime Until, int FrequencyMhz, int CoreVoltageMv);

public sealed class WebViewSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8484;
    /// <summary>PIN als SHA-256 (nie im Klartext gespeichert).</summary>
    public string PinHash { get; set; } = "";

    public static string HashPin(string pin) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("bitaxetuner|" + pin.Trim())));

    public WebViewSettings Clone() => (WebViewSettings)MemberwiseClone();
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
}
