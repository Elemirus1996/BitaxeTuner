using System.Security.AccessControl;
using System.Security.Principal;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Update;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Tests;

/// <summary>Funde S1 und S3 aus dem Audit vom 02.10.2026 (Windows-Dienst).</summary>
[Collection("SecretScope")]
public class WindowsSecurityTests
{
    [Fact]
    public void Restricted_folder_drops_inherited_access_of_other_users_also_for_existing_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var data = Path.Combine(dir.Path, "data");
        Directory.CreateDirectory(data);
        // wie %ProgramData%: „Benutzer“ dürfen lesen, vererbt an alles darunter
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var open = new DirectoryInfo(data).GetAccessControl();
        open.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(data).SetAccessControl(open);
        var secret = Path.Combine(data, "server-auth.json");
        File.WriteAllText(secret, "{}");
        Assert.False(WindowsAcl.IsRestricted(secret));

        WindowsAcl.Restrict(data);

        Assert.True(WindowsAcl.IsRestricted(data));
        Assert.True(WindowsAcl.IsRestricted(secret));                         // vorhandene Datei: Vererbung neu
        File.WriteAllText(Path.Combine(data, "neu.json"), "{}");
        Assert.True(WindowsAcl.IsRestricted(Path.Combine(data, "neu.json")));
        Assert.Equal("{}", File.ReadAllText(secret));                          // das laufende Konto sperrt sich nicht aus
    }

    [Fact]
    public void Update_file_is_checked_again_and_cannot_be_replaced_while_open()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "setup.exe");
        File.WriteAllText(file, "geprüft");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("geprüft"u8.ToArray())).ToLowerInvariant();
        using (UpdateService.OpenVerified(file, sha))
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(file, "ausgetauscht"));
            Assert.Equal("geprüft", File.ReadAllText(file));                    // lesen (ausführen) geht weiter
        }
        File.WriteAllText(file, "ausgetauscht");
        Assert.Throws<InvalidOperationException>(() => UpdateService.OpenVerified(file, sha));
        Assert.Throws<InvalidOperationException>(() => UpdateService.OpenVerified(file, null));
    }

    [Fact]
    public void Service_scope_re_encrypts_machine_wide_secrets_without_losing_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var store = new SecretStore(dir.Path);
        try
        {
            SecretStore.UseServiceAccountScope = false;
            store.Set(SecretStore.MqttPassword, "geheim-123");
            Assert.Contains("\"dpapi:", File.ReadAllText(store.FilePath));

            SecretStore.UseServiceAccountScope = true;
            Assert.Equal("geheim-123", store.Get(SecretStore.MqttPassword));       // altes Format lesbar …
            Assert.Contains("\"dpapi-user:", File.ReadAllText(store.FilePath));   // … und umgeschlüsselt
            Assert.Equal("geheim-123", store.Get(SecretStore.MqttPassword));
        }
        finally { SecretStore.UseServiceAccountScope = false; }
    }
}
