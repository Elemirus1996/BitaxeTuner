using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Views;

/// <summary>0.9.11: Wartungsmodus einschalten – Erklärung und Dauer (0 = bis zum Ausschalten).</summary>
public static class MaintenanceDialog
{
    /// <summary>Gewählte Dauer in Stunden (0 = unbegrenzt) oder null bei Abbruch.</summary>
    public static double? Ask(Window? owner, string miner)
    {
        var duration = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var (hours, text) in new (double, string)[]
                 {
                     (0, L.T("bis ich ihn ausschalte")), (1, L.T("1 Stunde")), (2, L.T("2 Stunden")), (4, L.T("4 Stunden")),
                     (8, L.T("8 Stunden")), (24, L.T("24 Stunden")),
                 })
            duration.Items.Add(new ComboBoxItem { Content = text, Tag = hours });
        duration.SelectedIndex = 0;

        var ok = new Button { Content = L.T("Einschalten"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = L.T("Während des Wartungsmodus pausiert die Überwachung von {0}:", miner) + "\n" +
                   "• " + L.T("keine Meldungen (offline, Temperatur, Pool, Gesundheit) – Blockfunde werden weiter gemeldet") + "\n" +
                   "• " + L.T("kein Watchdog-Neustart, keine Automatik, Pool-Umschaltung oder Temperatur-Absenkung") + "\n" +
                   "• " + L.T("die Zeit zählt nicht als Ausfall in Verfügbarkeit und Berichten") + "\n\n" +
                   L.T("Werte werden weiter abgefragt und angezeigt. Der Überhitzungsschutz der Miner-Firmware bleibt aktiv."),
        });
        panel.Children.Add(new TextBlock { Text = L.T("Wartungsmodus endet"), Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(duration);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = L.T("Wartungsmodus für {0}", miner),
            Content = panel,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? (double)((ComboBoxItem)duration.SelectedItem).Tag : null;
    }
}
