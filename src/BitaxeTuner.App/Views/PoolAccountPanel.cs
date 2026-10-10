using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BitaxeTuner.App.Services;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.App.Views;

/// <summary>
/// 0.9.12 Pool-Konto (Mining-Dutch) wie in der Server-Oberfläche: Schlüssel, Abstand, Zufluss-Art für die Steuer, dazu der
/// aktuelle Stand (Guthaben je Coin, Worker mit Coin und zugeordnetem Miner). Gespeichert mit „Speichern“; nur lesend.
/// </summary>
public sealed class PoolAccountPanel : StackPanel
{
    private readonly AppHost _host;
    private readonly CheckBox _enabled = new() { Content = L.T("Pool-Konto abfragen (Mining-Dutch)"), Margin = new Thickness(0, 0, 0, 6) };
    private readonly PasswordBox _key = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _interval = new() { Width = 60 };
    private readonly ComboBox _basis = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _import = new() { Content = L.T("Pool-Buchungen als Zuflüsse in die Steuer übernehmen"), Margin = new Thickness(0, 6, 0, 4) };
    private readonly CheckBox _clearKey = new() { Content = L.T("Schlüssel löschen"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _overview = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _refresh = new() { Content = L.T("Jetzt abfragen"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };

    public PoolAccountPanel(AppHost host)
    {
        _host = host;
        var cfg = host.Hub.Config.PoolAccount;

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = L.T("Laufen deine Miner bei Mining-Dutch auf einem Konto, zeigt BitaxeTuner je Miner den Coin, den der Pool gerade gibt (auch nach einem Wechsel im Pool-Dashboard), Guthaben und Gutschriften je Coin und übernimmt die Buchungen als Zuflüsse in die Steuer – auch DGB, NMC und andere Merged-Mining-Coins. Nur lesend: der Schlüssel aus „Edit Account“ → „API Key“ kann am Pool nichts ändern; er liegt geschützt in secrets.json, nicht in config.json oder Sicherungen."),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        _enabled.IsChecked = cfg.Enabled;
        _interval.Text = cfg.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        _basis.Items.Add(new ComboBoxItem { Content = L.T("Gutschrift beim Pool (je Tag und Coin)"), Tag = PoolIncomeBasis.Credit });
        _basis.Items.Add(new ComboBoxItem { Content = L.T("Auszahlung an die Wallet"), Tag = PoolIncomeBasis.Payout });
        _basis.SelectedIndex = cfg.TaxBasis == PoolIncomeBasis.Payout ? 1 : 0;
        _import.IsChecked = cfg.TaxImport;
        _clearKey.Visibility = cfg.ApiKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _key.ToolTip = cfg.ApiKey.Length > 0 ? L.T("gespeichert – leer lassen, um ihn zu behalten") : L.T("API-Schlüssel aus „Edit Account“");

        var basisHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Text = L.T("Gutschrift: alle Gutschriften eines Coins an einem Tag als ein Zufluss (sobald der Tag vorbei ist). Auszahlung: jede Auszahlung vom Pool an deine Wallet einzeln. Beim Umschalten bleiben von Hand eingetragene Kurse erhalten. Welche Sicht für dich gilt, klärst du am besten mit deiner Steuerberatung."),
        };
        basisHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        Children.Add(hint);
        Children.Add(_enabled);
        Children.Add(Label(L.T("API-Schlüssel")));
        Children.Add(_key);
        Children.Add(_clearKey);
        Children.Add(Label(L.T("Abfrage alle … Minuten")));
        Children.Add(_interval);
        Children.Add(Label(L.T("Zufluss für die Steuer")));
        Children.Add(_basis);
        Children.Add(_import);
        Children.Add(basisHint);
        Children.Add(_refresh);
        Children.Add(_status);
        Children.Add(_overview);

        _refresh.Click += async (_, _) =>
        {
            _refresh.IsEnabled = false;
            _status.Text = L.T("Fragt gerade ab …");
            var error = await _host.Hub.PoolAccountTickAsync();
            if (error is not null) _status.Text = error;
            Show();
        };
        _host.Hub.PoolAccountUpdated += OnUpdated;
        Unloaded += (_, _) => _host.Hub.PoolAccountUpdated -= OnUpdated;
        Show();
    }

    private void OnUpdated() => Dispatcher.BeginInvoke(Show);

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 2) };

