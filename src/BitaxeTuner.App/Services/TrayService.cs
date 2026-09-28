using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Services;

public enum TrayState
{
    Ok,
    Warning,
    Error,
    Unknown
}

/// <summary>
/// Symbol im Infobereich mit Statusfarbe und Tooltip. Doppelklick oder
/// "Öffnen" holt das Fenster zurück, "Beenden" schließt die Anwendung.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons = new();
    private readonly List<IntPtr> _handles = new();
    private TrayState _state = (TrayState)(-1);

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icons[TrayState.Ok] = CreateIcon(Drawing.Color.FromArgb(0x4E, 0xC9, 0xA0));
        _icons[TrayState.Warning] = CreateIcon(Drawing.Color.FromArgb(0xE0, 0xB4, 0x4A));
        _icons[TrayState.Error] = CreateIcon(Drawing.Color.FromArgb(0xD9, 0x5B, 0x5B));
        _icons[TrayState.Unknown] = CreateIcon(Drawing.Color.FromArgb(0x5A, 0x64, 0x78));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Öffnen"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Beenden"), null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "BitaxeTuner"
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        Update(TrayState.Unknown, L.T("BitaxeTuner – startet"));
    }

    public void Update(TrayState state, string tooltip)
    {
        if (state != _state)
        {
            _icon.Icon = _icons[state];
            _state = state;
        }

        // NotifyIcon.Text ist auf 63 Zeichen begrenzt
        _icon.Text = tooltip.Length > 63 ? tooltip[..62] + "…" : tooltip;
    }

    /// <summary>Kurze Sprechblase, z. B. beim ersten Minimieren.</summary>
    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>Farbiger Kreis mit dunklem Rand, 32×32.</summary>
    private Drawing.Icon CreateIcon(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var dark = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x12, 0x15, 0x1A));
            using var fill = new Drawing.SolidBrush(color);
            g.FillEllipse(dark, 1, 1, 30, 30);
            g.FillEllipse(fill, 5, 5, 22, 22);
        }

        var handle = bmp.GetHicon();
        _handles.Add(handle);
        return Drawing.Icon.FromHandle(handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        foreach (var h in _handles) DestroyIcon(h);
    }
}
