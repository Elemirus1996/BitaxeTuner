using System.ComponentModel;
using System.Windows;
using BitaxeTuner.App.ViewModels;

namespace BitaxeTuner.App;

public partial class MainWindow : Window
{
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || DataContext is not MainViewModel vm || !vm.AnyRunning) return;

        e.Cancel = true;
        var answer = MessageBox.Show(
            "Es laufen noch Benchmarks. Beenden und die Einstellungen der Geräte wiederherstellen?",
            "BitaxeTuner beenden", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        IsEnabled = false;
        Title = "BitaxeTuner – stelle Einstellungen wieder her …";
        await Task.WhenAll(vm.Devices.Select(d => d.StopAndWaitAsync()));
        _closingConfirmed = true;
        Close();
    }
}
