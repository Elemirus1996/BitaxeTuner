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

    /// <summary>
    /// 0.9.17: Display-Typ – "bwr75" (Waveshare 7,5″ B, schwarz/weiß/rot, wie bisher) oder "spectra73" (Waveshare 7,3″
    /// Spectra 6 / E6: schwarz, weiß, gelb, rot, blau, grün). Beide 800 × 480 an denselben Pins. Additiv.
    /// </summary>
    public string Panel { get; set; } = "bwr75";

    public static readonly string[] Panels = ["bwr75", "spectra73"];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSpectra => Panel == "spectra73";

    /// <summary>Regelmäßig neu aufbauen (Minuten, mindestens 3). Alarme und Tastendrücke zeigen sich früher.</summary>
    public int IntervalMinutes { get; set; } = 5;

    public string Title { get; set; } = "BitaxeTuner";

    /// <summary>Nachts keine regelmäßigen Aktualisierungen (Alarme weiterhin).</summary>
    public bool QuietEnabled { get; set; }
    public int QuietFromHour { get; set; } = 23;
    public int QuietToHour { get; set; } = 7;

    /// <summary>
    /// Taster am Pico (GP1/3/5/7) auswerten: Anzeige weiter / Automatik / 100 % (lang: aus) / Neustart. Display-Pico auf dem
    /// Waveshare-Board (8.2): KEY0 = Anzeige weiter, KEY1 = sofort neu zeichnen (3 s halten: Neustart).
    /// </summary>
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

    /// <summary>0.9.7, Seite „Kurs“: "btc", "bch" oder "both"; 0.9.16 "mined" = die Coins, die die Miner gerade schürfen (bis 4). Additiv.</summary>
    public string PriceCoins { get; set; } = "btc";

    /// <summary>0.9.7, Tagesbilanz: "none" (Minerliste wie bisher), "hashrate", "power", "efficiency" oder "temp" als 24-h-Graph.</summary>
    public string DailyChart { get; set; } = "none";

    /// <summary>0.9.11: Graph der Seite „Verlauf“ (24 h): "hashrate", "temp", "power" oder "efficiency". Additiv.</summary>
    public string HistoryChart { get; set; } = "hashrate";

    /// <summary>
    /// 0.9.11: Wie alle Sonderanzeigen enden – "each" (je Anzeige wie eingestellt), "hours" (alle nach
    /// <see cref="BlockFoundHoldHours"/> Stunden oder mit Taste 1) oder "button" (alle erst mit Taste 1). Additiv.
    /// </summary>
    public string SpecialUntil { get; set; } = "each";

    /// <summary>
    /// 0.9.16: Wie jede Sonderanzeige endet – je Art eine eigene Regel ("block", "alarm", "bestdiff"). Additiv; fehlt sie,
    /// gilt die bisherige Einstellung (<see cref="SpecialUntil"/>, <see cref="BlockFoundUntil"/>, <see cref="BlockFoundHoldHours"/>).
    /// </summary>
    public Dictionary<string, SpecialRule>? SpecialRules { get; set; }

    public static readonly string[] SpecialKinds = ["block", "alarm", "bestdiff"];

    /// <summary>Wirksame Regel einer Sonderanzeige (eigene oder aus den bisherigen Einstellungen abgeleitet).</summary>
    public SpecialRule RuleFor(string kind)
    {
        if (SpecialRules is not null && SpecialRules.TryGetValue(kind, out var r) && r is not null) return r;
        var hours = Math.Max(1, BlockFoundHoldHours);
        return kind switch
        {
            "block" => new SpecialRule { Until = SpecialUntil is "hours" or "button" ? SpecialUntil : BlockFoundUntil, Hours = hours },
            "alarm" => new SpecialRule { Until = SpecialUntil == "hours" ? "hours" : "button", Hours = hours },
            _ => new SpecialRule { Until = SpecialUntil switch { "hours" => "hours", "button" => "button", _ => "once" }, Hours = hours },
        };
    }

    /// <summary>0.9.11: Seite „Pool &amp; Netzwerk“ zeigt auch den letzten Block im Bitcoin-Cash-Netzwerk. Additiv.</summary>
    public bool NetworkBch { get; set; } = true;

    /// <summary>0.9.7, Monatsbilanz: Balken je Tag – "kwh", "cost", "income" oder "hashrate".</summary>
    public string MonthlyChart { get; set; } = "kwh";

    /// <summary>
    /// 0.9.11, Seite „Neuigkeiten“: welche Arten gezeigt werden ("solo", "firmware", "miner", "network", "bitaxetuner").
    /// Additiv; leer = alle.
    /// </summary>
    public List<string> NewsKinds { get; set; } = ["solo", "firmware", "miner", "network", "bitaxetuner", "halving", "price", "milestone"];

    /// <summary>0.9.20: 1 = „milestone“ wurde einmalig in <see cref="NewsKinds"/> aufgenommen (ältere Einstellungen). Additiv.</summary>
    public int NewsKindsVersion { get; set; }

    /// <summary>0.9.7, Seite „QR-Code“: eigene Adresse; leer = Adresse dieses Servers im Heimnetz.</summary>
    public string QrUrl { get; set; } = "";

    // 0.9.16, Seite „Eigenes Bild“ (das Bild selbst liegt im Datenordner unter display-images/)
    /// <summary>"fit" = ganzes Bild mit weißem Rand, "fill" = Fläche füllen, Ränder abschneiden.</summary>
    public string ImageFit { get; set; } = "fit";
    /// <summary>Rastern (Fotos); aus = je Pixel die nächste Farbe (Logos, Schrift).</summary>
    public bool ImageDither { get; set; } = true;
    /// <summary>Rote Bildteile rot zeigen; aus = nur Schwarz/Weiß.</summary>
    public bool ImageRed { get; set; } = true;
    /// <summary>Bildunterschrift (leer = Bild über die ganze Fläche).</summary>
    public string ImageCaption { get; set; } = "";

    /// <summary>0.9.16 Monatsvergleich: 6 oder 12 Monate.</summary>
    public int MonthCompareMonths { get; set; } = 6;

    /// <summary>
    /// 0.9.16 Seitenfolge nach Tageszeit: In einem Zeitfenster nur diese Seiten (und auf Wunsch ein eigenes Intervall),
    /// z. B. morgens Tagesbilanz, abends Strompreis/Ertrag. Leer = immer alle eingeschalteten Seiten. Additiv.
    /// </summary>
    public List<DisplayPageWindow>? PageWindows { get; set; }
}

