using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// Gestaltung der Kiosk-Anzeige (0.9.8): Farben, Schriftgröße, Animationen und frei angeordnete Panels in einem
/// 12-Spalten-Raster. Mehrere benannte Designs; jeder Kiosk-Link nutzt eins, die PIN-Anmeldung das Standard-Design.
/// </summary>
public sealed class KioskDesign
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Vorlage, aus der die Farben stammen (nur zur Anzeige im Designer).</summary>
    public string Preset { get; set; } = "bitcoin";
    public bool IsDefault { get; set; }
    public KioskColors Colors { get; set; } = new();
    /// <summary>Schriftgröße relativ (0,8–1,6).</summary>
    public double FontScale { get; set; } = 1;
    public KioskAnimations Animations { get; set; } = new();
    public List<KioskPanel> Panels { get; set; } = [];
}

public sealed class KioskColors
{
    public string Background { get; set; } = "#0d0f14";
    public string Card { get; set; } = "#171b24";
    public string Text { get; set; } = "#f2f4f8";
    public string Muted { get; set; } = "#8a93a6";
    public string Accent { get; set; } = "#f7931a";
    public string Good { get; set; } = "#3fcf8e";
    public string Warn { get; set; } = "#f5b942";
    public string Bad { get; set; } = "#ff5c5c";
}

public sealed class KioskAnimations
{
    /// <summary>Zahlen laufen beim Ändern zum neuen Wert.</summary>
    public bool CountUp { get; set; } = true;
    /// <summary>Warnungen pulsieren.</summary>
    public bool PulseAlerts { get; set; } = true;
    /// <summary>Panels blenden beim Öffnen nacheinander ein.</summary>
    public bool FadeIn { get; set; } = true;
    /// <summary>Langsam wandernder Farbverlauf im Hintergrund.</summary>
    public bool MovingBackground { get; set; }
    /// <summary>Leuchtende Akzentwerte.</summary>
    public bool Glow { get; set; }
}

public sealed class KioskPanel
{
    /// <summary>Art des Panels, siehe <see cref="KioskDesigns.PanelTypes"/>.</summary>
    public string Type { get; set; } = "hashrate";
    /// <summary>Breite in Spalten (1–12).</summary>
    public int ColSpan { get; set; } = 3;
    /// <summary>Höhe in Zeilen (1–4).</summary>
    public int RowSpan { get; set; } = 1;
    /// <summary>Eigene Überschrift (leer = Standard).</summary>
    public string Title { get; set; } = "";
    /// <summary>Text des Panels „Eigener Text“.</summary>
    public string Text { get; set; } = "";
}

public static class KioskDesigns
{
    public static readonly string[] PanelTypes =
        ["title", "clock", "hashrate", "power", "efficiency", "online", "cost", "price", "maxtemp", "alerts", "miners", "chart", "fans", "sensors", "text"];

    public const int MaxDesigns = 20;
    public const int MaxPanels = 30;

    /// <summary>Farbvorlagen (Name → Farben).</summary>
    public static readonly IReadOnlyDictionary<string, KioskColors> Presets = new Dictionary<string, KioskColors>
    {
        ["bitcoin"] = new(),
        ["light"] = new() { Background = "#f4f5f7", Card = "#ffffff", Text = "#111827", Muted = "#6b7280", Accent = "#c96f00", Good = "#16a34a", Warn = "#b7791f", Bad = "#dc2626" },
        ["ocean"] = new() { Background = "#071a2c", Card = "#0e2a44", Text = "#e6f1ff", Muted = "#8fb3d9", Accent = "#4cc9f0", Good = "#4ade80", Warn = "#fbbf24", Bad = "#f87171" },
        ["matrix"] = new() { Background = "#000000", Card = "#04140a", Text = "#c8ffd4", Muted = "#4f8a5e", Accent = "#00ff66", Good = "#00ff66", Warn = "#e5ff00", Bad = "#ff3355" },
        ["contrast"] = new() { Background = "#000000", Card = "#111111", Text = "#ffffff", Muted = "#bbbbbb", Accent = "#ffd400", Good = "#00e676", Warn = "#ffd400", Bad = "#ff1744" },
        ["sunset"] = new() { Background = "#1a1020", Card = "#2a1830", Text = "#fff1f2", Muted = "#c4a1b5", Accent = "#ff7a59", Good = "#7ee0a1", Warn = "#ffc857", Bad = "#ff4d6d" },
    };

    /// <summary>Standardaufbau: Titel und Uhr, Warnungen, vier Kennzahlen, Miner, Verlauf, Strompreis und Kosten.</summary>
    public static List<KioskPanel> DefaultPanels() =>
    [
        new() { Type = "title", ColSpan = 9 }, new() { Type = "clock", ColSpan = 3 },
        new() { Type = "alerts", ColSpan = 12 },
        new() { Type = "hashrate" }, new() { Type = "power" }, new() { Type = "efficiency" }, new() { Type = "online" },
        new() { Type = "miners", ColSpan = 12, RowSpan = 2 },
        new() { Type = "chart", ColSpan = 8, RowSpan = 2 }, new() { Type = "price", ColSpan = 4 }, new() { Type = "cost", ColSpan = 4 },
    ];

    public static KioskDesign Create(string name, string preset) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..10],
        Name = name,
        Preset = Presets.ContainsKey(preset) ? preset : "bitcoin",
        Colors = Clone(Presets.TryGetValue(preset, out var c) ? c : Presets["bitcoin"]),
        Panels = DefaultPanels(),
    };

    private static KioskColors Clone(KioskColors c) => new()
    {
        Background = c.Background, Card = c.Card, Text = c.Text, Muted = c.Muted, Accent = c.Accent, Good = c.Good, Warn = c.Warn, Bad = c.Bad,
    };

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Eingaben aus dem Browser prüfen und begrenzen; wirft bei ungültigen Werten.</summary>
    public static void Validate(KioskDesign d)
    {
        d.Name = (d.Name ?? "").Trim();
        if (d.Name.Length == 0) throw new LocalizedException("Bitte einen Namen angeben.");
        if (d.Name.Length > 40) d.Name = d.Name[..40];
        d.Preset = Presets.ContainsKey(d.Preset ?? "") ? d.Preset! : "custom";
        d.Colors ??= new KioskColors();
        foreach (var v in new[] { d.Colors.Background, d.Colors.Card, d.Colors.Text, d.Colors.Muted, d.Colors.Accent, d.Colors.Good, d.Colors.Warn, d.Colors.Bad })
            if (v is null || !Hex.IsMatch(v)) throw new LocalizedException("Farben bitte als #RRGGBB angeben.");
        d.FontScale = Math.Round(Math.Clamp(d.FontScale, 0.8, 1.6), 2);
        d.Animations ??= new KioskAnimations();
        d.Panels ??= [];
        if (d.Panels.Count > MaxPanels) throw new LocalizedException("Höchstens {0} Panels.", MaxPanels);
        foreach (var p in d.Panels)
        {
            if (!PanelTypes.Contains(p.Type)) throw new LocalizedException("Unbekanntes Panel: {0}", p.Type ?? "");
            p.ColSpan = Math.Clamp(p.ColSpan, 1, 12);
            p.RowSpan = Math.Clamp(p.RowSpan, 1, 4);
            p.Title = (p.Title ?? "").Trim() is var t && t.Length > 40 ? t[..40] : (p.Title ?? "").Trim();
            p.Text = (p.Text ?? "").Trim() is var x && x.Length > 300 ? x[..300] : (p.Text ?? "").Trim();
        }
    }
}
