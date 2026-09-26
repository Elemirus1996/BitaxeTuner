using System.Windows.Controls;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.App.Tax.ViewModels;

namespace BitaxeTuner.App.Tax.Views;

public partial class TaxPanel : UserControl
{
    public TaxPanel()
    {
        InitializeComponent();
        CoinColumn.ItemsSource = Enum.GetValues<CoinType>();
    }

    private TaxViewModel? Vm => DataContext as TaxViewModel;

    /// <summary>Label/Coin einer Wallet nach Bearbeitung sichern.</summary>
    private void WalletGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not WalletAddress wallet) return;

        // Commit läuft erst nach diesem Handler; deshalb im nächsten Dispatcher-Durchlauf speichern
        Dispatcher.BeginInvoke(() => Vm?.SaveWallet(wallet));
    }

    /// <summary>Manuell nachgetragenen Kurs oder Notiz sichern.</summary>
    private void RewardGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not MinedReward reward) return;
        Dispatcher.BeginInvoke(() => Vm?.SaveReward(reward));
    }
}
