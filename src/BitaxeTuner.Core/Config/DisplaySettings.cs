namespace BitaxeTuner.Core.Config;

/// <summary>E-Paper-Anzeige (7,5″, 800 × 480, schwarz/weiß/rot) am Pico. Gespeichert in config.json („Display“).</summary>
public sealed class DisplaySettings
{
    /// <summary>Hersteller-Empfehlung: nicht öfter als alle 3 Minuten neu aufbauen.</summary>
    public const int MinIntervalMinutes = 3;

    public bool Enabled { get; set; }

    /// <summary>
    /// 0.9.7: "fans" = Anzeige am Lüfter-Pico (wie bisher), "own" = eigener Pico, direkt auf das E-Paper gesteckt
    /// (Waveshare-Belegung), per USB oder WLAN. Additiv.
    /// </summary>
    public string Device { get; set; } = "fans";

    /// <summary>Eigener Display-Pico: "usb" oder "wlan".</summary>
    public string Connection { get; set; } = "usb";

    /// <summary>Eigener Display-Pico per USB: "auto" oder fester Port.</summary>
    public string Port { get; set; } = "auto";

    /// <summary>Eigener Display-Pico per WLAN: Gerätename (z. B. bitaxetuner-display.local) oder IP-Adresse.</summary>
    public string NetworkHost { get; set; } = "";

    /// <summary>Zuletzt erreichte IP-Adresse (Rückfall, falls der Gerätename nicht auflöst).</summary>
    public string NetworkIp { get; set; } = "";

    public bool OwnDevice => Device == "own";

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

    /// <summary>
    /// 0.9.9: Wann das Blockfund-Vollbild endet – "hours" (Taste 1 oder nach <see cref="BlockFoundHoldHours"/>, wie bisher)
    /// oder "button" (bleibt wie die Warnungen stehen, bis Taste 1 bzw. „Anzeige weiter“ im Browser quittiert). Additiv.
    /// </summary>
    public string BlockFoundUntil { get; set; } = "hours";

    /// <summary>0.9.7: Schwarz und Weiß tauschen (helle Schrift auf schwarzem Grund), Rot bleibt. Additiv.</summary>
    public bool Inverted { get; set; }

    /// <summary>Warnungen als Vollbild statt nur als rote Zeile (Taste 1 quittiert bis zur nächsten neuen Warnung).</summary>
    public bool AlarmFullscreen { get; set; } = true;

    /// <summary>Neuer Best-Diff-Rekord: einmal als Sonderanzeige.</summary>
    public bool BestDiffNotice { get; set; } = true;

    /// <summary>Welche Seiten es gibt; Taste 1 blättert.</summary>
    public DisplayPages Pages { get; set; } = new();

    /// <summary>Bei jeder regelmäßigen Aktualisierung zur nächsten Seite wechseln (sonst nur per Taste 1).</summary>
    public bool RotatePages { get; set; } = true;

    /// <summary>0.9.7, Seite „Kurs“: "btc", "bch" oder "both". Additiv.</summary>
    public string PriceCoins { get; set; } = "btc";

    /// <summary>0.9.7, Tagesbilanz: "none" (Minerliste wie bisher), "hashrate", "power", "efficiency" oder "temp" als 24-h-Graph.</summary>
    public string DailyChart { get; set; } = "none";

    /// <summary>0.9.7, Monatsbilanz: Balken je Tag – "kwh", "cost", "income" oder "hashrate".</summary>
    public string MonthlyChart { get; set; } = "kwh";

    /// <summary>0.9.7, Seite „QR-Code“: eigene Adresse; leer = Adresse dieses Servers im Heimnetz.</summary>
    public string QrUrl { get; set; } = "";
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

    // 0.9.7 – neue Seiten, standardmäßig aus (bestehende Anzeigen ändern sich nicht)
    /// <summary>Kurs BTC/BCH (CoinGecko) mit 24-h-Verlauf, bei BTC Countdown bis zur Difficulty-Anpassung (mempool.space).</summary>
    public bool Prices { get; set; }
    /// <summary>Monatsbilanz: Ertrag, kWh, Kosten, Differenz, Balken je Tag.</summary>
    public bool Monthly { get; set; }
    /// <summary>Je Miner-Gruppe eine Übersichtsseite.</summary>
    public bool Groups { get; set; }
    /// <summary>Strompreis-Ampel für die nächsten Stunden (aWATTar/Tibber).</summary>
    public bool Power { get; set; }
    /// <summary>QR-Code zur Browser-Oberfläche.</summary>
    public bool Qr { get; set; }
    /// <summary>Alle Temperaturfühler mit Warnschwelle.</summary>
    public bool Sensors { get; set; }
}
