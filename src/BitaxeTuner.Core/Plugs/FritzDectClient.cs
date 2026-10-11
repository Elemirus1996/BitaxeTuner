using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Plugs;

/// <summary>
/// 0.9.14: FRITZ!DECT-Steckdosen (200/210 …) über die AHA-HTTP-Schnittstelle der FRITZ!Box, nur lesend:
/// <c>getswitchpower</c> (mW) und <c>getswitchenergy</c> (Wh) je AIN. Anmeldung über <c>login_sid.lua?version=2</c>
/// (PBKDF2-Challenge, ältere FRITZ!OS: MD5); die Sitzung wird gemerkt und bei 403 neu angelegt.
/// Ohne Benutzer gilt der zuletzt an der Box angemeldete Benutzer (wie in der FRITZ!Box-Oberfläche).
/// </summary>
public sealed class FritzDectClient : IPlugClient
{
    public const string NoSid = "0000000000000000";
    private readonly HttpClient _http;
    private readonly string _ain;
    private readonly string? _user;
    private readonly string _password;
    private string? _sid;

    public FritzDectClient(string host, string ain, string? user, string? password, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = ShellyClient.BaseUri(host);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _ain = new string((ain ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (_ain.Length == 0) throw new LocalizedException("FRITZ!DECT: AIN der Steckdose eintragen (steht auf dem Gerät bzw. in der FRITZ!Box unter Smart Home).");
        _user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        _password = password ?? "";
    }

    public async Task<PlugIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        var name = (await CommandAsync("getswitchname", ct)).Trim();
        return new PlugIdentity(0, $"FRITZ!DECT · {name}", true);
    }

    public async Task<PlugReading> ReadAsync(int channel, CancellationToken ct = default)
    {
        var mw = await CommandAsync("getswitchpower", ct);
        var wh = await CommandAsync("getswitchenergy", ct);
        if (!double.TryParse(mw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var power))
            throw new LocalizedException("FRITZ!DECT {0} liefert keine Leistung (nicht verbunden?).", _ain);
        double? energy = double.TryParse(wh.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null;
        return new PlugReading(power / 1000.0, energy, null, null);
    }

    private async Task<string> CommandAsync(string cmd, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _sid ??= await LoginAsync(ct);
            using var resp = await _http.GetAsync($"webservices/homeautoswitch.lua?ain={Uri.EscapeDataString(_ain)}&switchcmd={cmd}&sid={_sid}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) { _sid = null; continue; }   // Sitzung abgelaufen
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new LocalizedException("FRITZ!DECT: Steckdose mit AIN {0} nicht gefunden.", _ain);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (text.Trim() == "inval") throw new LocalizedException("FRITZ!DECT {0} liefert keine Leistung (nicht verbunden?).", _ain);
            return text;
        }
        throw new LocalizedException("Anmeldung an der FRITZ!Box fehlgeschlagen – Benutzer/Passwort prüfen.");
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var info = XDocument.Parse(await _http.GetStringAsync("login_sid.lua?version=2", ct)).Root!;
        var challenge = info.Element("Challenge")?.Value ?? "";
        if (int.TryParse(info.Element("BlockTime")?.Value, out var block) && block > 0)
            throw new LocalizedException("FRITZ!Box sperrt Anmeldungen noch {0} s (zu viele Fehlversuche).", block);
        var user = _user ?? info.Element("Users")?.Elements("User").FirstOrDefault(u => (string?)u.Attribute("last") == "1")?.Value ?? "";
        var response = Response(challenge, _password);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["response"] = response });
        using var resp = await _http.PostAsync("login_sid.lua?version=2", content, ct);
        var sid = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).Root?.Element("SID")?.Value ?? NoSid;
        if (sid == NoSid) throw new LocalizedException("Anmeldung an der FRITZ!Box fehlgeschlagen – Benutzer/Passwort prüfen.");
        return sid;
    }

    /// <summary>Antwort auf die Challenge: „2$iter1$salt1$iter2$salt2“ → PBKDF2 zweistufig, sonst MD5 (UTF-16LE).</summary>
    public static string Response(string challenge, string password)
    {
        if (challenge.StartsWith("2$", StringComparison.Ordinal))
        {
            var p = challenge.Split('$');
            var iter1 = int.Parse(p[1], CultureInfo.InvariantCulture);
            var salt1 = Convert.FromHexString(p[2]);
            var iter2 = int.Parse(p[3], CultureInfo.InvariantCulture);
            var salt2 = Convert.FromHexString(p[4]);
            var hash1 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt1, iter1, HashAlgorithmName.SHA256, 32);
            var hash2 = Rfc2898DeriveBytes.Pbkdf2(hash1, salt2, iter2, HashAlgorithmName.SHA256, 32);
            return $"{p[4]}${Convert.ToHexString(hash2).ToLowerInvariant()}";
        }
        // Altes Verfahren (FRITZ!OS vor 7.24): Zeichen > 255 werden durch „.“ ersetzt
        var safe = new string((challenge + "-" + password).Select(c => c > 255 ? '.' : c).ToArray());
        var md5 = MD5.HashData(Encoding.Unicode.GetBytes(safe));
        return challenge + "-" + Convert.ToHexString(md5).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}