    /// <summary>Stand anzeigen (Guthaben je Coin, Worker).</summary>
    private void Show()
    {
        var st = _host.Hub.PoolAccount;
        _refresh.IsEnabled = st.Enabled && st.HasKey && !st.Busy;
        if (!st.Enabled)
        {
            _status.Text = "";
            _overview.Text = "";
            return;
        }
        _status.Text = (st.Busy ? L.T("Fragt gerade ab …")
                       : st.LastPollUtc is { } at ? L.T("Zuletzt abgefragt: {0}", at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                       : L.T("Noch nicht abgefragt."))
                     + (st.NewRewards > 0 ? " · " + L.T("{0} neue Zuflüsse seit dem Start", st.NewRewards) : "")
                     + (st.Error is { } e ? Environment.NewLine + e : "");

        var lines = new List<string>();
        foreach (var c in st.Coins)
        {
            var value = c.Price is { } p ? $" ≈ {((c.Confirmed + c.Unconfirmed) * p).ToString("N2", CultureInfo.CurrentCulture)} {st.Currency}" : "";
            lines.Add(L.T("{0}: Guthaben {1}{2} · 24 h {3} · 7 Tage {4} · Auszahlungen 30 Tage {5}", c.Coin.Symbol().PadRight(3),
                Amount(c.Confirmed + c.Unconfirmed), value, Amount(c.Credits24h), Amount(c.Credits7d), Amount(c.Payouts30d)));
        }
        if (st.Workers.Count > 0) lines.Add("");
        var states = _host.Hub.Polling.States;
        foreach (var w in st.Workers)
        {
            var miner = states.FirstOrDefault(s => _host.Hub.PoolWorkerOf(s) == w)?.Config.Name ?? "–";
            var coin = CoinTypeExtensions.FromSymbolOrName(w.NowMining)?.Symbol() ?? w.NowMining;
            lines.Add($"{(w.Alive ? "●" : "○")} {w.Name} → {miner} · {coin}{(w.MergedMining ? " + " + L.T("Merged Mining") : "")} · {w.Mode} · {(w.HashrateHs / 1e12).ToString("0.00", CultureInfo.CurrentCulture)} TH/s");
        }
        _overview.Text = string.Join(Environment.NewLine, lines);
    }

    private static string Amount(decimal v) =>
        v.ToString(Math.Abs(v) >= 1000 ? "N2" : Math.Abs(v) >= 1 ? "N4" : "N8", CultureInfo.CurrentCulture);

    public string? Validate() =>
        int.TryParse(_interval.Text.Trim(), out var m) && m is >= 10 and <= 240 ? null : L.T("Pool-Konto: Abfrage alle 10–240 Minuten.");

    /// <summary>Werte übernehmen und Änderungen protokollieren (wie im Browser).</summary>
    public void Apply(AppConfig config)
    {
        var p = config.PoolAccount;
        var basis = (_basis.SelectedItem as ComboBoxItem)?.Tag is PoolIncomeBasis b ? b : PoolIncomeBasis.Credit;
        var key = _clearKey.IsChecked == true ? "" : _key.Password.Trim().Length > 0 ? _key.Password.Trim() : p.ApiKey;
        var changes = new List<string>();
        var enabled = _enabled.IsChecked == true;
        var import = _import.IsChecked == true;
        if (p.Enabled != enabled) changes.Add(enabled ? L.T("Pool-Konto eingeschaltet.") : L.T("Pool-Konto ausgeschaltet."));
        if (key != p.ApiKey) changes.Add(key.Length > 0 ? L.T("API-Schlüssel des Pool-Kontos geändert.") : L.T("API-Schlüssel des Pool-Kontos gelöscht."));
        if (p.TaxBasis != basis)
            changes.Add(basis == PoolIncomeBasis.Payout ? L.T("Pool-Zuflüsse für die Steuer: je Auszahlung.") : L.T("Pool-Zuflüsse für die Steuer: Gutschriften je Tag."));
        if (p.TaxImport != import)
            changes.Add(import ? L.T("Pool-Buchungen werden in die Steuer übernommen.") : L.T("Pool-Buchungen werden nicht mehr in die Steuer übernommen."));
        p.Enabled = enabled;
        p.ApiKey = key;
        p.IntervalMinutes = int.TryParse(_interval.Text.Trim(), out var m) ? m : p.IntervalMinutes;
        p.TaxBasis = basis;
        p.TaxImport = import;
        p.Normalize();
        foreach (var c in changes) _host.Hub.LogEvent(null, EventCategories.Settings, c);
    }
}
