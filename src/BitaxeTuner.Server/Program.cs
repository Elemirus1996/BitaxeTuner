using System.Net;
using BitaxeTuner.Server;
using BitaxeTuner.Server.Api;
using BitaxeTuner.Server.Security;

// BitaxeTuner-Server: der Motor der Desktop-App als Dienst (Windows-Dienst, systemd, Docker) mit REST-API,
// Live-Ereignissen und Browser-Oberfläche. Siehe README „24/7-Betrieb“.
var settings = ServerSettings.FromArgs(args);
Console.OutputEncoding = System.Text.Encoding.UTF8; // Umlaute im Protokoll (Windows-Konsole)

// Erster Start eines vorbereiteten Pi: Einrichtungspaket von der Boot-Partition übernehmen, dann beenden
var provisionIndex = Array.IndexOf(args, "--provision");
if (provisionIndex >= 0)
{
    if (provisionIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("Aufruf: BitaxeTuner.Server --provision <Ordner> [--data <Datenordner>]");
        return 2;
    }
    try
    {
        foreach (var line in BitaxeTuner.Core.Transfer.Provisioning.Apply(args[provisionIndex + 1], settings.DataDirectory))
            Console.WriteLine(line);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Einrichtungspaket konnte nicht übernommen werden: " + ex.Message);
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "BitaxeTuner");
builder.Host.UseSystemd();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
// Nicht jede Anfrage protokollieren (Handy-Ansicht fragt laufend ab; SD-Karte des Pi schonen)
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<HubService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubService>());
builder.Services.AddSingleton(sp => new AuthStore(sp.GetRequiredService<ServerSettings>().DataDirectory,
    () => sp.GetRequiredService<HubService>().Hub.Config));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<Lockout>();
builder.Services.AddSingleton<EventStream>();
builder.Services.AddSingleton<ServerUpdater>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerUpdater>());
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024; // Datenübertragung (history.db) – Einzelheiten im Import
    void Listen(IPAddress ip)
    {
        k.Listen(ip, settings.Port, o =>
        {
            if (settings.Https) o.UseHttps(Certificates.LoadOrCreate(settings.DataDirectory));
        });
    }
    if (settings.Bind is { } bind) Listen(bind);
    else k.ListenAnyIP(settings.Port, o => { if (settings.Https) o.UseHttps(Certificates.LoadOrCreate(settings.DataDirectory)); });
});

var app = builder.Build();
settings = app.Services.GetRequiredService<ServerSettings>(); // Tests ersetzen die Einstellungen per DI
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BitaxeTuner.Server");

// 1. Nur Heimnetz/VPN (außer ausdrücklich freigegeben), Sicherheits-Header, Anmeldung auflösen
app.Use(async (http, next) =>
{
    var remote = http.Connection.RemoteIpAddress;
    if (!settings.AllowPublic && remote is not null && !NetworkRules.IsPrivate(remote))
    {
        http.Response.StatusCode = 403;
        await http.Response.WriteAsync("BitaxeTuner-Server: nur aus dem Heimnetz oder per VPN erreichbar.");
        return;
    }
    var h = http.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
                                   "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    http.Items[typeof(AuthContext)] = AuthContext.Resolve(http,
        http.RequestServices.GetRequiredService<SessionStore>(), http.RequestServices.GetRequiredService<AuthStore>(), DateTime.UtcNow);
    await next();
});

// 2. API und Oberfläche
Endpoints.Map(app);
StaticUi.Map(app);

// 3. Hinweise beim Start: Adresse, Einrichtungs-Code, Zertifikat
app.Lifetime.ApplicationStarted.Register(() =>
{
    var auth = app.Services.GetRequiredService<AuthStore>();
    var scheme = settings.Https ? "https" : "http";
    log.LogInformation("BitaxeTuner-Server {Version} läuft auf {Scheme}://<diese-IP>:{Port}/ – Datenordner {Dir}",
        Endpoints.Version, scheme, settings.Port, settings.DataDirectory);
    foreach (var ip in BitaxeTuner.Core.Discovery.NetworkScanner.LocalIPv4Addresses().Where(NetworkRules.IsPrivate))
        log.LogInformation("  im Browser öffnen: {Scheme}://{Ip}:{Port}/", scheme, ip, settings.Port);
    if (settings.Https)
        log.LogInformation("HTTPS-Zertifikat (SHA-256): {Fingerprint}", Certificates.Fingerprint(Certificates.LoadOrCreate(settings.DataDirectory)));
    if (auth.SetupCode is { } code)
        log.LogWarning("Noch nicht eingerichtet. Einrichtungs-Code: {Code} – im Browser eingeben und ein Admin-Passwort festlegen.", code);
    if (settings.AllowPublic)
        log.LogWarning("Zugriff auch von außerhalb des Heimnetzes erlaubt (--allow-public). Empfohlen ist ein VPN (z. B. Tailscale/WireGuard).");
});

app.Run();
return 0;

/// <summary>Einstiegspunkt (für Tests mit WebApplicationFactory sichtbar).</summary>
public partial class Program;
