using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Monitoring;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BitaxeTuner.App.ViewModels;

/// <summary>
/// Miner-Logs eines Geräts: Live über den WebSocket des Miners, dazu optional der bisherige Puffer.
/// Die Live-Verbindung ist nur offen, solange der Tab sichtbar ist (der Miner hat nur wenige WebSocket-Plätze).
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 5000;

    private readonly MinerConnection _connection;
    private readonly ConcurrentQueue<LogLine> _incoming = new();
    private readonly DispatcherTimer _flush;
    private IDisposable? _subscription;

    public LogViewModel(MinerConnection connection)
    {
        _connection = connection;
        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = o => o is LogLine l && Matches(l);
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Flush(),
            Application.Current.Dispatcher);
    }

    public ObservableCollection<LogLine> Lines { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _statusText = "Aus";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showErrors = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showDebug = true;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadBufferCommand))] private bool _loadingBuffer;

    /// <summary>Wird ausgelöst, nachdem neue Zeilen angehängt wurden (für Auto-Scroll).</summary>
    public event Action? LinesAppended;

    partial void OnFilterTextChanged(string value) => View.Refresh();
    partial void OnShowErrorsChanged(bool value) => View.Refresh();
    partial void OnShowWarningsChanged(bool value) => View.Refresh();
    partial void OnShowInfoChanged(bool value) => View.Refresh();
    partial void OnShowDebugChanged(bool value) => View.Refresh();

    private bool Matches(LogLine l)
    {
        var levelOk = l.Level switch
        {
            LogLevel.Error => ShowErrors,
            LogLevel.Warning => ShowWarnings,
            LogLevel.Debug or LogLevel.Verbose => ShowDebug,
            LogLevel.App => true,
            _ => ShowInfo,
        };
        if (!levelOk) return false;
        var f = FilterText.Trim();
        return f.Length == 0 || l.Tag.Contains(f, StringComparison.OrdinalIgnoreCase) || l.Message.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tab sichtbar/unsichtbar: Live-Verbindung nur, solange jemand mitliest.</summary>
    public void SetVisible(bool visible)
    {
        if (visible && !IsLive) StartLive();
        else if (!visible && IsLive) StopLive();
    }

    [RelayCommand]
    private void ToggleLive()
    {
        if (IsLive) StopLive();
        else StartLive();
    }

    private void StartLive()
    {
        if (IsLive) return;
        IsLive = true;
        _flush.Start();
        // Gemeinsame Verbindung: laufen Log-Alarme für diesen Miner, wird deren Verbindung mitbenutzt
        _subscription = _connection.Logs.Subscribe(
            line => _incoming.Enqueue(line),
            status => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = status));
        AddAppLine("Live-Mitlesen gestartet");
    }

    private void StopLive()
    {
        if (!IsLive) return;
        IsLive = false;
        _subscription?.Dispose();
        _subscription = null;
        StatusText = _connection.Logs.IsRunning ? "Aus (Verbindung bleibt für Log-Alarme offen)" : "Aus";
        AddAppLine("Live-Mitlesen beendet");
    }

    /// <summary>Bisherigen Puffer des Miners laden (letzte 3.000 Zeilen, vor die Live-Zeilen).</summary>
    [RelayCommand(CanExecute = nameof(CanLoadBuffer))]
    private async Task LoadBuffer()
    {
        LoadingBuffer = true;
        StatusText = "Lade Log-Puffer …";
        try
        {
            var text = await _connection.GetLogBufferAsync();
            var lines = await Task.Run(() => LogParser.ParseBuffer(text, DateTime.Now, _connection.BootTime, 3000));
            var existing = Lines.ToList();
            Lines.Clear();
            foreach (var l in lines) Lines.Add(l);
            Lines.Add(new LogLine(DateTime.Now, LogLevel.App, null, "", $"── Ende des Puffers ({lines.Count} Zeilen) ──"));
            foreach (var l in existing) Lines.Add(l);
            Trim();
            StatusText = IsLive ? "Live verbunden" : $"{lines.Count} Zeilen aus dem Puffer";
            LinesAppended?.Invoke();
        }
        catch (MinerApiException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            LoadingBuffer = false;
        }
    }

    private bool CanLoadBuffer() => !LoadingBuffer;

    [RelayCommand]
    private void Clear() => Lines.Clear();

    [RelayCommand]
    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Textdatei (*.txt)|*.txt",
            FileName = $"{_connection.Address.Replace(':', '_')}_log_{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog() != true) return;
        var sb = new StringBuilder();
        foreach (var l in View.Cast<LogLine>()) sb.AppendLine(l.ToString());
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText = $"Gespeichert: {dialog.FileName} (enthält ggf. Wallet-Adresse aus den Stratum-Zeilen)";
    }

    /// <summary>Tuning-Änderung als Markierung in den Log einfügen (thread-sicher).</summary>
    public void AddTuningMarker(TuningEvent e) =>
        Enqueue(new LogLine(e.Time, LogLevel.App, null, "Tuning", $"── {e.SourceText}: {e.ChangeText} ──"));

    private void AddAppLine(string text) =>
        Enqueue(new LogLine(DateTime.Now, LogLevel.App, null, "", $"── {text} ──"));

    private void Enqueue(LogLine line)
    {
        _incoming.Enqueue(line);
        Application.Current?.Dispatcher.BeginInvoke(() => _flush.Start());
    }

    private void Flush()
    {
        if (Paused || _incoming.IsEmpty)
        {
            if (!IsLive && _incoming.IsEmpty) _flush.Stop();
            return;
        }
        while (_incoming.TryDequeue(out var line)) Lines.Add(line);
        Trim();
        LinesAppended?.Invoke();
    }

    private void Trim()
    {
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _flush.Stop();
    }
}
