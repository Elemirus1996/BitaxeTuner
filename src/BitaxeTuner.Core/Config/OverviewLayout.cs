using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.11 Übersicht-Designer: Aufbau der Übersicht im Browser aus Panels in einem 12-Spalten-Raster – wie beim Kiosk.
/// Hinweise (Updates, Fehler, Einführung) stehen immer darüber. Null in der Konfiguration = Standardaufbau wie bisher.
/// </summary>
public sealed class OverviewLayout
{
    public List<OverviewPanel> Panels { get; set; } = [];
}

public sealed class OverviewPanel
{
    /// <summary>Art des Panels, siehe <see cref="OverviewLayouts.PanelTypes"/>.</summary>
    public string Type { get; set; } = "kpis";
    /// <summary>Breite in Spalten (1–12); auf dem Handy immer volle Breite.</summary>
    public int ColSpan { get; set; } = 12;
    /// <summary>Eigene Überschrift (leer = Standard).</summary>
    public string Title { get; set; } = "";
    /// <summary>Text des Panels „Eigener Text“.</summary>
    public string Text { get; set; } = "";
}

public static class OverviewLayouts
{
    /// <summary>kpis = die Kennzahlen-Reihe wie bisher; einzelne Kennzahlen gibt es auch als eigene Panels.</summary>
    public static readonly string[] PanelTypes =
        ["kpis", "hashrate", "power", "efficiency", "maxtemp", "price", "cost", "chart", "miners", "plugs", "soak", "news", "text"];

    public const int MaxPanels = 30;

    /// <summary>Standard: genau der bisherige Aufbau.</summary>
    public static OverviewLayout Default() => new()
    {
        Panels = [new() { Type = "kpis" }, new() { Type = "chart" }, new() { Type = "miners" }, new() { Type = "plugs" }, new() { Type = "soak" }],
    };

    /// <summary>Eingaben aus dem Browser prüfen und begrenzen.</summary>
    public static void Validate(OverviewLayout layout)
    {
        layout.Panels ??= [];
        layout.Panels.RemoveAll(p => p is null);
        if (layout.Panels.Count > MaxPanels) throw new LocalizedException("Höchstens {0} Panels.", MaxPanels);
        foreach (var p in layout.Panels)
        {
            if (!PanelTypes.Contains(p.Type)) throw new LocalizedException("Unbekannte Panel-Art.");
            p.ColSpan = Math.Clamp(p.ColSpan, 1, 12);
            p.Title = Clip(p.Title, 60);
            p.Text = Clip(p.Text, 500);
        }
    }

    private static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }
}
