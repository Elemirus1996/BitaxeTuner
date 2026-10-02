using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BitaxeTuner.Server.Security;

/// <summary>
/// Windows-Dienst: Datenordner (Zugangsdaten, Einrichtungs-Code, Tokens) und Update-Downloads nur für SYSTEM und
/// Administratoren. %ProgramData% vererbt sonst Leserechte an alle Benutzer des Rechners (Audit S3).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Vererbung aus, Vollzugriff nur für SYSTEM, Administratoren und das laufende Konto (damit sich ein von Hand
    /// gestarteter Server nicht selbst aussperrt). Die neuen Rechte werden an vorhandene Dateien weitergegeben.
    /// </summary>
    public static void Restrict(string directory)
    {
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var sids = new List<SecurityIdentifier> { System, Administrators };
        if (WindowsIdentity.GetCurrent().User is { } me && !sids.Contains(me)) sids.Add(me);
        foreach (var sid in sids)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    /// <summary>Hat außer SYSTEM, Administratoren und dem laufenden Konto noch jemand Zugriff?</summary>
    public static bool IsRestricted(string path)
    {
        var allowed = new List<SecurityIdentifier> { System, Administrators };
        if (WindowsIdentity.GetCurrent().User is { } me) allowed.Add(me);
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .All(r => allowed.Contains((SecurityIdentifier)r.IdentityReference));
    }
}
