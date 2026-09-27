namespace BitaxeTuner.Core.Config;

/// <summary>E-Paper-Anzeige (7,5″, 800 × 480, schwarz/weiß/rot) am Pico. Gespeichert in config.json („Display“).</summary>
public sealed class DisplaySettings
{
    /// <summary>Hersteller-Empfehlung: nicht öfter als alle 3 Minuten neu aufbauen.</summary>
    public const int MinIntervalMinutes = 3;

    public bool Enabled { get; set; }

    /// <summary>Regelmäßig neu aufbauen (Minuten, mindestens 3). Alarme und Tastendrücke zeigen sich früher.</summary>
    public int IntervalMinutes { get; set; } = 5;

    public string Title { get; set; } = "BitaxeTuner";

    /// <summary>Nachts keine regelmäßigen Aktualisierungen (Alarme weiterhin).</summary>
    public bool QuietEnabled { get; set; }
    public int QuietFromHour { get; set; } = 23;
    public int QuietToHour { get; set; } = 7;

    /// <summary>Taster am Pico (GP1/3/5/7) auswerten: Anzeige weiter / Automatik / 100 % (lang: aus) / Neustart.</summary>
    public bool ButtonsEnabled { get; set; } = true;

    /// <summary>Langer Druck auf Taste 4 startet neben dem Pico auch den Rechner neu (nur Linux-Paket/Pi).</summary>
    public bool AllowSystemReboot { get; set; } = true;

    /// <summary>Vollbild „Block gefunden“ – bis Taste 1 oder <see cref="BlockFoundHoldHours"/> vorbei sind.</summary>
    public bool BlockFoundScreen { get; set; } = true;
    public int BlockFoundHoldHours { get; set; } = 24;

    /// <summary>Warnungen als Vollbild statt nur als rote Zeile (Taste 1 quittiert bis zur nächsten neuen Warnung).</summary>
    public bool AlarmFullscreen { get; set; } = true;

    /// <summary>Neuer Best-Diff-Rekord: einmal als Sonderanzeige.</summary>
    public bool BestDiffNotice { get; set; } = true;

    /// <summary>Welche Seiten es gibt; Taste 1 blättert.</summary>
    public DisplayPages Pages { get; set; } = new();

    /// <summary>Bei jeder regelmäßigen Aktualisierung zur nächsten Seite wechseln (sonst nur per Taste 1).</summary>
    public bool RotatePages { get; set; } = true;
}

public sealed class DisplayPages
{
    public bool Overview { get; set; } = true;
    public bool Daily { get; set; } = true;
    public bool Chart { get; set; } = true;
    /// <summary>Nur solange ein Dauertest läuft.</summary>
    public bool Soak { get; set; } = true;
    /// <summary>Pool-Status je Miner und Stand des Bitcoin-Netzwerks (mempool.space).</summary>
    public bool Network { get; set; }
}
