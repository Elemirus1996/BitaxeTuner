using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Config;
using SMBLibrary;
using SMBLibrary.Client;

namespace BitaxeTuner.Core.Backup;

/// <summary>Ein Ort für Sicherungen: Datei ablegen, vorhandene auflisten, alte löschen.</summary>
public interface IBackupTarget
{
    string Name { get; }
    /// <summary>Datei hochladen und prüfen (Größe bzw. SHA-256).</summary>
    Task UploadAsync(string localFile, string fileName, CancellationToken ct);
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string fileName, CancellationToken ct);
}

public static partial class BackupNames
{
    [GeneratedRegex(@"^bitaxetuner-backup-\d{8}-\d{6}\.zip$")]
    public static partial Regex Pattern();

    public static string For(DateTime time) => $"bitaxetuner-backup-{time:yyyyMMdd-HHmmss}.zip";

    public static bool IsBackup(string name) => Pattern().IsMatch(name);

    /// <summary>Älteste Sicherungen über <paramref name="keep"/> hinaus (nur eigene Dateien).</summary>
    public static IEnumerable<string> Surplus(IEnumerable<string> names, int keep) =>
        names.Where(IsBackup).OrderByDescending(n => n, StringComparer.Ordinal).Skip(Math.Max(1, keep));
}

/// <summary>Lokaler Ordner, USB-Stick oder eingebundenes Netzlaufwerk.</summary>
public sealed class FolderBackupTarget(string name, string path) : IBackupTarget
{
    public string Name { get; } = name;
    public string Path { get; } = path;

    public async Task UploadAsync(string localFile, string fileName, CancellationToken ct)
    {
        Directory.CreateDirectory(Path);
        var target = System.IO.Path.Combine(Path, fileName);
        var tmp = target + ".tmp";
        await using (var src = File.OpenRead(localFile))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            await src.CopyToAsync(dst, ct);
        if (!(await HashAsync(localFile, ct)).SequenceEqual(await HashAsync(tmp, ct)))
        {
            File.Delete(tmp);
            throw new IOException("Kopie weicht vom Original ab (SHA-256).");
        }
        File.Move(tmp, target, overwrite: true);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Directory.Exists(Path)
            ? Directory.EnumerateFiles(Path).Select(f => System.IO.Path.GetFileName(f)).ToList()
            : []);

    public Task DeleteAsync(string fileName, CancellationToken ct)
    {
        if (!BackupNames.IsBackup(fileName)) throw new InvalidOperationException("Nur eigene Sicherungen werden gelöscht.");
        File.Delete(System.IO.Path.Combine(Path, fileName));
        return Task.CompletedTask;
    }

    private static async Task<byte[]> HashAsync(string file, CancellationToken ct)
    {
        await using var s = File.OpenRead(file);
        return await SHA256.HashDataAsync(s, ct);
    }
}

/// <summary>Windows-/NAS-Freigabe (SMB 2/3) ohne Einbinden ins System – funktioniert auch im abgesicherten Dienst.</summary>
public sealed class SmbBackupTarget(BackupSmbTarget settings, string password) : IBackupTarget
{
    public string Name => $@"\\{settings.Server}\{settings.Share}";

    private string Dir => settings.Folder.Trim().Trim('\\', '/').Replace('/', '\\');
    private string PathOf(string file) => Dir.Length > 0 ? Dir + "\\" + file : file;

    private sealed class Session(SMB2Client client, ISMBFileStore store) : IDisposable
    {
        public ISMBFileStore Store { get; } = store;
        public uint MaxWrite => client.MaxWriteSize;

        public void Dispose()
        {
            try { Store.Disconnect(); } catch { /* egal */ }
            try { client.Logoff(); } catch { /* egal */ }
            client.Disconnect();
        }
    }

    private Session Open()
    {
        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Share))
            throw new InvalidOperationException("Netzlaufwerk: Server und Freigabe angeben.");
        var client = new SMB2Client();
        var connected = IPAddress.TryParse(settings.Server.Trim(), out var ip)
            ? client.Connect(ip, SMBTransportType.DirectTCPTransport)
            : client.Connect(settings.Server.Trim(), SMBTransportType.DirectTCPTransport);
        if (!connected) throw new IOException($"Netzlaufwerk {settings.Server} nicht erreichbar (Port 445).");
        var login = client.Login(settings.Domain.Trim(), settings.User.Trim(), password);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"Anmeldung am Netzlaufwerk fehlgeschlagen ({login}).");
        }
        var store = client.TreeConnect(settings.Share.Trim(), out var status);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            client.Logoff();
            client.Disconnect();
            throw new IOException($"Freigabe „{settings.Share}“ nicht verfügbar ({status}).");
        }
        return new Session(client, store);
    }

    private static void Check(NTStatus status, string what)
    {
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"Netzlaufwerk: {what} fehlgeschlagen ({status}).");
    }

    private static void EnsureDirectory(ISMBFileStore store, string dir)
    {
        if (dir.Length == 0) return;
        var path = "";
        foreach (var part in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "\\" + part;
            var s = store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(s, $"Ordner „{path}“ anlegen");
            store.CloseFile(handle);
        }
    }

    public Task UploadAsync(string localFile, string fileName, CancellationToken ct) => Task.Run(() =>
    {
        using var session = Open();
        var store = session.Store;
        EnsureDirectory(store, Dir);
        var tmp = PathOf(fileName + ".tmp");
        Check(store.CreateFile(out var handle, out _, tmp, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null), "Datei anlegen");
        long offset = 0;
        try
        {
            using var src = File.OpenRead(localFile);
            var buffer = new byte[(int)Math.Min(session.MaxWrite, 1024 * 1024)];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                Check(store.WriteFile(out var written, handle, offset, chunk), "Schreiben");
                if (written != read) throw new IOException("Netzlaufwerk: unvollständig geschrieben.");
                offset += written;
            }
        }
        finally
        {
            store.CloseFile(handle);
        }
        if (offset != new FileInfo(localFile).Length) throw new IOException("Netzlaufwerk: Größe stimmt nicht.");
        Rename(store, tmp, PathOf(fileName));
    }, ct);

    private static void Rename(ISMBFileStore store, string from, string to)
    {
        Check(store.CreateFile(out var handle, out _, from, AccessMask.DELETE | AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null), "Öffnen zum Umbenennen");
        try
        {
            Check(store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = to, ReplaceIfExists = true }), "Umbenennen");
        }
        finally
        {
            store.CloseFile(handle);
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var session = Open();
        var store = session.Store;
        var s = store.CreateFile(out var handle, out _, Dir, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (s == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND || s == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND) return [];
        Check(s, "Ordner öffnen");
        try
        {
            store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
            return entries.OfType<FileDirectoryInformation>().Select(e => e.FileName).Where(n => n is not "." and not "..").ToList();
        }
        finally
        {
            store.CloseFile(handle);
        }
    }, ct);

    public Task DeleteAsync(string fileName, CancellationToken ct) => Task.Run(() =>
    {
        if (!BackupNames.IsBackup(fileName)) throw new InvalidOperationException("Nur eigene Sicherungen werden gelöscht.");
        using var session = Open();
        var store = session.Store;
        Check(store.CreateFile(out var handle, out _, PathOf(fileName), AccessMask.DELETE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null), "Löschen");
        store.CloseFile(handle);
    }, ct);
}
