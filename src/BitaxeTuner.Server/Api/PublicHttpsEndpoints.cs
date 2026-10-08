using System.Net;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

/// <summary>0.9.12 HTTPS ohne Warnung (nur Admin): DuckDNS-Name und Token, Let's Encrypt holen, Stand anzeigen.</summary>
public static class PublicHttpsEndpoints
{
    /// <param name="Token">null = unverändert, „-“ = entfernen, sonst neues DuckDNS-Token.</param>
    public sealed record PublicHttpsRequest(bool Enabled, string? Subdomain, string? Email, bool Staging, string? Ip, string? Token);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/admin/public-https", async (HubService hub, PublicCertificates certs, ServerSettings settings) => Results.Json(await hub.RunAsync(h =>
        {
            var s = h.Config.PublicHttps;
            return new
            {
                settings = new { s.Enabled, s.Subdomain, s.Email, s.Staging, s.Ip },
                status = certs.Status(s, h.Secrets.Has(PublicCertificates.TokenKey)),
                https = settings.Https,
                port = settings.Port,
                url = s.HostName.Length > 0 ? $"https://{s.HostName}:{settings.Port}/" : null,
                suggestedIp = Core.Discovery.NetworkScanner.LocalIPv4Addresses().FirstOrDefault()?.ToString(),
            };
        })));

        g.MapPut("/admin/public-https", async (PublicHttpsRequest req, HubService hub, PublicCertificates certs) => Results.Json(await hub.RunAsync(h =>
        {
            var sub = (req.Subdomain ?? "").Trim().ToLowerInvariant();
            if (sub.EndsWith(".duckdns.org", StringComparison.Ordinal)) sub = sub[..^".duckdns.org".Length];
            if (sub.Length > 0 && !PublicHttpsSettings.ValidSubdomain(sub))
                throw new LocalizedException("DuckDNS-Name: nur Kleinbuchstaben, Ziffern und Bindestrich (z. B. meinminer).");
            var ip = (req.Ip ?? "").Trim();
            if (ip.Length > 0 && (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                throw new LocalizedException("Adresse bitte als IPv4 angeben (z. B. 192.168.1.20) oder leer lassen.");
            var email = (req.Email ?? "").Trim();
            if (email.Length > 0 && (email.Length > 100 || !email.Contains('@') || email.Any(char.IsWhiteSpace)))
                throw new LocalizedException("E-Mail-Adresse ungültig.");
            if (req.Token is { } token)
            {
                var t = token.Trim();
                if (t == "-") h.Secrets.Set(PublicCertificates.TokenKey, null);
                else if (t.Length > 0)
                {
                    if (t.Length is < 20 or > 64 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                        throw new LocalizedException("DuckDNS-Token ungültig – bitte so kopieren, wie es auf duckdns.org steht.");
                    h.Secrets.Set(PublicCertificates.TokenKey, t);
                }
            }
            if (req.Enabled && (sub.Length == 0 || !h.Secrets.Has(PublicCertificates.TokenKey)))
                throw new LocalizedException("Zum Einschalten DuckDNS-Name und Token eintragen.");

            var s = h.Config.PublicHttps;
            var changed = s.Enabled != req.Enabled || s.Subdomain != sub || s.Staging != req.Staging;
            (s.Enabled, s.Subdomain, s.Email, s.Staging, s.Ip) = (req.Enabled, sub, email, req.Staging, ip);
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, req.Enabled
                ? L.T("HTTPS ohne Warnung eingeschaltet: {0}{1}.", s.HostName, req.Staging ? L.T(" (Testzertifikat)") : "")
                : L.T("HTTPS ohne Warnung ausgeschaltet."));
            if (req.Enabled && changed) certs.RequestNow();
            return new { ok = true, status = certs.Status(s, h.Secrets.Has(PublicCertificates.TokenKey)) };
        })));

        g.MapPost("/admin/public-https/issue", async (HubService hub, PublicCertificates certs) => Results.Json(await hub.RunAsync(h =>
        {
            if (!h.Config.PublicHttps.Enabled) throw new LocalizedException("HTTPS ohne Warnung ist nicht eingeschaltet.");
            certs.RequestNow();
            return new { ok = true };
        })));
    }
}
