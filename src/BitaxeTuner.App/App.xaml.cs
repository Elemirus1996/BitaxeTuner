using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using BitaxeTuner.App.Services;
using BitaxeTuner.App.Themes;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.Core.Config;

namespace BitaxeTuner.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF formatiert sonst mit en-US (Dezimalpunkt) – Systemkultur verwenden.
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(language));
        FrameworkContentElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.Documents.TextElement), new FrameworkPropertyMetadata(language));
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { WriteCrashLog(args.Exception); args.SetObserved(); };

        // 0. Neustart nach Moduswechsel: warten, bis die alte Instanz history.db freigegeben hat
        var waitIndex = Array.IndexOf(e.Args, "--wait-pid");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(30_000); } catch { /* schon beendet */ }
        }

        // 1. Läuft der alte BitaxeMonitor noch? Dann würden beide pollen und in dieselbe history.db schreiben.
        if (Process.GetProcessesByName("BitaxeMonitor").Length > 0 &&
            MessageBox.Show(
                "BitaxeMonitor läuft noch.\n\nBeide Programme würden die Miner doppelt abfragen und gleichzeitig in history.db schreiben. " +
                "Bitte BitaxeMonitor zuerst beenden.\n\nTrotzdem starten?",
                "BitaxeTuner", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            Shutdown();
            return;
        }

        var dataDir = DataPaths.Current;
        Directory.CreateDirectory(dataDir);
        var notes = new List<string>();

        // Vom Server geholte, bereits geprüfte Daten übernehmen – bevor history.db geöffnet wird
        try
        {
            if (ServerTransfer.ApplyPendingImport(dataDir) is { } imported) notes.Add(imported);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show($"Die Daten vom Server konnten nicht übernommen werden:\n{ex.Message}\n\nDer bisherige lokale Stand ist unverändert bzw. gesichert (backup-…).",
                "BitaxeTuner", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var config = AppConfig.Load();
        Core.I18n.Loc.Configure(config.Language);

        // Betriebsart „Server“: kein eigener Motor, keine Miner-Abfrage – nur die Oberfläche des Servers
        if (config.Server.Enabled && config.Server.Url.Length > 0)
        {
            ThemeManager.Apply(config.Theme);
            var remote = new Views.RemoteWindow(config);
            MainWindow = remote;
            remote.Show();
            return;
        }

        // 2. Vor der ersten Schemaänderung (tuning_events) den ganzen Datenordner sichern – bevor history.db geöffnet wird
        if (!config.IntegrationBackupDone &&
            (File.Exists(DataPaths.ConfigFile) || File.Exists(DataPaths.HistoryFile) || Directory.Exists(DataPaths.TaxDirectory)))
        {
            try
            {
                var backup = ConfigMigrator.BackupDataDirectory(dataDir);
                notes.Add($"Sicherung angelegt: {backup}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Die Sicherung des Datenordners ist fehlgeschlagen:\n{ex.Message}\n\n" +
                    "Zum Schutz deiner Daten (history.db, Steuerdaten) wird das Programm nicht gestartet. Es wurde nichts verändert.",
                    "BitaxeTuner", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }
            config.IntegrationBackupDone = true;
        }

        // 3. Geräte und Ergebnisse des eigenständigen BitaxeTuner übernehmen (nur kopieren, nie überschreiben)
        try
        {
            notes.AddRange(ConfigMigrator.MigrateTunerData(config, TunerLegacySettings.DefaultDirectory, DataPaths.TuningDirectory));
        }
        catch (Exception ex)
        {
            notes.Add("Übernahme der BitaxeTuner-Daten fehlgeschlagen: " + ex.Message);
        }
        config.Save();

        // 4. Übertakten-Hinweis
        if (!config.WarningAccepted)
        {
            var result = MessageBox.Show(
                "BitaxeTuner verändert Frequenz und Kernspannung deiner Miner.\n\n" +
                "• Übertakten erhöht Leistungsaufnahme und Temperatur und kann die Hardware beschädigen.\n" +
                "• Prüfe, ob Netzteil und Kühlung für höhere Leistung ausgelegt sind.\n" +
                "• Das Programm überwacht Temperatur- und Leistungsgrenzen, eine Garantie gibt es trotzdem nicht.\n\n" +
                "Nutzung auf eigenes Risiko. Fortfahren?",
                "BitaxeTuner – Hinweis", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
            config.WarningAccepted = true;
            config.Save();
        }

        // 5. Design, Dienste, Fenster
        ThemeManager.Apply(config.Theme);
        _host = new AppHost(config);
        _host.StartupNotes.AddRange(notes);

        var window = new MainWindow();
        MainWindow = window;
        if (config.StartMinimized) window.WindowState = WindowState.Minimized;
        window.Initialize(new MainViewModel(_host));
        window.Show();
        _ = CheckDoubleOperationAsync(window, _host);
    }

    /// <summary>
    /// Doppelbetrieb verhindern: Ist ein Server bekannt, der gerade dieselben Miner abfragt, während diese App
    /// im Modus „Lokal“ startet, wird nachgefragt (umschalten, Server pausieren oder – nicht empfohlen – beide).
    /// </summary>
    private static async Task CheckDoubleOperationAsync(Window owner, AppHost host)
    {
        var s = host.Config.Server;
        if (s.Url.Length == 0 || s.Token.Length == 0) return;
        try
        {
            using var client = new Core.Transfer.ServerClient(s.Url, s.Token, s.CertificateFingerprint, TimeSpan.FromSeconds(5));
            var info = await client.InfoAsync();
            if (info.Paused || info.Devices == 0) return;
            var status = await client.StatusAsync();
            var local = host.Config.Devices.Select(d => d.Host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shared = status.GetProperty("devices").EnumerateArray()
                .Select(d => d.GetProperty("host").GetString() ?? "").Where(local.Contains).ToList();
            if (shared.Count == 0) return;

            var answer = MessageBox.Show(owner,
                $"Der BitaxeTuner-Server {client.BaseUri} fragt gerade dieselben Miner ab ({shared.Count}).\n" +
                "Beide gleichzeitig würden die Miner doppelt abfragen, doppelt melden und zwei getrennte Verläufe schreiben.\n\n" +
                "Ja: auf „Server“ umschalten (App startet neu, ohne Daten zu übertragen)\n" +
                "Nein: Server pausieren – diese App fragt ab\n" +
                "Abbrechen: beide laufen lassen (nicht empfohlen)",
                "Doppelbetrieb", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                host.Config.Server.Enabled = true;
                host.Config.Save();
                ServerTransfer.Restart();
            }
            else if (answer == MessageBoxResult.No)
            {
                await client.SetPausedAsync(true);
            }
        }
        catch (Core.Transfer.ServerException)
        {
            // Server nicht erreichbar oder Token widerrufen: kein Doppelbetrieb möglich bzw. nicht feststellbar
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(e.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Fehler mit Stacktrace nach &lt;Datenordner&gt;\crash.log (für die Fehlersuche).</summary>
    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(Path.Combine(DataPaths.Current, "crash.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\r\n{ex}\r\n\r\n");
        }
        catch { /* nichts mehr zu retten */ }
    }
}