/// <summary>0.9.16: Zeitfenster der Seitenfolge (Stunde von–bis, über Mitternacht möglich).</summary>
public sealed class DisplayPageWindow
{
    public int From { get; set; } = 6;
    public int To { get; set; } = 10;
    /// <summary>Szenen-Namen (z. B. "Daily", "Power"); nur Seiten, die auch eingeschaltet sind.</summary>
    public List<string> Pages { get; set; } = [];
    /// <summary>Eigenes Intervall in Minuten (0 = wie sonst).</summary>
    public int IntervalMinutes { get; set; }
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
    /// <summary>0.9.11: Neuigkeiten aus der Solo-Mining-Welt (Firmware, neue Miner, Solo-Blockfunde, Netzwerk, BitaxeTuner).</summary>
    public bool News { get; set; }
    /// <summary>0.9.16: eigenes, hochgeladenes Bild.</summary>
    public bool Image { get; set; }
    /// <summary>0.9.16: Ertrag gegen Strom (24 h, 7 Tage, 30 Tage, Monat).</summary>
    public bool Profit { get; set; }
    /// <summary>0.9.16: Solo-Chance je Coin.</summary>
    public bool Solo { get; set; }
    /// <summary>0.9.16: Systemzustand (Speicher, Sicherung, Pi, Zertifikat …).</summary>
    public bool System { get; set; }
    /// <summary>0.9.16: Bitcoin-Netz (Gebühren, Difficulty-Anpassung, Halving).</summary>
    public bool Bitcoin { get; set; }
    /// <summary>0.9.16: Pool (Coin je Miner, Guthaben, letzte Auszahlung, bester Pool).</summary>
    public bool PoolAccount { get; set; }
    /// <summary>0.9.16: Monatsvergleich (Strom und Ertrag der letzten Monate).</summary>
    public bool MonthCompare { get; set; }
    /// <summary>0.9.25: Jahresrückblick der letzten 12 Monate (ohne Minernamen). Standardmäßig aus.</summary>
    public bool YearReview { get; set; }
}

/// <summary>
/// 0.9.11: weitere E-Paper-Anzeige mit eigenem Display-Pico (USB mit festem Port oder WLAN) – eigene Seiten, eigenes
/// Intervall, optional nur eine Miner-Gruppe. Die erste Anzeige bleibt <see cref="AppConfig.Display"/>. Additiv.
/// </summary>
public sealed class ExtraDisplayConfig
{
    /// <summary>Kurzkennung (a–f, 0–9), auch für den WLAN-Schlüssel und den Zustand der Sonderanzeigen.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "";

    /// <summary>Nur diese Miner-Gruppe (Übersicht und Summen); leer = alle Miner.</summary>
    public string Group { get; set; } = "";

    /// <summary>Seiten, Intervall, Verbindung usw. wie bei der ersten Anzeige; Device ist immer „own“.</summary>
    public DisplaySettings Settings { get; set; } = new() { Enabled = true, Device = "own", Connection = "wlan" };

    public static bool ValidId(string? id) => id is { Length: > 0 and <= 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// 0.9.16: Ende einer Sonderanzeige – "button" (bis Taste 1 bzw. „Anzeige weiter“ quittiert), "hours" (nach <see cref="Hours"/>
/// Stunden oder vorher mit Taste 1) oder "once" (einmal zeigen, danach weiter mit den Seiten).
/// </summary>
public sealed class SpecialRule
{
    public string Until { get; set; } = "button";
    public int Hours { get; set; } = 24;

    public static readonly string[] Modes = ["button", "hours", "once"];
}
