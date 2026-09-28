using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

/// <summary>
/// Zugang zum Pi: Benutzer, WLAN, SSH – was der Raspberry Pi Imager bei eigenen Images nicht anbietet.
/// <paramref name="SshKeys"/>: öffentliche SSH-Schlüssel (z. B. des PCs) für die Anmeldung ohne Passwort.
/// </summary>
public sealed record PiOsOptions(
    string User, string Password, string? WifiSsid = null, string? WifiPassword = null,
    string Hostname = "bitaxetuner", string Country = "DE", string Timezone = "Europe/Berlin", string Keyboard = "de", bool Ssh = true,
    IReadOnlyList<string>? SshKeys = null);

/// <summary>
/// Schreibt die cloud-init-Dateien von Raspberry Pi OS (ab Trixie) auf die Boot-Partition: <c>user-data</c>
/// (Hostname, Benutzer, Zeitzone, Tastatur) und <c>network-config</c> (WLAN/LAN), dazu die Datei <c>ssh</c>.
/// Passwörter stehen dort nur als Hash: Benutzer als SHA-512-crypt, WLAN als vorberechneter WPA-Schlüssel (PSK).
/// </summary>
public static partial class PiOsSetup
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z_]+(/[A-Za-z0-9_+\-]+){0,2}$")]
    private static partial Regex TimezonePattern();

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)) [A-Za-z0-9+/=]+( [^\r\n""]*)?$")]
    private static partial Regex SshKeyPattern();

    /// <summary>Ist das eine Boot-Partition mit cloud-init (Raspberry Pi OS ab Trixie)?</summary>
    public static bool Supports(string bootRoot) =>
        File.Exists(Path.Combine(bootRoot, "cmdline.txt")) && File.Exists(Path.Combine(bootRoot, "meta-data"));

    public static void Validate(PiOsOptions o)
    {
        if (!UserPattern().IsMatch(o.User) || o.User is "root" or "bitaxetuner")
            throw new InvalidOperationException(L.T("Benutzername: Kleinbuchstaben, Ziffern, - oder _ (z. B. „pi“ oder „admin“)."));
        if (o.Password.Length < 8) throw new InvalidOperationException(L.T("Das Pi-Passwort braucht mindestens 8 Zeichen."));
        if (!HostPattern().IsMatch(o.Hostname)) throw new InvalidOperationException(L.T("Hostname: Kleinbuchstaben, Ziffern und -."));
        if (!Regex.IsMatch(o.Country, "^[A-Z]{2}$")) throw new InvalidOperationException(L.T("Land als Kürzel mit 2 Großbuchstaben, z. B. DE."));
        if (!TimezonePattern().IsMatch(o.Timezone)) throw new InvalidOperationException(L.T("Zeitzone im Format Kontinent/Stadt, z. B. Europe/Berlin."));
        if (!Regex.IsMatch(o.Keyboard, "^[a-z]{2,3}$")) throw new InvalidOperationException(L.T("Tastatur als Kürzel mit 2–3 Kleinbuchstaben, z. B. de oder us."));
        foreach (var key in o.SshKeys ?? [])
            if (!SshKeyPattern().IsMatch(key.Trim())) throw new InvalidOperationException(L.T("Ungültiger SSH-Schlüssel."));
        if (o.WifiSsid is { Length: > 0 } ssid)
        {
            if (Encoding.UTF8.GetByteCount(ssid) > 32) throw new InvalidOperationException(L.T("Der WLAN-Name ist zu lang (max. 32 Zeichen)."));
            if (o.WifiPassword is not { Length: >= 8 and <= 63 })
                throw new InvalidOperationException(L.T("Das WLAN-Passwort muss 8 bis 63 Zeichen haben."));
        }
    }

    /// <summary>Dateien schreiben. Die mitgelieferten Vorlagen werden einmalig als *.orig gesichert.</summary>
    public static void Write(string bootRoot, PiOsOptions o)
    {
        Validate(o);
        if (!Supports(bootRoot))
            throw new InvalidOperationException(L.T("Diese Boot-Partition unterstützt keine Voreinstellungen (cloud-init fehlt – altes Raspberry Pi OS?)."));

        var userData = new StringBuilder()
            .Append("#cloud-config\n")
            .Append(L.T("# Erstellt von BitaxeTuner (Raspberry Pi vorbereiten)\n"))
            .Append($"hostname: {Q(o.Hostname)}\n")
            .Append("manage_etc_hosts: true\n")
            .Append($"timezone: {Q(o.Timezone)}\n")
            .Append("keyboard:\n  model: pc105\n")
            .Append($"  layout: {Q(o.Keyboard)}\n")
            .Append("users:\n")
            .Append($"- name: {Q(o.User)}\n")
            .Append("  groups: users,adm,dialout,audio,netdev,video,plugdev,cdrom,games,input,gpio,spi,i2c,render,sudo\n")
            .Append("  shell: /bin/bash\n")
            .Append("  sudo: \"ALL=(ALL) NOPASSWD:ALL\"\n")
            .Append("  lock_passwd: false\n")
            .Append($"  passwd: {Q(Sha512Crypt(o.Password))}\n")
            .Append(o.SshKeys is { Count: > 0 } keys
                ? "  ssh_authorized_keys:\n" + string.Concat(keys.Select(k => $"  - {Q(k.Trim())}\n")) : "")
            .Append($"ssh_pwauth: {(o.Ssh ? "true" : "false")}\n")
            // WLAN-Land setzen (hebt die WLAN-Sperre von Raspberry Pi OS auf)
            .Append("runcmd:\n")
            .Append($"- [ raspi-config, nonint, do_wifi_country, {Q(o.Country)} ]\n")
            .ToString();

        var net = new StringBuilder()
            .Append(L.T("# Erstellt von BitaxeTuner (Raspberry Pi vorbereiten)\n"))
            .Append("network:\n  version: 2\n")
            .Append("  ethernets:\n    eth0:\n      dhcp4: true\n      optional: true\n");
        if (o.WifiSsid is { Length: > 0 } ssid)
            net.Append("  wifis:\n    wlan0:\n      dhcp4: true\n      optional: true\n")
               .Append($"      regulatory-domain: {Q(o.Country)}\n")
               .Append("      access-points:\n")
               .Append($"        {Q(ssid)}:\n")
               .Append($"          password: {Q(WpaPsk(ssid, o.WifiPassword!))}\n");

        Backup(bootRoot, "user-data");
        Backup(bootRoot, "network-config");
        WriteText(Path.Combine(bootRoot, "user-data"), userData);
        WriteText(Path.Combine(bootRoot, "network-config"), net.ToString());
        var sshFile = Path.Combine(bootRoot, "ssh");
        if (o.Ssh) File.WriteAllBytes(sshFile, []);
        else if (File.Exists(sshFile)) File.Delete(sshFile);
    }

    private static void Backup(string root, string name)
    {
        var file = Path.Combine(root, name);
        var orig = file + ".orig";
        if (File.Exists(file) && !File.Exists(orig)) File.Copy(file, orig);
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));

    /// <summary>YAML-String in doppelten Anführungszeichen (JSON-Syntax ist gültiges YAML).</summary>
    private static string Q(string s) => JsonSerializer.Serialize(s, Quote);

    /// <summary>WPA2-Schlüssel wie wpa_passphrase: PBKDF2-HMAC-SHA1(Passwort, SSID, 4096, 32 Byte) als Hex.</summary>
    public static string WpaPsk(string ssid, string passphrase) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), Encoding.UTF8.GetBytes(ssid), 4096, HashAlgorithmName.SHA1, 32)).ToLowerInvariant();

    private const string CryptAlphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>SHA-512-crypt ($6$) nach Ulrich Drepper, wie /etc/shadow; 5000 Runden.</summary>
    public static string Sha512Crypt(string password, string? salt = null)
    {
        salt ??= new string(RandomNumberGenerator.GetItems(CryptAlphabet.AsSpan(), 16));
        if (salt.Length > 16) salt = salt[..16];
        var p = Encoding.UTF8.GetBytes(password);
        var s = Encoding.UTF8.GetBytes(salt);
        const int rounds = 5000;

        var b = SHA512.HashData([.. p, .. s, .. p]);
        var a = new List<byte>(p.Length * 4);
        a.AddRange(p);
        a.AddRange(s);
        for (var n = p.Length; n > 0; n -= 64) a.AddRange(b.Take(Math.Min(64, n)));
        for (var i = p.Length; i > 0; i >>= 1) a.AddRange((i & 1) != 0 ? b : p);
        var c = SHA512.HashData(a.ToArray());

        var dp = SHA512.HashData(Repeat(p, p.Length));
        var pp = Stretch(dp, p.Length);
        var ds = SHA512.HashData(Repeat(s, 16 + c[0]));
        var sp = Stretch(ds, s.Length);

        for (var r = 0; r < rounds; r++)
        {
            var ctx = new List<byte>(256);
            ctx.AddRange((r & 1) != 0 ? pp : c);
            if (r % 3 != 0) ctx.AddRange(sp);
            if (r % 7 != 0) ctx.AddRange(pp);
            ctx.AddRange((r & 1) != 0 ? c : pp);
            c = SHA512.HashData(ctx.ToArray());
        }

        int[] order =
        [
            0, 21, 42, 22, 43, 1, 44, 2, 23, 3, 24, 45, 25, 46, 4, 47, 5, 26, 6, 27, 48, 28, 49, 7, 50, 8, 29, 9, 30, 51, 31, 52, 10,
            53, 11, 32, 12, 33, 54, 34, 55, 13, 56, 14, 35, 15, 36, 57, 37, 58, 16, 59, 17, 38, 18, 39, 60, 40, 61, 19, 62, 20, 41,
        ];
        var sb = new StringBuilder("$6$").Append(salt).Append('$');
        for (var i = 0; i < order.Length; i += 3) Encode(sb, c[order[i]], c[order[i + 1]], c[order[i + 2]], 4);
        Encode(sb, 0, 0, c[63], 2);
        return sb.ToString();
    }

    private static byte[] Repeat(byte[] data, int times)
    {
        var r = new byte[data.Length * times];
        for (var i = 0; i < times; i++) data.CopyTo(r, i * data.Length);
        return r;
    }

    private static byte[] Stretch(byte[] hash, int length)
    {
        var r = new byte[length];
        for (var i = 0; i < length; i++) r[i] = hash[i % hash.Length];
        return r;
    }

    private static void Encode(StringBuilder sb, byte b2, byte b1, byte b0, int n)
    {
        var w = (b2 << 16) | (b1 << 8) | b0;
        for (var i = 0; i < n; i++, w >>= 6) sb.Append(CryptAlphabet[w & 0x3f]);
    }
}
