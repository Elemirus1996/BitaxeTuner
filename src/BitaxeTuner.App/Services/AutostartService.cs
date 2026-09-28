using System.IO;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.Services;

/// <summary>
/// Verknüpfung im Autostart-Ordner des Benutzers (ersetzt Install-Autostart.ps1 aus BitaxeMonitor).
/// Eine alte BitaxeMonitor-Verknüpfung wird nur nach Rückfrage entfernt – sonst würden beide Programme
/// starten und die Miner doppelt abfragen.
/// </summary>
public static class AutostartService
{
    private static string StartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public static string LinkPath => Path.Combine(StartupFolder, "BitaxeTuner.lnk");
    public static string OldMonitorLinkPath => Path.Combine(StartupFolder, "BitaxeMonitor.lnk");

    public static bool IsEnabled => File.Exists(LinkPath);
    public static bool OldMonitorLinkExists => File.Exists(OldMonitorLinkPath);

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("Programmpfad unbekannt"));
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L.T("WScript.Shell nicht verfügbar"));
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "BitaxeTuner";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Disable()
    {
        if (File.Exists(LinkPath)) File.Delete(LinkPath);
    }

    public static void RemoveOldMonitorLink()
    {
        if (File.Exists(OldMonitorLinkPath)) File.Delete(OldMonitorLinkPath);
    }
}
