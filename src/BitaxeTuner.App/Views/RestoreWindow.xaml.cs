using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BitaxeTuner.App.Views;

/// <summary>Auswahl einer Sicherung und der Felder, die zurückgespielt werden sollen.</summary>
public partial class RestoreWindow : Window
{
    public sealed partial class Row : ObservableObject
    {
        public Row(SettingChange change, string? hint)
        {
            Change = change;
            Hint = hint;
            _isSelected = hint is null;
        }

        public SettingChange Change { get; }
        /// <summary>Grund, warum das Feld nicht wiederhergestellt werden darf (z. B. außerhalb der Profilgrenzen).</summary>
        public string? Hint { get; }
        public bool IsAllowed => Hint is null;
        [ObservableProperty] private bool _isSelected;
    }

    private readonly Func<SettingsSnapshot, List<SettingChange>> _diff;
    private readonly DeviceProfile? _profile;
    private List<Row> _rows = [];

    public RestoreWindow(string deviceName, IReadOnlyList<SettingsSnapshot> snapshots,
                         Func<SettingsSnapshot, List<SettingChange>> diff, DeviceProfile? profile)
    {
        InitializeComponent();
        _diff = diff;
        _profile = profile;
        HeaderText.Text = $"Einstellungen von {deviceName} wiederherstellen";
        SnapshotBox.ItemsSource = snapshots;
        SnapshotBox.SelectedIndex = 0;
    }

    public SettingsSnapshot? Snapshot => SnapshotBox.SelectedItem as SettingsSnapshot;
    public List<SettingChange> Selected { get; private set; } = [];

    private void OnSnapshotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Snapshot is not { } snap) return;
        _rows = _diff(snap).Select(c => new Row(c, Check(c))).ToList();
        ChangeList.ItemsSource = _rows;
        NoChangesText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = _rows.Any(r => r.IsAllowed);
    }

    /// <summary>Frequenz/Spannung nur innerhalb der Profilgrenzen des ASIC-Modells.</summary>
    private string? Check(SettingChange c)
    {
        if (_profile is null || c.Field is not ("frequency" or "coreVoltage")) return null;
        var value = SettingsSnapshots.ToPatchValue(c.Value) is int i ? i : -1;
        return c.Field == "frequency"
            ? value < _profile.MinFrequencyMhz || value > _profile.MaxFrequencyMhz
                ? $"außerhalb der Grenzen {_profile.MinFrequencyMhz}–{_profile.MaxFrequencyMhz} MHz ({_profile.Name})" : null
            : value < _profile.MinVoltageMv || value > _profile.MaxVoltageMv
                ? $"außerhalb der Grenzen {_profile.MinVoltageMv}–{_profile.MaxVoltageMv} mV ({_profile.Name})" : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Selected = _rows.Where(r => r.IsAllowed && r.IsSelected).Select(r => r.Change).ToList();
        if (Selected.Count == 0)
        {
            MessageBox.Show(this, "Kein Feld ausgewählt.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var text = string.Join("\n", Selected.Select(c => $"{c.Label}: {c.Current} → {c.Saved}"));
        if (MessageBox.Show(this, $"Diese Werte an den Miner senden?\n\n{text}", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        DialogResult = true;
    }
}
