using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

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

/// <summary>
/// Eigener Nur-Lesen-Zugang mit eigener PIN (gespeichert als PBKDF2-Hash), z. B. für Mitbewohner oder Kunden.
/// Gruppen leer = alle Miner, sonst nur Miner in mindestens einer der Gruppen. Einzeln widerrufbar.
/// </summary>
public sealed class ViewerAccess
{
    public const int MinPinLength = 6;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PinHash { get; set; } = "";
    public List<string> Groups { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>Ergebnis der Anmeldung; <see cref="Access"/> ist gesetzt, wenn mit der PIN eines eigenen Ansicht-Zugangs angemeldet.</summary>
public sealed record LoginResult(Role Role, ViewerAccess? Access = null);

/// <summary>Zugangsdaten des Servers (server-auth.json im Datenordner).</summary>
public sealed class AuthData
{
    public string AdminHash { get; set; } = "";
    public List<ApiToken> Tokens { get; set; } = [];
    public List<ViewerAccess> Viewers { get; set; } = [];
}

/// <summary>
/// Admin-Passwort (PBKDF2-SHA256), Einrichtungs-Code beim ersten Start, API-Token für die Desktop-App.
/// Die Rolle „Nur ansehen“ nutzt die PIN der Handy-Ansicht aus config.json.
/// </summary>
public sealed class AuthStore
{
    public const int MinPasswordLength = 10;
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

    /// <summary>Erste Einrichtung: Code prüfen, Admin-Passwort setzen. Ergebnis: Fehlermeldung oder null.</summary>
    public LocText? Setup(string code, string password)
    {
        lock (_lock)
        {
            if (IsSetUp) return new LocText(L.N("Der Server ist bereits eingerichtet."));
            if (SetupCode is null || !FixedEquals(code.Trim(), SetupCode)) return new LocText(L.N("Einrichtungs-Code falsch."));
            if (ValidatePassword(password) is { } error) return error;
            _data.AdminHash = HashPassword(password);
            Save();
            SetupCode = null;
            try { File.Delete(_codeFile); } catch { /* egal */ }
            return null;
        }
    }

    public LocText? ChangePassword(string current, string password)
    {
        lock (_lock)
        {
            if (!VerifyPassword(current, _data.AdminHash)) return new LocText(L.N("Aktuelles Passwort falsch."));
            if (ValidatePassword(password) is { } error) return error;
            _data.AdminHash = HashPassword(password);
            Save();
            return null;
        }
    }

    /// <summary>Anmeldung: Admin-Passwort oder PIN der Ansicht.</summary>
    public Role Login(string secret) => LoginAs(secret).Role;

    /// <summary>
    /// Anmeldung: Admin-Passwort, dann die PINs der eigenen Ansicht-Zugänge (vor der allgemeinen PIN – bei gleicher PIN
    /// gilt also die engere Freigabe), dann die allgemeine PIN der Ansicht (alle Miner).
    /// </summary>
    public LoginResult LoginAs(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return new LoginResult(Role.None);
        string adminHash;
        List<ViewerAccess> viewers;
        lock (_lock)
        {
            adminHash = _data.AdminHash;
            viewers = _data.Viewers.ToList();
        }
        if (adminHash.Length > 0 && VerifyPassword(secret, adminHash)) return new LoginResult(Role.Admin);
        var pin = secret.Trim();
        foreach (var v in viewers.Where(v => v.PinHash.Length > 0))
        {
            if (!VerifyPassword(pin, v.PinHash)) continue;
            lock (_lock)
            {
                if (_data.Viewers.FirstOrDefault(x => x.Id == v.Id) is not { } current) break; // gerade widerrufen
                current.LastUsedUtc = DateTime.UtcNow;
                Save();
                return new LoginResult(Role.Viewer, Clone(current));
            }
        }
        var pinHash = _config().WebView.PinHash;
        if (pinHash.Length > 0 && FixedEquals(WebViewSettings.HashPin(secret), pinHash)) return new LoginResult(Role.Viewer);
        return new LoginResult(Role.None);
    }

    public IReadOnlyList<ViewerAccess> Viewers
    {
        get { lock (_lock) return _data.Viewers.Select(Clone).ToList(); }
    }

    /// <summary>
    /// Neuer Ansicht-Zugang. Die PIN braucht mindestens 6 Ziffern und darf weder das Admin-Passwort noch die allgemeine
    /// PIN noch die PIN eines anderen Zugangs sein (sonst wäre unklar, wer was sehen darf).
    /// </summary>
    public ViewerAccess CreateViewer(string name, string pin, IEnumerable<string>? groups)
    {
        pin = (pin ?? "").Trim();
        if (pin.Length < ViewerAccess.MinPinLength || !pin.All(char.IsAsciiDigit))
            throw new LocalizedException("Die PIN braucht mindestens {0} Ziffern.", ViewerAccess.MinPinLength);
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new LocalizedException("Bitte einen Namen angeben.");
        if (name.Length > 60) name = name[..60];
        var pinHash = _config().WebView.PinHash;
        if (pinHash.Length > 0 && FixedEquals(WebViewSettings.HashPin(pin), pinHash))
            throw new LocalizedException("Diese PIN ist schon vergeben.");
        lock (_lock)
        {
            if ((_data.AdminHash.Length > 0 && VerifyPassword(pin, _data.AdminHash)) || _data.Viewers.Any(v => VerifyPassword(pin, v.PinHash)))
                throw new LocalizedException("Diese PIN ist schon vergeben.");
            if (_data.Viewers.Count >= 50) throw new LocalizedException("Höchstens {0} Ansicht-Zugänge.", 50);
            var entry = new ViewerAccess
            {
                Id = Base64Url(RandomNumberGenerator.GetBytes(6)),
                Name = name,
                PinHash = HashPassword(pin),
                Groups = MinerGroups.Normalize(groups),
                CreatedUtc = DateTime.UtcNow,
            };
            _data.Viewers.Add(entry);
            Save();
            return Clone(entry);
        }
    }

    /// <summary>Zugang widerrufen; Ergebnis: der entfernte Eintrag oder null.</summary>
    public ViewerAccess? RevokeViewer(string id)
    {
        lock (_lock)
        {
            var entry = _data.Viewers.FirstOrDefault(v => v.Id == id);
            if (entry is null) return null;
            _data.Viewers.Remove(entry);
            Save();
            return entry;
        }
    }

    private static ViewerAccess Clone(ViewerAccess v) => new()
    {
        Id = v.Id, Name = v.Name, PinHash = v.PinHash, Groups = v.Groups.ToList(), CreatedUtc = v.CreatedUtc, LastUsedUtc = v.LastUsedUtc,
    };

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

    public static LocText? ValidatePassword(string password) =>
        password.Length < MinPasswordLength ? new LocText("Das Passwort braucht mindestens {0} Zeichen.", MinPasswordLength) : null;

    // Gleiche Hashes wie das Einrichtungspaket der Desktop-App (Core/Transfer/Provisioning.cs)
    public static string HashPassword(string password) => Core.Transfer.Secrets.HashPassword(password);

    public static bool VerifyPassword(string password, string stored) => Core.Transfer.Secrets.VerifyPassword(password, stored);

    private static string NewSetupCode() =>
        string.Concat(Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetInt32(1000, 10000).ToString())).Insert(8, "-").Insert(4, "-");

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Sha256(string s) => Core.Transfer.Secrets.TokenHash(s);

    internal static string Base64Url(byte[] bytes) => Core.Transfer.Secrets.Base64Url(bytes);

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
