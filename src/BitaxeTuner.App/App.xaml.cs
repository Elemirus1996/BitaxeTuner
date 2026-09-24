using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using BitaxeTuner.App.ViewModels;
using BitaxeTuner.Core.Storage;

namespace BitaxeTuner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF formatiert sonst mit en-US (Dezimalpunkt) – Systemkultur verwenden.
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(language));
        FrameworkContentElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.Documents.TextElement), new FrameworkPropertyMetadata(language));
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        if (!settings.WarningAccepted)
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
            settings.WarningAccepted = true;
            settings.Save();
        }

        var vm = new MainViewModel(settings);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        _ = vm.InitializeAsync();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
