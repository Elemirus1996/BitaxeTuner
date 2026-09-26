using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.App.Tax.ViewModels;

/// <summary>Adresse eines Miners, die ins Steuer-Modul übernommen werden kann.</summary>
public record MinerAddressCandidate(string DeviceName, string Host, string Address);

public sealed class TaxViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly WalletMonitorService _monitor;
    private readonly TaxLogRepository _repository;
    private readonly Func<IEnumerable<MinerAddressCandidate>> _minerAddresses;

    public ObservableCollection<WalletAddress> Wallets { get; } = new();
    public ObservableCollection<MinedReward> Rewards { get; } = new();
    public ObservableCollection<DisposalRow> Disposals { get; } = new();

    private List<Disposal> _disposals = new();
    private List<DisposalResult> _results = new();
    public IEnumerable<CoinType> AvailableCoins { get; } = Enum.GetValues<CoinType>();

    // ---------- Eingabe ----------

    private string _newAddress = string.Empty;
    public string NewAddress
    {
        get => _newAddress;
        set
        {
            _newAddress = value;
            Raise();
            if (CoinTypeExtensions.IsUnambiguous(value)) NewCoin = CoinTypeExtensions.GuessFromAddress(value);
        }
    }

    private string _newLabel = string.Empty;
    public string NewLabel { get => _newLabel; set { _newLabel = value; Raise(); } }

    private CoinType _newCoin = CoinType.BitcoinCash;
    public CoinType NewCoin { get => _newCoin; set { _newCoin = value; Raise(); } }

    private MinedReward? _selectedReward;
    public MinedReward? SelectedReward { get => _selectedReward; set { _selectedReward = value; Raise(); } }

    /// <summary>Ältere Zuflüsse (Erstimport einer Adresse) lösen kein Hinweisfenster aus.</summary>
    private static readonly TimeSpan NotifyWindow = TimeSpan.FromHours(6);

    private WalletAddress? _selectedWallet;
    public WalletAddress? SelectedWallet { get => _selectedWallet; set { _selectedWallet = value; Raise(); } }

    private string _statusText = "Bereit.";
    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }

    /// <summary>Summe aller EUR-Werte im laufenden Kalenderjahr — relevant für die 256-€-Freigrenze.</summary>
    public string YearSummary
    {
        get
        {
            var year = DateTime.Now.Year;
            var inYear = Rewards.Where(r => r.ReceivedAtUtc.ToLocalTime().Year == year).ToList();
            var sum = inYear.Sum(r => r.EurValue ?? 0);
            var missing = inYear.Count(r => r.EurValue is null);
            var text = $"{year}: {inYear.Count} Zuflüsse · {sum.ToString("N2", De)} €";
            if (missing > 0) text += $" · {missing} ohne Kurs";
            return text;
        }
    }

    /// <summary>Private Veräußerungsgeschäfte (§ 23 EStG) im laufenden Jahr nach FIFO.</summary>
    public string SalesSummary
    {
        get
        {
            var year = DateTime.Now.Year;
            var inYear = _results.Where(r => r.Disposal.SoldAtLocal.Year == year).ToList();
            if (inYear.Count == 0) return $"{year}: keine Verkäufe erfasst";

            var gain = inYear.Sum(r => r.TaxableGainEur);
            var text = $"{year}: {inYear.Count} Verkäufe · steuerpflichtiger Gewinn {gain.ToString("N2", De)} € (Freigrenze 1.000 €)";
            if (inYear.Any(r => r.MissingPrice)) text += " · Kurs fehlt";
            if (inYear.Any(r => r.UnmatchedAmount > 0)) text += " · Menge ohne Zufluss";
            return text;
        }
    }

    private bool _showSales;
    public bool ShowSales
    {
        get => _showSales;
        set { _showSales = value; Raise(); Raise(nameof(ShowRewards)); }
    }
    public bool ShowRewards => !ShowSales;

    private DateTime? _newSaleDate = DateTime.Today;
    public DateTime? NewSaleDate { get => _newSaleDate; set { _newSaleDate = value; Raise(); } }

    private CoinType _newSaleCoin = CoinType.BitcoinCash;
    public CoinType NewSaleCoin { get => _newSaleCoin; set { _newSaleCoin = value; Raise(); } }

    private string _newSaleAmount = string.Empty;
    public string NewSaleAmount { get => _newSaleAmount; set { _newSaleAmount = value; Raise(); } }

    private string _newSaleProceeds = string.Empty;
    public string NewSaleProceeds { get => _newSaleProceeds; set { _newSaleProceeds = value; Raise(); } }

    private string _newSaleNote = string.Empty;
    public string NewSaleNote { get => _newSaleNote; set { _newSaleNote = value; Raise(); } }

    private DisposalRow? _selectedDisposal;
    public DisposalRow? SelectedDisposal { get => _selectedDisposal; set { _selectedDisposal = value; Raise(); } }

    public ICommand ShowRewardsCommand { get; }
    public ICommand ShowSalesCommand { get; }
    public ICommand AddDisposalCommand { get; }
    public ICommand RemoveDisposalCommand { get; }
    public ICommand ExportDisposalsCommand { get; }

    public ICommand AddWalletCommand { get; }
    public ICommand RemoveWalletCommand { get; }
    public ICommand ImportFromMinersCommand { get; }
    public ICommand RefreshNowCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand RemoveRewardCommand { get; }

    public TaxViewModel(WalletMonitorService monitor, TaxLogRepository repository,
                        Func<IEnumerable<MinerAddressCandidate>> minerAddresses)
    {
        _monitor = monitor;
        _repository = repository;
        _minerAddresses = minerAddresses;

        _monitor.NewRewardDetected += OnNewRewardDetected;
        _monitor.RewardUpdated += OnRewardUpdated;
        _monitor.StatusChanged += text => OnUi(() =>
        {
            StatusText = text;
            foreach (var r in Rewards) r.RefreshStatus();
        });

        foreach (var w in _monitor.Wallets) Wallets.Add(w);
        foreach (var r in _monitor.LoadRewards().OrderByDescending(r => r.ReceivedAtUtc)) Rewards.Add(r);

        AddWalletCommand = new RelayCommand(_ => AddWallet(), _ => !string.IsNullOrWhiteSpace(NewAddress));
        RemoveWalletCommand = new RelayCommand(_ => RemoveSelectedWallet(), _ => SelectedWallet is not null);
        ImportFromMinersCommand = new RelayCommand(_ => ImportFromMiners());
        RefreshNowCommand = new RelayCommand(async _ => await RefreshNowAsync());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => Rewards.Count > 0);
        RemoveRewardCommand = new RelayCommand(_ => RemoveSelectedReward(), _ => SelectedReward is not null);

        ShowRewardsCommand = new RelayCommand(_ => ShowSales = false);
        ShowSalesCommand = new RelayCommand(_ => ShowSales = true);
        AddDisposalCommand = new RelayCommand(_ => AddDisposal(),
            _ => NewSaleDate is not null && !string.IsNullOrWhiteSpace(NewSaleAmount) && !string.IsNullOrWhiteSpace(NewSaleProceeds));
        RemoveDisposalCommand = new RelayCommand(_ => RemoveSelectedDisposal(), _ => SelectedDisposal is not null);
        ExportDisposalsCommand = new RelayCommand(_ => ExportDisposals(), _ => _results.Count > 0);

        _disposals = _repository.LoadDisposals();
        Recalculate();

        if (_repository.MigrationNote is { } note) StatusText = note;
    }

    // ---------- Wallets ----------

    private void AddWallet()
    {
        var address = NewAddress.Trim();
        if (_monitor.Contains(address))
        {
            StatusText = "Adresse ist bereits eingetragen.";
            return;
        }

        var wallet = new WalletAddress
        {
            Address = address,
            Coin = NewCoin,
            Label = string.IsNullOrWhiteSpace(NewLabel) ? $"{NewCoin.Symbol()}-Wallet" : NewLabel.Trim()
        };

        if (_monitor.AddWallet(wallet)) Wallets.Add(wallet);

        NewAddress = string.Empty;
        NewLabel = string.Empty;
        StatusText = $"'{wallet.Label}' hinzugefügt.";
    }

    private void RemoveSelectedWallet()
    {
        if (SelectedWallet is null) return;
        if (MessageBox.Show($"'{SelectedWallet.Label}' aus der Überwachung entfernen?\nBereits dokumentierte Zuflüsse bleiben erhalten.",
                            "Wallet entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _monitor.RemoveWallet(SelectedWallet.Id);
        Wallets.Remove(SelectedWallet);
        StatusText = "Wallet entfernt.";
    }

    /// <summary>Nach Bearbeitung von Label oder Coin in der Tabelle.</summary>
    public void SaveWallet(WalletAddress wallet) => _monitor.UpdateWallet(wallet);

    /// <summary>
    /// Adressen aus der Miner-Konfiguration übernehmen (Override oder Stratum-User).
    /// Bereits vorhandene werden übersprungen, der Gerätename wird zum Label.
    /// </summary>
    private void ImportFromMiners()
    {
        var added = 0;
        var skipped = 0;
        var ambiguous = new List<string>();

        foreach (var c in _minerAddresses())
        {
            if (string.IsNullOrWhiteSpace(c.Address)) continue;
            if (_monitor.Contains(c.Address)) { skipped++; continue; }

            var coin = CoinTypeExtensions.GuessFromAddress(c.Address);
            if (!CoinTypeExtensions.IsUnambiguous(c.Address)) ambiguous.Add(c.DeviceName);

            var wallet = new WalletAddress
            {
                Address = c.Address.Trim(),
                Coin = coin,
                Label = c.DeviceName,
                SourceDeviceHost = c.Host
            };

            if (_monitor.AddWallet(wallet))
            {
                Wallets.Add(wallet);
                added++;
            }
        }

        var text = $"{added} Adresse(n) übernommen, {skipped} bereits vorhanden.";
        if (ambiguous.Count > 0)
            text += $" Coin bei {string.Join(", ", ambiguous)} bitte prüfen (Legacy-Adresse, BCH angenommen).";
        if (added == 0 && skipped == 0)
            text = "Keine Adresse gefunden. Miner müssen online sein oder eine Wallet-Adresse in den Einstellungen haben.";
        StatusText = text;
    }

    // ---------- Rewards ----------

    private async Task RefreshNowAsync()
    {
        StatusText = "Prüfe Wallets …";
        await _monitor.PollOnceAsync();
    }

    private void OnNewRewardDetected(MinedReward reward)
    {
        OnUi(() =>
        {
            Rewards.Insert(0, reward);
            Raise(nameof(YearSummary));
            Recalculate();

            // Beim ersten Abruf einer Adresse kommen bis zu 50 alte Eingänge.
            // Die gehören in die Liste, aber nicht als 50 Hinweisfenster.
            if (DateTime.UtcNow - reward.ReceivedAtUtc > NotifyWindow) return;

            var text = $"{reward.Amount.ToString("0.00000000", De)} {reward.Coin.Symbol()} auf {reward.WalletLabel}\n" +
                       $"Zeitpunkt: {reward.ReceivedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}\n" +
                       (reward.EurValue is { } eur
                           ? $"Wert: {eur.ToString("N2", De)} € (Kurs {reward.EurPriceAtReceipt!.Value.ToString("N2", De)} €)"
                           : "EUR-Kurs konnte nicht ermittelt werden – bitte nachtragen.");
            MessageBox.Show(text, "Neuer Zufluss dokumentiert", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    /// <summary>
    /// Nach manueller Bearbeitung von Kurs oder Notiz in der Tabelle. Wurde der
    /// Kurs geändert, wird er als manuell markiert und nie automatisch überschrieben.
    /// </summary>
    public void SaveReward(MinedReward reward)
    {
        var stored = _monitor.LoadRewards().FirstOrDefault(r => r.Id == reward.Id);
        if (stored is null || stored.EurPriceAtReceipt != reward.EurPriceAtReceipt)
        {
            reward.PriceSource = $"manuell eingetragen am {DateTime.Now:dd.MM.yyyy}";
            reward.PriceAtUtc = null;
        }

        _monitor.SaveReward(reward);
        Raise(nameof(YearSummary));
        Recalculate();
    }

    /// <summary>Kurs wurde im Hintergrund nachgeholt: sichtbaren Eintrag aktualisieren.</summary>
    private void OnRewardUpdated(MinedReward updated)
    {
        OnUi(() =>
        {
            var row = Rewards.FirstOrDefault(r => r.Id == updated.Id);
            if (row is null) return;

            row.EurPriceAtReceipt = updated.EurPriceAtReceipt;
            row.PriceAtUtc = updated.PriceAtUtc;
            row.PriceSource = updated.PriceSource;
            row.Note = updated.Note;
            Raise(nameof(YearSummary));
            Recalculate();
        });
    }

    /// <summary>
    /// Eintrag entfernen, der kein Mining-Ertrag ist (z. B. Überweisung von einer Börse).
    /// Die TXID wird gemerkt und nicht erneut importiert.
    /// </summary>
    private void RemoveSelectedReward()
    {
        if (SelectedReward is not { } reward) return;

        var text = $"Eintrag vom {reward.ReceivedAtLocal:dd.MM.yyyy HH:mm} über " +
                   $"{reward.Amount.ToString("0.00000000", De)} {reward.Coin.Symbol()} entfernen?\n\n" +
                   "Nur für Eingänge, die kein Mining-Ertrag sind. Die Transaktion wird danach dauerhaft ignoriert.";
        if (MessageBox.Show(text, "Eintrag entfernen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _monitor.RemoveReward(reward);
        Rewards.Remove(reward);
        Raise(nameof(YearSummary));
        Recalculate();
        StatusText = "Eintrag entfernt, Transaktion wird künftig ignoriert.";
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"Mining-Zufluesse_{DateTime.Now:yyyy-MM-dd}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        _repository.ExportCsv(dialog.FileName, Rewards);
        StatusText = $"Export gespeichert: {dialog.FileName}";
    }

    // ---------- Verkäufe und Haltefrist ----------

    /// <summary>FIFO neu anwenden: Restbestände der Zuflüsse und Ergebnis je Verkauf.</summary>
    private void Recalculate()
    {
        _results = HoldingCalculator.Apply(Rewards, _disposals);

        Disposals.Clear();
        foreach (var r in _results.OrderByDescending(r => r.Disposal.SoldAtUtc))
            Disposals.Add(new DisposalRow(r));

        Raise(nameof(SalesSummary));
    }

    private void AddDisposal()
    {
        if (!TryParseDecimal(NewSaleAmount, out var amount) || amount <= 0)
        {
            StatusText = "Menge ungültig.";
            return;
        }
        if (!TryParseDecimal(NewSaleProceeds, out var proceeds) || proceeds < 0)
        {
            StatusText = "Erlös ungültig.";
            return;
        }

        var available = Rewards.Where(r => r.Coin == NewSaleCoin).Sum(r => r.Remaining);
        if (amount > available &&
            MessageBox.Show($"Verkauft werden sollen {amount.ToString("0.00000000", De)} {NewSaleCoin.Symbol()}, " +
                            $"dokumentiert sind nur {available.ToString("0.00000000", De)} im Bestand.\n\n" +
                            "Trotzdem erfassen? Der Überhang wird als \"ohne dokumentierten Zufluss\" markiert.",
                            "Verkauf erfassen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Uhrzeit 12:00 lokal, damit ein Verkauf am Zuflusstag nicht vor dem Zufluss einsortiert wird
        var local = NewSaleDate!.Value.Date.AddHours(12);
        _disposals.Add(new Disposal
        {
            Coin = NewSaleCoin,
            SoldAtUtc = local.ToUniversalTime(),
            Amount = amount,
            ProceedsEur = proceeds,
            Note = NewSaleNote.Trim()
        });
        _repository.SaveDisposals(_disposals);

        NewSaleAmount = string.Empty;
        NewSaleProceeds = string.Empty;
        NewSaleNote = string.Empty;
        Recalculate();
        StatusText = "Verkauf erfasst.";
    }

    private void RemoveSelectedDisposal()
    {
        if (SelectedDisposal is not { } row) return;
        if (MessageBox.Show($"Verkauf vom {row.SoldAtLocal:dd.MM.yyyy} über {row.Amount.ToString("0.00000000", De)} {row.CoinSymbol} löschen?",
                            "Verkauf löschen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _disposals.RemoveAll(d => d.Id == row.Disposal.Id);
        _repository.SaveDisposals(_disposals);
        Recalculate();
        StatusText = "Verkauf gelöscht.";
    }

    private void ExportDisposals()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"Verkaeufe_{DateTime.Now:yyyy-MM-dd}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        _repository.ExportDisposalsCsv(dialog.FileName, _results);
        StatusText = $"Export gespeichert: {dialog.FileName}";
    }

    /// <summary>Mit Komma deutsch, ohne Komma mit Punkt als Dezimaltrenner.</summary>
    private static bool TryParseDecimal(string text, out decimal value)
    {
        text = text.Trim().Replace(" ", "").Replace("€", "");
        return text.Contains(',')
            ? decimal.TryParse(text, NumberStyles.Number, De, out value)
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    // ---------- intern ----------

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _monitor.NewRewardDetected -= OnNewRewardDetected;
        _monitor.RewardUpdated -= OnRewardUpdated;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
