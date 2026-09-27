namespace BitaxeTuner.Core.Config;

/// <summary>
/// Tägliche Sicherung (config.json, history.db, tax\, tuning\, snapshots\ als geprüftes Datenarchiv).
/// Immer im Datenordner (auto-backups\), dazu wahlweise in einen Ordner/USB-Stick und/oder auf ein Netzlaufwerk.
/// Gespeichert in config.json („Backup“); das NAS-Passwort liegt getrennt in secrets.json.
/// </summary>
public sealed class BackupSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Ab dieser Stunde einmal täglich.</summary>
    public int Hour { get; set; } = 3;

    /// <summary>So viele Sicherungen bleiben im Datenordner (SD-Karte schonen).</summary>
    public int LocalKeep { get; set; } = 7;

    /// <summary>So viele Sicherungen bleiben auf Ordner/USB und NAS.</summary>
    public int Keep { get; set; } = 14;

    public BackupFolderTarget Folder { get; set; } = new();
    public BackupSmbTarget Smb { get; set; } = new();

    /// <summary>Datum der letzten geplanten Sicherung (yyyy-MM-dd).</summary>
    public string? LastRun { get; set; }
}

public sealed class BackupFolderTarget
{
    public bool Enabled { get; set; }
    /// <summary>Zielordner, z. B. „D:\Sicherungen“ oder am Pi der USB-Stick „/media/bitaxetuner-usb/BitaxeTuner-Sicherungen“.</summary>
    public string Path { get; set; } = "";
}

public sealed class BackupSmbTarget
{
    public bool Enabled { get; set; }
    /// <summary>Rechnername oder IP des NAS/PCs.</summary>
    public string Server { get; set; } = "";
    /// <summary>Freigabename, z. B. „backup“.</summary>
    public string Share { get; set; } = "";
    /// <summary>Unterordner in der Freigabe (leer = oberste Ebene).</summary>
    public string Folder { get; set; } = "BitaxeTuner";
    public string User { get; set; } = "";
    public string Domain { get; set; } = "";
}
