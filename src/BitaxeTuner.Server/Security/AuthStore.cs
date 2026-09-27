using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.Config;

namespace BitaxeTuner.Server.Security;

public enum Role
{
    None = 0,
    /// <summary>Nur ansehen (wie die bisherige Handy-Ansicht, ohne Wallet-Adressen).</summary>
    Viewer = 1,
    /// <summary>Alles, auch Frequenz/Spannung, Regeln, Einstellungen.</summary>
    Admin = 2,
}

/// <summary>API-Token für die Desktop-App (gespeichert wird nur der SHA-256-Hash).</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>Zugangsdaten des Servers (server-auth.json im Datenordner).</summary>
public sealed class AuthData
{
    public string AdminHash { get; set; } = "";
    public List<ApiToken> Tokens { get; set; } = [];
}

/// <summary>
/// Admin-Passwort (PBKDF2-SHA256), Einrichtungs-Code beim ersten Start, API-Token für die Desktop-App.
/// Die Rolle „Nur ansehen“ nutzt die PIN der Handy-Ansicht aus config.json.
/// </summary>
public sealed class AuthStore
{
    public const int MinPasswordLength = 10;
    private const int Iterations = 210_000;
    private const string TokenPrefix = "btk_";

    private readonly string _file;
    private readonly Func<AppConfig> _config;
    private readonly object _lock = new();
    private AuthData _data;

    public AuthStore(string dataDirectory, Func<AppConfig> config)
    {
        _file = Path.Combine(dataDirectory, "server-auth.json");
        _config = config;
        _data = Load(_file);
        _codeFile = Path.Combine(dataDirectory, "SETUP-CODE.txt");
        if (!IsSetUp)
        {
            SetupCode = NewSetupCode();
            // Auch als Datei im Datenordner (nur für Admin/Dienstkonto lesbar) – z. B. wenn das Dienstprotokoll schwer zu finden ist
            try
            {
                File.WriteAllText(_codeFile, $"BitaxeTuner-Server Einrichtungs-Code: {SetupCode}{Environment.NewLine}Gültig bis zur Einrichtung bzw. zum nächsten Neustart des Dienstes.{Environment.NewLine}");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_codeFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* nur Komfort */ }
        }
        else if (File.Exists(_codeFile)) File.Delete(_codeFile);
    }

    private readonly string _codeFile;

    public bool IsSetUp => _data.AdminHash.Length > 0;

    /// <summary>Einmaliger Code für die Einrichtung; steht im Dienstprotokoll/Konsole. Null nach der Einrichtung.</summary>
    public string? SetupCode { get; private set; }

    public IReadOnlyList<ApiToken> Tokens
    {
        get { lock (_lock) return _data.Tokens.ToList(); }
    }

    /// <summary>Erste Einrichtung: Code prüfen, Admin-Passwort setzen.</summary>
    public string? Setup(string code, string password)
    {
        lock (_lock)
        {
            if (IsSetUp) return "Der Server ist bereits eingerichtet.";
            if (SetupCode is null || !FixedEquals(code.Trim(), SetupCode)) return "Einrichtungs-Code falsch.";
            if (ValidatePassword(password) is { } error) return error;
            _data.AdminHash = HashPassword(password);
            Save();
            SetupCode = null;
            try { File.Delete(_codeFile); } catch { /* egal */ }
            return null;
        }
    }

    public string? ChangePassword(string current, string password)
    {
        lock (_lock)
        {
            if (!VerifyPassword(current, _data.AdminHash)) return "Aktuelles Passwort falsch.";
            if (ValidatePassword(password) is { } error) return error;
            _data.AdminHash = HashPassword(password);
            Save();
            return null;
        }
    }

    /// <summary>Anmeldung: Admin-Passwort oder PIN der Ansicht.</summary>
    public Role Login(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return Role.None;
        string adminHash;
        lock (_lock) adminHash = _data.AdminHash;
        if (adminHash.Length > 0 && VerifyPassword(secret, adminHash)) return Role.Admin;
        var pinHash = _config().WebView.PinHash;
        if (pinHash.Length > 0 && FixedEquals(WebViewSettings.HashPin(secret), pinHash)) return Role.Viewer;
        return Role.None;
    }

    /// <summary>Neues Token – der Klartext wird nur hier einmal zurückgegeben.</summary>
    public (ApiToken Entry, string Secret) CreateToken(string name)
    {
        var secret = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var entry = new ApiToken
        {
            Id = Base64Url(RandomNumberGenerator.GetBytes(6)),
            Name = string.IsNullOrWhiteSpace(name) ? "Desktop" : name.Trim()[..Math.Min(60, name.Trim().Length)],
            Hash = Sha256(secret),
            CreatedUtc = DateTime.UtcNow,
        };
        lock (_lock)
        {
            _data.Tokens.Add(entry);
            Save();
        }
        return (entry, secret);
    }

    public bool RevokeToken(string id)
    {
        lock (_lock)
        {
            var removed = _data.Tokens.RemoveAll(t => t.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>Token prüfen (Admin-Rechte). Letzte Nutzung wird höchstens stündlich gespeichert.</summary>
    public Role VerifyToken(string secret)
    {
        if (!secret.StartsWith(TokenPrefix, StringComparison.Ordinal)) return Role.None;
        var hash = Sha256(secret);
        lock (_lock)
        {
            var token = _data.Tokens.FirstOrDefault(t => FixedEquals(t.Hash, hash));
            if (token is null) return Role.None;
            if (token.LastUsedUtc is null || DateTime.UtcNow - token.LastUsedUtc > TimeSpan.FromHours(1))
            {
                token.LastUsedUtc = DateTime.UtcNow;
                Save();
            }
            return Role.Admin;
        }
    }

    public static string? ValidatePassword(string password) =>
        password.Length < MinPasswordLength ? $"Das Passwort braucht mindestens {MinPasswordLength} Zeichen." : null;

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

    private static string NewSetupCode() =>
        string.Concat(Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetInt32(1000, 10000).ToString())).Insert(8, "-").Insert(4, "-");

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static AuthData Load(string file)
    {
        if (!File.Exists(file)) return new AuthData();
        // Defekte Datei nicht still ersetzen: dann lieber nicht starten (sonst wäre der Server ohne Passwort)
        return JsonSerializer.Deserialize<AuthData>(File.ReadAllText(file))
               ?? throw new InvalidDataException($"{file} ist leer oder beschädigt.");
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _file, overwrite: true);
    }
}
