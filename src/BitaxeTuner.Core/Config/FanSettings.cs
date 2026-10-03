namespace BitaxeTuner.Core.Config;

/// <summary>
/// Zusatzlüfter über einen Raspberry Pi Pico am Server (6 Kanäle): je Miner ein VR-Lüfter und eine gemeinsame
/// Gehäuselüfter-Gruppe. Gespeichert in config.json („Fans“).
/// </summary>
public sealed class FanSettings
{
    public const int ChannelCount = 6;

    public bool Enabled { get; set; }

    /// <summary>"auto" (Pico per USB-Kennung suchen) oder fester Port, z. B. "/dev/ttyACM0" bzw. "COM5".</summary>
    public string Port { get; set; } = "auto";

    /// <summary>0.9.7: "usb" (Standard) oder "wlan" (Pico 2 W, vorher per USB „für WLAN einrichten“). Additiv.</summary>
    public string Connection { get; set; } = "usb";

    /// <summary>WLAN: Gerätename (z. B. bitaxetuner-fans.local) oder IP-Adresse, optional mit :Port.</summary>
    public string NetworkHost { get; set; } = "";

    /// <summary>WLAN: zuletzt erreichte IP-Adresse – Rückfall, falls der Gerätename im Heimnetz nicht auflöst.</summary>
    public string NetworkIp { get; set; } = "";

    public List<FanChannelSettings> Channels { get; set; } =
        Enumerable.Range(1, ChannelCount).Select(i => new FanChannelSettings { Channel = i }).ToList();

    /// <summary>Gemeinsame Einstellung für alle Kanäle mit Rolle „Gehäuse“.</summary>
    public CaseFanSettings Case { get; set; } = new();

    /// <summary>Vorgabe-Warnschwelle für neu erkannte Temperaturfühler (°C).</summary>
    public double CaseTempWarn { get; set; } = 45;

    /// <summary>Temperaturfühler (DS18B20) am Pico, erkannt an ihrer 1-Wire-Kennung. Neue werden automatisch ergänzt.</summary>
    public List<TempSensorSettings> Sensors { get; set; } = [];

    /// <summary>Kanal 1–6, fehlende Einträge (ältere config.json) werden ergänzt.</summary>
    public FanChannelSettings Channel(int channel)
    {
        var c = Channels.FirstOrDefault(x => x.Channel == channel);
        if (c is null)
        {
            c = new FanChannelSettings { Channel = channel };
            Channels.Add(c);
            Channels.Sort((a, b) => a.Channel.CompareTo(b.Channel));
        }
        return c;
    }
}

public sealed class FanChannelSettings
{
    public int Channel { get; set; }
    public string Name { get; set; } = "";

    /// <summary>"none" (nicht belegt), "miner" (VR-Lüfter eines Miners) oder "case" (Gehäusegruppe).</summary>
    public string Role { get; set; } = "none";

    /// <summary>Host des Miners bei Rolle "miner".</summary>
    public string? MinerHost { get; set; }

    /// <summary>"auto" (Kurve nach VR-Temperatur) oder "manual".</summary>
    public string Mode { get; set; } = "auto";
    public int ManualPercent { get; set; } = 60;
    public FanCurve Curve { get; set; } = new();

    /// <summary>Lüfter liefert ein Drehzahlsignal (sonst keine „Lüfter steht“-Meldung).</summary>
    public bool HasTach { get; set; } = true;
}

/// <summary>Ein Temperaturfühler, z. B. „Netzteil“ oder „Miner-Raum“.</summary>
public sealed class TempSensorSettings
{
    /// <summary>1-Wire-Kennung (16 Hex-Zeichen); "#1" usw. bei alter Pico-Firmware.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Ab dieser Temperatur: Push-Meldung und rote Anzeige.</summary>
    public double WarnTemp { get; set; } = 45;

    /// <summary>Zählt für die Gehäuselüfter (Messgröße „Temperaturfühler“; es gilt der höchste Wert).</summary>
    public bool CaseFans { get; set; } = true;

    public bool ShowOnDisplay { get; set; } = true;
}

/// <summary>Unter StartTemp: MinPercent. Von StartTemp (StartPercent) linear bis FullTemp (100 %).</summary>
public sealed class FanCurve
{
    public double StartTemp { get; set; } = 50;
    public int StartPercent { get; set; } = 30;
    public double FullTemp { get; set; } = 70;
    public int MinPercent { get; set; } = 25;
    /// <summary>Beim Abkühlen erst so viele °C später wieder herunterregeln (kein Pendeln).</summary>
    public double Hysteresis { get; set; } = 2;
}

public sealed class CaseFanSettings
{
    public string Mode { get; set; } = "auto";
    public int ManualPercent { get; set; } = 50;

    /// <summary>"vr", "asic" (höchster Wert der zugeordneten Miner) oder "case" (Temperaturfühler mit <see cref="TempSensorSettings.CaseFans"/>).</summary>
    public string Sensor { get; set; } = "vr";

    /// <summary>Hosts der berücksichtigten Miner; leer = alle.</summary>
    public List<string> Miners { get; set; } = [];

    public FanCurve Curve { get; set; } = new() { StartTemp = 45, StartPercent = 30, FullTemp = 70, MinPercent = 25 };

    /// <summary>Ein berücksichtigter Miner ist offline oder seine Daten sind veraltet → mindestens so viel.</summary>
    public int UnknownPercent { get; set; } = 100;

    public bool NightEnabled { get; set; }
    public int NightFromHour { get; set; } = 22;
    public int NightToHour { get; set; } = 7;
    /// <summary>Höchstwert nachts (gilt nicht, sobald die Volllast-Temperatur erreicht ist).</summary>
    public int NightMaxPercent { get; set; } = 40;
}
