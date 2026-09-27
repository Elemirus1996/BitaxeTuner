namespace BitaxeTuner.Server.Security;

/// <summary>Wer fragt an: Rolle und ob per Browser-Sitzung (dann CSRF-Schutz nötig) oder per API-Token.</summary>
public sealed record AuthContext(Role Role, Session? Session, bool ViaToken)
{
    public const string CookieName = "bt_session";
    public const string CsrfHeader = "X-CSRF-Token";
    private static readonly AuthContext Anonymous = new(Role.None, null, false);

    public static AuthContext Of(HttpContext http) => http.Items[typeof(AuthContext)] as AuthContext ?? Anonymous;

    /// <summary>Rolle aus Cookie oder "Authorization: Bearer btk_…" bestimmen.</summary>
    public static AuthContext Resolve(HttpContext http, SessionStore sessions, AuthStore auth, DateTime nowUtc)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var role = auth.VerifyToken(header["Bearer ".Length..].Trim());
            return role == Role.None ? Anonymous : new AuthContext(role, null, true);
        }
        var session = sessions.Get(http.Request.Cookies[CookieName], nowUtc);
        return session is null ? Anonymous : new AuthContext(session.Role, session, false);
    }

    /// <summary>Schreibende Aufrufe aus dem Browser brauchen den CSRF-Wert der Sitzung im Header.</summary>
    public bool CsrfOk(HttpRequest request) =>
        ViaToken || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
        (Session is { } s && request.Headers[CsrfHeader].ToString() is { Length: > 0 } sent &&
         System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
             System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(s.Csrf)));
}
