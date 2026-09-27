using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Transfer;

/// <summary>Passwort- und Token-Hashes – dasselbe Format wie der Server (server-auth.json).</summary>
public static class Secrets
{
    public const string TokenPrefix = "btk_";
    private const int Iterations = 210_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Einrichtungspaket für einen neuen Pi: Die Desktop-App schreibt es nach dem Flashen auf die Boot-Partition
/// (Ordner „bitaxetuner“), der Pi übernimmt es beim ersten Start.
/// <list type="bullet">
/// <item><c>zugang.json</c>: Admin-Passwort (nur als Hash) und Token-Hash für die Desktop-App.</item>
/// <item><c>daten.zip</c> (optional): Datenarchiv der Desktop-App (geprüft wie bei der Datenübertragung).</item>
/// </list>
/// Der Server startet danach pausiert, damit nie Desktop und Pi gleichzeitig die Miner abfragen;
/// „Betriebsart → Nur umschalten“ in der App setzt ihn fort.
/// </summary>
public static class Provisioning
{
    public const string FolderName = "bitaxetuner";
    public const string AccessFile = "zugang.json";
    public const string DataFile = "daten.zip";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Access
    {
        public int Format { get; set; } = 1;
        public string AdminHash { get; set; } = "";
        public List<TokenEntry> Tokens { get; set; } = [];
        public bool StartPaused { get; set; } = true;
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class TokenEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    /// <summary>Paket schreiben. Liefert das Token für die Desktop-App (nur hier im Klartext).</summary>
    public static string Write(string targetFolder, string adminPassword, string? dataDirectory, HistoryStore? openHistory, string appVersion)
    {
        if (adminPassword.Length < 10) throw new InvalidOperationException("Das Admin-Passwort braucht mindestens 10 Zeichen.");
        var folder = Path.Combine(targetFolder, FolderName);
        Directory.CreateDirectory(folder);
        var token = Secrets.NewToken();
        var access = new Access
        {
            AdminHash = Secrets.HashPassword(adminPassword),
            Tokens = [new TokenEntry { Id = Secrets.Base64Url(RandomNumberGenerator.GetBytes(6)), Name = "Desktop " + Environment.MachineName, Hash = Secrets.TokenHash(token) }],
            CreatedBy = "BitaxeTuner " + appVersion,
        };
        if (dataDirectory is not null)
        {
            var tmp = Path.Combine(folder, DataFile + ".tmp");
            using (var fs = File.Create(tmp)) DataArchive.Create(dataDirectory, openHistory, fs, "desktop", appVersion);
            File.Move(tmp, Path.Combine(folder, DataFile), overwrite: true);
        }
        else
        {
            File.Delete(Path.Combine(folder, DataFile));
        }
        File.WriteAllText(Path.Combine(folder, AccessFile), JsonSerializer.Serialize(access, Json));
        File.WriteAllText(Path.Combine(folder, "LIESMICH.txt"),
            "BitaxeTuner-Einrichtungspaket. Der Raspberry Pi übernimmt es beim ersten Start und löscht es danach.\r\n" +
            "zugang.json enthält nur Hashes (kein Klartext-Passwort).\r\n");
        return token;
    }

    /// <summary>
    /// Auf dem Pi beim ersten Start (als root, vor dem Dienst): Paket übernehmen, wenn der Datenordner noch leer ist.
    /// Löscht Passwort-Hash und Datenarchiv anschließend von der Boot-Partition. Liefert Protokollzeilen.
    /// </summary>
    public static List<string> Apply(string packageFolder, string dataDirectory)
    {
        var log = new List<string>();
        var accessFile = Path.Combine(packageFolder, AccessFile);
        if (!File.Exists(accessFile))
        {
            log.Add("Kein Einrichtungspaket gefunden – Einrichtung später im Browser (Einrichtungs-Code).");
            return log;
        }
        Directory.CreateDirectory(dataDirectory);
        var access = JsonSerializer.Deserialize<Access>(File.ReadAllText(accessFile)) ?? throw new InvalidDataException("zugang.json ist leer.");

        var authFile = Path.Combine(dataDirectory, "server-auth.json");
        if (File.Exists(authFile))
            log.Add("Server ist bereits eingerichtet – Zugangsdaten werden nicht überschrieben.");
        else if (access.AdminHash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal))
        {
            var auth = new
            {
                AdminHash = access.AdminHash,
                Tokens = access.Tokens.Select(t => new { t.Id, t.Name, t.Hash, CreatedUtc = DateTime.UtcNow }).ToList(),
            };
            File.WriteAllText(authFile, JsonSerializer.Serialize(auth, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(authFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            log.Add($"Admin-Passwort und {access.Tokens.Count} Token übernommen.");
        }

        var data = Path.Combine(packageFolder, DataFile);
        var fresh = !File.Exists(Path.Combine(dataDirectory, "config.json")) && !File.Exists(Path.Combine(dataDirectory, "history.db"));
        if (File.Exists(data) && fresh)
        {
            var staging = Path.Combine(dataDirectory, "transfer-provision");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ArchiveManifest manifest;
            using (var fs = File.OpenRead(data)) manifest = DataArchive.ExtractAndVerify(fs, staging);
            DataArchive.Apply(staging, dataDirectory);
            Directory.Delete(staging, true);
            // Verbindungsdaten der Desktop-App (Server-URL, Token) gehören nicht auf den Server
            var configFile = Path.Combine(dataDirectory, "config.json");
            if (File.Exists(configFile))
            {
                var config = Config.AppConfig.Load(configFile);
                config.Server = new Config.ServerConnectionSettings();
                config.Backup = new Config.BackupSettings(); // Sicherungsziele des PCs (Pfade, NAS) passen nicht zum Pi
                config.Mqtt.Enabled = false;                 // ohne Passwort (liegt nicht im Paket) erst im Browser einschalten
                config.Save(configFile);
            }
            log.Add($"Daten übernommen: {manifest.Devices} Gerät(e), {manifest.HistoryRows.GetValueOrDefault("samples"):N0} Verlaufswerte.");
        }
        else if (File.Exists(data))
        {
            log.Add("Datenordner ist nicht leer – Datenarchiv wird nicht übernommen.");
        }

        if (access.StartPaused)
        {
            File.WriteAllText(Path.Combine(dataDirectory, "server-state.json"), "{\"paused\":true}");
            log.Add("Server startet pausiert (in der Desktop-App „Betriebsart → Nur umschalten“ setzt ihn fort).");
        }

        // Geheimnisse nicht auf der Boot-Partition liegen lassen
        foreach (var f in new[] { accessFile, data })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* egal */ }
        File.WriteAllText(Path.Combine(packageFolder, "UEBERNOMMEN.txt"), string.Join(Environment.NewLine, log) + Environment.NewLine);
        return log;
    }
}
