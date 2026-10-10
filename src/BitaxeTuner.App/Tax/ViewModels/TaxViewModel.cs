using System.Collections.ObjectModel;
using BitaxeTuner.Core.Config;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Tax.ViewModels;

/// <summary>Adresse eines Miners, die ins Steuer-Modul übernommen werden kann.</summary>
public record MinerAddressCandidate(string DeviceName, string Host, string Address);

public sealed class TaxViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Zahlen-/Datumsformat der gewählten Sprache.</summary>
    private static CultureInfo De => L.Culture;

    private readonly WalletMonitorService _monitor;
    private readonly TaxLogRepository _repository;
    private readonly Func<IEnumerable<MinerAddressCandidate>> _minerAddresses;

    public ObservableCollection<WalletAddress> Wallets { get; } = new();
    public ObservableCollection<MinedReward> Rewards { get; } = new();
    public ObservableCollection<DisposalRow> Disposals { get; } = new();

    private List<Disposal> _disposals = new();
    private List<DisposalResult> _results = new();
    public IEnumerable<CoinType> AvailableCoins { get; } = Enum.GetValues<CoinType>();
    /// <summary>0.9.12: Wallets nur für Coins mit Blockchain-Abfrage; Verkäufe für alle Coins.</summary>
    public IEnumerable<CoinType> WalletCoins { get; } = CoinTypeExtensions.WalletCoins;

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

    private string _statusText = L.T("Bereit.");
    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }

    /// <summary>0.9.11: eingestellte Währung (ISO-Code); Beträge der Übersicht darin.</summary>
    public Func<string> Currency { get; set; } = () => "EUR";
    private CurrencyInfo Cur => Currencies.Get(Currency());

    /// <summary>Betrag in der eingestellten Währung („12,34 €“, „12,34 $“).</summary>
    private string Money(decimal v) => v.ToString("N2", De) + " " + Cur.Symbol;

    /// <summary>Summe aller Werte im laufenden Kalenderjahr (in der eingestellten Währung).</summary>
    public string YearSummary
    {
        get
        {
            var year = DateTime.Now.Year;
            var code = Cur.Code;
            var inYear = Rewards.Where(r => r.ReceivedAtLocal.Year == year).ToList();
            var sum = inYear.Sum(r => r.ValueIn(code) ?? 0);
            var missing = inYear.Count(r => r.ValueIn(code) is null);
            var text = L.T("{0}: {1} Zuflüsse · {2}", year, inYear.Count, Money(sum));
            if (missing > 0) text += L.T(" · {0} ohne Kurs", missing);
            if (!Cur.IsEuro) text += L.T(" · Beträge in {0}; das deutsche Steuerrecht rechnet in Euro (Euro-Kurse stehen im CSV)", code);
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
            if (inYear.Count == 0) return L.T("{0}: keine Verkäufe erfasst", year);

            var gain = inYear.Sum(r => r.TaxableGainEur);
            var text = Cur.IsEuro
                ? L.T("{0}: {1} Verkäufe · steuerpflichtiger Gewinn {2} € (Freigrenze 1.000 €)", year, inYear.Count, gain.ToString("N2", De))
                : L.T("{0}: {1} Verkäufe · steuerpflichtiger Gewinn {2}", year, inYear.Count, Money(gain));
            if (inYear.Any(r => r.MissingPrice)) text += L.T(" · Kurs fehlt");
            if (inYear.Any(r => r.UnmatchedAmount > 0)) text += L.T(" · Menge ohne Zufluss");
            if (inYear.Any(r => r.OtherCurrency)) text += L.T(" · Erlös in anderer Währung erfasst");
            if (inYear.Sum(r => r.UncertainGainEur) is > 0 and var u) text += L.T(" · davon {0} mit 0 Anschaffung (Kurs fehlt/ohne Zufluss)", Money(u));
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
        _monitor.RewardsReloaded += () => OnUi(() =>
        {
            Rewards.Clear();
            foreach (var r in _monitor.LoadRewards().OrderByDescending(r => r.ReceivedAtUtc)) Rewards.Add(r);
            Raise(nameof(YearSummary));
            Recalculate();
        });
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
        if (_repository.Warning is { } warning) StatusText = warning;
    }

    // ---------- Wallets ----------

    private void AddWallet()
    {
        var address = NewAddress.Trim();
        if (_monitor.Contains(address))
        {
            StatusText = L.T("Adresse ist bereits eingetragen.");
            return;
        }

        var wallet = new WalletAddress
        {
            Address = address,
            Coin = NewCoin,
            Label = string.IsNullOrWhiteSpace(NewLabel) ? L.T("{0}-Wallet", NewCoin.Symbol()) : NewLabel.Trim()
        };

        if (_monitor.AddWallet(wallet)) Wallets.Add(wallet);

        NewAddress = string.Empty;
        NewLabel = string.Empty;
        StatusText = L.T("'{0}' hinzugefügt.", wallet.Label);
    }

    private void RemoveSelectedWallet()
    {
        if (SelectedWallet is null) return;
        if (MessageBox.Show(L.T("'{0}' aus der Überwachung entfernen?\nBereits dokumentierte Zuflüsse bleiben erhalten.", SelectedWallet.Label),
                            L.T("Wallet entfernen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _monitor.RemoveWallet(SelectedWallet.Id);
        Wallets.Remove(SelectedWallet);
        StatusText = L.T("Wallet entfernt.");
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

        var text = L.T("{0} Adresse(n) übernommen, {1} bereits vorhanden.", added, skipped);
        if (ambiguous.Count > 0)
            text += L.T(" Coin bei {0} bitte prüfen (Legacy-Adresse, BCH angenommen).", string.Join(", ", ambiguous));
        if (added == 0 && skipped == 0)
            text = L.T("Keine Adresse gefunden. Miner müssen online sein oder eine Wallet-Adresse in den Einstellungen haben.");
        StatusText = text;
    }

    // ---------- Rewards ----------

    private async Task RefreshNowAsync()
    {
        StatusText = L.T("Prüfe Wallets …");
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

            var text = L.T("{0} {1} auf {2}\n", reward.Amount.ToString("0.00000000", De), reward.Coin.Symbol(), reward.WalletLabel) +
                       L.T("Zeitpunkt: {0:g}\n", reward.ReceivedAtUtc.ToLocalTime()) +
                       (reward.ValueIn(Cur.Code) is { } value
                           ? L.T("Wert: {0} (Kurs {1})", Money(value), Money(reward.PriceIn(Cur.Code)!.Value))
                           : L.T("Kurs in {0} konnte nicht ermittelt werden – bitte nachtragen.", Cur.Code));
            MessageBox.Show(text, L.T("Neuer Zufluss dokumentiert"), MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    /// <summary>
    /// Nach manueller Bearbeitung von Kurs oder Notiz in der Tabelle. Wurde der
    /// Kurs geändert, wird er als manuell markiert und nie automatisch überschrieben.
    /// </summary>
    public void SaveReward(MinedReward reward)
    {
        var stored = _monitor.LoadRewards().FirstOrDefault(r => r.Id == reward.Id);
        var code = Cur.Code;
        if (stored is null || stored.PriceIn(code) != reward.PriceIn(code))
            reward.SetPrice(code, reward.PriceIn(code), null, L.T("manuell eingetragen am {0:d}", DateTime.Now));

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
            row.OtherPrices = updated.OtherPrices;
            row.ViewCurrency = "";   // Anzeige neu lesen
            row.ViewCurrency = Cur.Code;
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

        var text = L.T("Eintrag vom {0:g} über ", reward.ReceivedAtLocal) +
                   L.T("{0} {1} entfernen?\n\n", reward.Amount.ToString("0.00000000", De), reward.Coin.Symbol()) +
                   L.T("Nur für Eingänge, die kein Mining-Ertrag sind. Die Transaktion wird danach dauerhaft ignoriert.");
        if (MessageBox.Show(text, L.T("Eintrag entfernen"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _monitor.RemoveReward(reward);
        Rewards.Remove(reward);
        Raise(nameof(YearSummary));
        Recalculate();
        StatusText = L.T("Eintrag entfernt, Transaktion wird künftig ignoriert.");
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = L.T("CSV-Datei (*.csv)|*.csv"),
            FileName = $"Mining-Zufluesse_{DateTime.Now:yyyy-MM-dd}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        _repository.ExportCsv(dialog.FileName, Rewards, currency: Cur.Code);   // Restbestand ist durch Recalculate() schon gesetzt
        StatusText = L.T("Export gespeichert: {0}", dialog.FileName);
    }

    // ---------- Verkäufe und Haltefrist ----------

    /// <summary>FIFO neu anwenden: Restbestände der Zuflüsse und Ergebnis je Verkauf (in der eingestellten Währung).</summary>
    public void Recalculate()
    {
        var code = Cur.Code;
        foreach (var r in Rewards) r.ViewCurrency = code;
        _results = HoldingCalculator.Apply(Rewards, _disposals, code);
        Raise(nameof(YearSummary));

        Disposals.Clear();
        foreach (var r in _results.OrderByDescending(r => r.Disposal.SoldAtUtc))
            Disposals.Add(new DisposalRow(r, code));

        Raise(nameof(SalesSummary));
    }

    private void AddDisposal()
    {
        if (!Core.Tax.Services.TaxEditor.TryParseDecimal(NewSaleAmount, out var amount) || amount <= 0)
        {
            StatusText = L.T("Menge ungültig.");
            return;
        }
        if (!Core.Tax.Services.TaxEditor.TryParseEur(NewSaleProceeds, out var proceeds) || proceeds < 0)
        {
            StatusText = L.T("Erlös ungültig.");
            return;
        }

        var available = Rewards.Where(r => r.Coin == NewSaleCoin).Sum(r => r.Remaining);
        if (amount > available &&
            MessageBox.Show(L.T("Verkauft werden sollen {0} {1}, ", amount.ToString("0.00000000", De), NewSaleCoin.Symbol()) +
                            L.T("dokumentiert sind nur {0} im Bestand.\n\n", available.ToString("0.00000000", De)) +
                            L.T("Trotzdem erfassen? Der Überhang wird als \"ohne dokumentierten Zufluss\" markiert."),
                            L.T("Verkauf erfassen"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Uhrzeit 12:00 lokal, damit ein Verkauf am Zuflusstag nicht vor dem Zufluss einsortiert wird
        var local = NewSaleDate!.Value.Date.AddHours(12);
        _disposals.Add(new Disposal
        {
            Coin = NewSaleCoin,
            SoldAtUtc = Core.Tax.TaxTime.FromTax(local),
            Amount = amount,
            ProceedsEur = Cur.IsEuro ? proceeds : 0,
            Currency = Cur.IsEuro ? null : Cur.Code,
            Proceeds = Cur.IsEuro ? null : proceeds,
            Note = NewSaleNote.Trim()
        });
        _repository.SaveDisposals(_disposals);

        NewSaleAmount = string.Empty;
        NewSaleProceeds = string.Empty;
        NewSaleNote = string.Empty;
        Recalculate();
        StatusText = L.T("Verkauf erfasst.");
    }

    private void RemoveSelectedDisposal()
    {
        if (SelectedDisposal is not { } row) return;
        if (MessageBox.Show(L.T("Verkauf vom {0:d} über {1} {2} löschen?", row.SoldAtLocal, row.Amount.ToString("0.00000000", De), row.CoinSymbol),
                            L.T("Verkauf löschen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _disposals.RemoveAll(d => d.Id == row.Disposal.Id);
        _repository.SaveDisposals(_disposals);
        Recalculate();
        StatusText = L.T("Verkauf gelöscht.");
    }

    private void ExportDisposals()
    {
        var dialog = new SaveFileDialog
        {
            Filter = L.T("CSV-Datei (*.csv)|*.csv"),
            FileName = $"Verkaeufe_{DateTime.Now:yyyy-MM-dd}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        _repository.ExportDisposalsCsv(dialog.FileName, _results, Cur.Code);
        StatusText = L.T("Export gespeichert: {0}", dialog.FileName);
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
