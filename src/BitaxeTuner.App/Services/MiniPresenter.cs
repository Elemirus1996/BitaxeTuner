using BitaxeTuner.App.Views;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Display;

namespace BitaxeTuner.App.Services;

/// <summary>
/// 0.9.25: Tray-Symbol und Mini-Anzeige gemeinsam führen – gleiche Darstellung im Modus „Lokal“ (Werte aus dem Hub) und
/// „Server“ (Werte aus der Status-Abfrage). Der Menüpunkt „Mini-Anzeige“ im Infobereich schaltet das Fenster.
/// </summary>
public sealed class MiniPresenter : IDisposable
{
    private readonly AppConfig _config;
    private readonly TrayService _tray;
    private MiniWindow? _window;
    private MiniStatus? _last;

    public MiniPresenter(AppConfig config, TrayService tray)
    {
        _config = config;
        _tray = tray;
        _tray.MiniToggled += Set;
        _tray.SetMiniChecked(config.Mini.Enabled);
        if (config.Mini.Enabled) Open();
    }

    public void Update(MiniStatus s)
    {
        _last = s;
        _tray.Update(s);
        _window?.Update(s);
    }

    private void Set(bool on)
    {
        _config.Mini.Enabled = on;
        Save();
        if (on) Open(); else CloseWindow();
    }

    private void Open()
    {
        if (_window is not null) return;
        _window = new MiniWindow(_config);
        _window.ClosedByUser += () =>
        {
            _window = null;
            _config.Mini.Enabled = false;
            Save();
            _tray.SetMiniChecked(false);
        };
        _window.Show();
        if (_last is { } s) _window.Update(s);
    }

    private void CloseWindow()
    {
        var w = _window;
        _window = null;
        w?.CloseByCode();
    }

    private void Save()
    {
        try { _config.Save(); } catch { /* nicht kritisch */ }
    }

    public void Dispose() => CloseWindow();
}
