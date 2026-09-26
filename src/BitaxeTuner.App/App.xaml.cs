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
        var config = AppConfig.Load();
        var notes = new List<string>();

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
