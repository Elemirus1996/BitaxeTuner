using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.ViewModels;

namespace BitaxeTuner.App.Views;

public partial class ComparisonView : UserControl
{
    public ComparisonView() => InitializeComponent();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ComparisonViewModel vm)
            new CompareReportWindow(vm.Host) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
