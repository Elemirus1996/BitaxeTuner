using System.Windows;
using System.Windows.Media;

namespace BitaxeTuner.App.Themes;

/// <summary>Schaltet zwischen dunklem (BitaxeMonitor) und hellem (BitaxeTuner) Design um – zur Laufzeit.</summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static string Current { get; private set; } = Dark;

    /// <summary>Für selbst gezeichnete Elemente (Diagramme), die neu rendern müssen.</summary>
    public static event Action? Changed;

    public static void Apply(string? theme)
    {
        Current = theme == Light ? Light : Dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/BitaxeTuner;component/Themes/{(Current == Light ? "Light" : "Dark")}.xaml")
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        // Alle bisherigen Design-Wörterbücher entfernen ("Themes/Dark.xaml" aus App.xaml oder eine frühere Umschaltung)
        foreach (var old in merged.Where(d => d.Source?.OriginalString.Contains("Themes/") == true).ToList())
            merged.Remove(old);
        merged.Add(dict);
        if (_appearance is not null) ApplyAppearance(_appearance);
        Changed?.Invoke();
    }

    // ---------- 0.9.14 Hintergrund (Einstellungen → Darstellung, gleicher Wert wie der Browser-Standard) ----------

    private static Core.Config.AppearanceSettings? _appearance;

    /// <summary>Fenster-Hintergrund „BackdropBrush“ aus der Darstellung bauen (Farben aus dem aktuellen Design).</summary>
    public static void ApplyAppearance(Core.Config.AppearanceSettings a)
    {
        _appearance = a;
        if (Application.Current is null) return;
        static Color Of(string key) => (Brush(key) as SolidColorBrush)?.Color ?? Colors.Gray;
        var bg = Of("WindowBrush");
        var accent = Of("AccentBrush");
        var info = Of("InfoBrush");
        var ok = Of("OkBrush");
        var keep = 1 - Math.Clamp(a.Veil, 0, 80) / 100.0;   // Abdunkeln: Muster/Verlauf blasser
        Color Mix(Color c, double share) => System.Windows.Media.Color.FromRgb(
            (byte)(bg.R + (c.R - bg.R) * share * keep), (byte)(bg.G + (c.G - bg.G) * share * keep), (byte)(bg.B + (c.B - bg.B) * share * keep));
        Brush brush = a.Background switch
        {
            "gradient" => new RadialGradientBrush(Mix(accent, .14), bg) { Center = new Point(.9, -.1), GradientOrigin = new Point(.9, -.1), RadiusX = .9, RadiusY = 1.1 },
            "aurora" => Aurora(Mix(accent, .16), Mix(info, .12), Mix(ok, .10), a.Animations),
            "hex" => Tile(Mix(accent, .45), bg, Geometry.Parse("M28,66 L0,50 V16 L28,0 L56,16 V50 Z M28,66 V98"), new Rect(0, 0, 56, 98)),
            "circuit" => Tile(Mix(info, .45), bg, Geometry.Parse("M0,30 H40 L50,40 H80 M60,0 V20 L70,30 M120,90 H80 L70,80 H40 M20,120 V90 L30,80"), new Rect(0, 0, 120, 120)),
            "dots" => Tile(Mix(Of("MutedTextBrush"), .45), bg, new EllipseGeometry(new Point(11, 11), 1.1, 1.1), new Rect(0, 0, 22, 22), fill: true),
            _ => new SolidColorBrush(bg),
        };
        if (brush.CanFreeze && a.Background != "aurora") brush.Freeze();
        Application.Current.Resources["BackdropBrush"] = brush;
    }

    private static Brush Tile(Color line, Color bg, Geometry g, Rect box, bool fill = false)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(box)));
        group.Children.Add(new GeometryDrawing(fill ? new SolidColorBrush(line) : null, fill ? null : new Pen(new SolidColorBrush(line), 1), g));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = box, ViewportUnits = BrushMappingMode.Absolute, Viewbox = box, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
    }

    private static Brush Aurora(Color a, Color b, Color c, bool animate)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection { new(a, 0), new(b, .4), new(c, .7), new(a, 1) }, 25);
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            // Langsames Drehen des Verlaufs – fällt aus, wenn Windows Animationen abgeschaltet hat
            var rot = new RotateTransform(0, .5, .5);
            brush.RelativeTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(90)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
        return brush;
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Color Color(string key) =>
        Application.Current?.TryFindResource(key) is Color c ? c : Colors.Gray;
}
