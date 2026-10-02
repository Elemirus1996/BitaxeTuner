using System.Reflection;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

public sealed record LoginRequest(string Password);
public sealed record SetupRequest(string Code, string Password);
public sealed record PasswordRequest(string Current, string Password);
public sealed record NotifyTestRequest(string? TargetId);
public sealed record OnboardingRequest(bool Show);
/// <summary>SoakHours: nach der Änderung automatisch einen Dauertest dieser Dauer starten (Effizienz-Ratgeber).</summary>
public sealed record ChangeRequest(int Frequency, int Voltage, int? SoakHours = null);
public sealed record BenchmarkRequest(BenchmarkSettings? Settings, bool Resume);
public sealed record SoakRequest(int Hours);
/// <summary>Einstellungen übertragen: Quelle, Ziele (Geräte-IDs) und Bereiche („pool“, „fan“).</summary>
public sealed record CopySettingsRequest(string Source, string[] Targets, string[] Groups);
public sealed record SoakBatchRequest(int Hours, List<string>? Ids);
public sealed record IdRequest(string Id);
public sealed record RuleRequest(string Rule);
public sealed record AutomationRequest(List<TuningPreset>? Presets, ThermalGuardRule? ThermalGuard, PresetScheduleRule? Schedule);
public sealed record DeviceRequest(string? Name, string? Host, string? WalletAddress, string? Coin, string? FirmwareRepo, bool? LogAlerts);
public sealed record TokenRequest(string? Name);
public sealed record SnapshotRequest(string File, List<string>? Fields);
public sealed record PauseRequest(bool Paused);
public sealed record FanFirmwareRequest(bool Reinstall);
public sealed record OverrideRequest(string Mode);

/// <summary>REST-API /api/v1 – Rollen: öffentlich (Info, Einrichtung, Anmeldung), Nur ansehen, Admin.</summary>
public static class Endpoints
{
    public const int ApiVersion = 1;

    public static string Version =>
        typeof(Endpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Endpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter(HandleErrors);
        MapPublic(api);

        var viewer = api.MapGroup("").AddEndpointFilter(Require(Role.Viewer));
        MapViewer(viewer);

        var admin = api.MapGroup("").AddEndpointFilter(Require(Role.Admin));
        MapDevices(admin);
        MapAdmin(admin);
    }

    // ---------- Filter ----------

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(Role role) => async (ctx, next) =>
    {
        var auth = AuthContext.Of(ctx.HttpContext);
        if (auth.Role == Role.None) return Error(401, L.N("Bitte anmelden."));
        if (auth.Role < role) return Error(403, L.N("Dafür sind Admin-Rechte nötig."));
        if (!auth.CsrfOk(ctx.HttpContext.Request)) return Error(403, L.N("CSRF-Prüfung fehlgeschlagen – Seite neu laden."));
        return await next(ctx);
    };

    private static async ValueTask<object?> HandleErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (LocalizedException ex) { return Error(ex.Status, ex.Text, ex.Args); }
        catch (InvalidOperationException ex) { return Error(400, ex.Message); }
        catch (MinerApiException ex) { return Error(502, ex.Message); }
        catch (KeyNotFoundException ex) { return Error(404, ex.Message); }
    }

    /// <summary>Sprache der Anfrage (die Browser-Oberfläche schickt ihre Wahl als Accept-Language).</summary>
    public static Loc LangOf(HttpContext http) => Loc.ForRequest(http.Request.Headers.AcceptLanguage.ToString());

    /// <summary>
    /// Fehlermeldung {"error": …}, erst beim Senden in die Sprache der Anfrage übersetzt. Der deutsche Text ist der
    /// Schlüssel; unbekannte Texte (z. B. Meldungen eines Miners) bleiben, wie sie sind.
    /// </summary>
    public static IResult Error(int status, string message, params object?[] args) => new TranslatedError(status, message, args);

    private sealed class TranslatedError(int status, string message, object?[] args) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            var loc = LangOf(http);
            var text = args.Length == 0 ? loc.T(message) : loc.T(message, args);
            return Results.Json(new { error = text }, statusCode: status).ExecuteAsync(http);
        }
    }

    internal static void ValidateFans(Core.Config.FanSettings f, MinerHub hub)
    {
        f.Port = string.IsNullOrWhiteSpace(f.Port) ? "auto" : f.Port.Trim();
        for (var ch = 1; ch <= Core.Config.FanSettings.ChannelCount; ch++)
        {
            var c = f.Channel(ch);
            if (c.Role is not ("none" or "miner" or "case")) throw new LocalizedException("K{0}: unbekannte Rolle.", ch);
            if (c.Mode is not ("auto" or "manual")) throw new LocalizedException("K{0}: unbekannter Modus.", ch);
            if (c.Role == "miner" && hub.Device(c.MinerHost ?? "") is null) throw new LocalizedException("K{0}: Miner auswählen.", ch);
            CheckCurve(c.Curve, L.N("K{0}"), ch);
            c.ManualPercent = Math.Clamp(c.ManualPercent, 0, 100);
            c.Name = (c.Name ?? "").Trim();
        }
        f.Channels = f.Channels.Where(c => c.Channel is >= 1 and <= Core.Config.FanSettings.ChannelCount).OrderBy(c => c.Channel).ToList();
        CheckCurve(f.Case.Curve, L.N("Gehäuse"));
        if (f.Case.Mode is not ("auto" or "manual")) throw new InvalidOperationException(L.N("Gehäuse: unbekannter Modus."));
        if (f.Case.Sensor is not ("vr" or "asic" or "case")) f.Case.Sensor = "vr";
        f.CaseTempWarn = Math.Clamp(f.CaseTempWarn, 20, 80);
        f.Sensors = (f.Sensors ?? []).Where(s => s.Id is { Length: > 0 } id && !id.StartsWith('#'))
            .GroupBy(s => s.Id.Trim().ToLowerInvariant()).Select(g => g.First()).ToList();
        foreach (var s in f.Sensors)
        {
            s.Id = s.Id.Trim().ToLowerInvariant();
            s.Name = (s.Name ?? "").Trim();
            if (s.Name.Length == 0) throw new InvalidOperationException(L.N("Jeder Temperaturfühler braucht einen Namen."));
            if (s.Name.Length > 24) s.Name = s.Name[..24];
            s.WarnTemp = Math.Clamp(s.WarnTemp, 20, 100);
        }
        f.Case.ManualPercent = Math.Clamp(f.Case.ManualPercent, 0, 100);
        f.Case.UnknownPercent = Math.Clamp(f.Case.UnknownPercent, 0, 100);
        f.Case.NightMaxPercent = Math.Clamp(f.Case.NightMaxPercent, 0, 100);
        f.Case.NightFromHour = Math.Clamp(f.Case.NightFromHour, 0, 23);
        f.Case.NightToHour = Math.Clamp(f.Case.NightToHour, 0, 23);
    }

    /// <param name="label">Übersetzbarer Name der Kurve (Platzhalter {0} = <paramref name="channel"/>).</param>
    private static void CheckCurve(Core.Config.FanCurve c, string label, int channel = 0)
    {
        if (c.FullTemp <= c.StartTemp) throw new LocalizedException("{0}: Volllast-Temperatur muss über der Start-Temperatur liegen.", new LocText(label, channel));
        if (c.StartTemp < 20 || c.FullTemp > 110) throw new LocalizedException("{0}: Temperaturen zwischen 20 und 110 °C.", new LocText(label, channel));
        c.StartPercent = Math.Clamp(c.StartPercent, 0, 100);
        c.MinPercent = Math.Clamp(c.MinPercent, 0, 100);
        c.Hysteresis = Math.Clamp(c.Hysteresis, 0, 10);
    }

    private static int _scanning;

    private static List<HubDevice> CopyTargets(MinerHub h, CopySettingsRequest req) =>
        (req.Targets ?? []).Distinct().Select(id => Device(h, id)).ToList();

    private static HashSet<Core.Storage.SettingGroup> CopyGroups(CopySettingsRequest req) =>
        (req.Groups ?? []).Select(g => g switch
        {
            "pool" => Core.Storage.SettingGroup.Pool,
            "fan" => Core.Storage.SettingGroup.Fan,
            _ => throw new LocalizedException("Unbekannter Bereich: {0}", g),
        }).ToHashSet();

    private static object CopyResult(List<CopyPreview> list) => list.Select(p => new
    {
        id = Dto.DeviceId(p.Device.Host),
        name = p.Device.Title,
        error = p.Error,
        changes = p.Changes.Select(c => new { group = c.GroupText, label = c.Label, current = c.Current, next = c.Saved }),
    });

    private static HubDevice Device(MinerHub hub, string id) =>
        Dto.Find(hub, id) ?? throw new KeyNotFoundException(L.N("Gerät nicht gefunden."));

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "?";

    // ---------- Öffentlich ----------

    private static void MapPublic(RouteGroupBuilder api)
    {
        api.MapGet("/info", (HttpContext http, AuthStore auth, HubService hub) => Results.Json(new
        {
            name = "BitaxeTuner-Server",
            version = Version,
            apiVersion = ApiVersion,
            setupRequired = !auth.IsSetUp,
            role = AuthContext.Of(http).Role.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            paused = hub.Hub.IsPaused,
            devices = hub.Hub.Config.Devices.Count,
            language = Core.I18n.Loc.Current.Language,
        }));

        // Übersetzungstabelle für die Browser-Oberfläche (deutscher Text → Text der Sprache); auch vor der Anmeldung
        api.MapGet("/i18n/{lang}", (string lang, HttpContext http) =>
        {
            var language = Core.I18n.Loc.Resolve(lang);
            var json = System.Text.Json.JsonSerializer.Serialize(Core.I18n.Loc.Table(language));
            var etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16] + "\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";
            if (http.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(304);
            return Results.Text(json, "application/json; charset=utf-8");
        });

        // Desktop-App: aus dem API-Token eine Browser-Sitzung für die eingebettete Oberfläche machen
        api.MapPost("/token-session", (HttpContext http, SessionStore sessions) =>
        {
            var auth = AuthContext.Of(http);
            if (!auth.ViaToken || auth.Role != Role.Admin) return Error(401, L.N("Nur mit API-Token."));
            var session = sessions.Create(Role.Admin, DateTime.UtcNow);
            return Results.Json(new { session = session.Id, csrf = session.Csrf, expiresUtc = session.ExpiresUtc });
        });

        api.MapPost("/setup", (SetupRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout) =>
        {
            var client = Client(http);
            if (lockout.IsLocked(client, DateTime.UtcNow)) return Error(429, L.N("Zu viele Fehlversuche – bitte 5 Minuten warten."));
            if (auth.Setup(req.Code ?? "", req.Password ?? "") is { } error)
            {
                if (error.German.Contains("Code")) lockout.Fail(client, DateTime.UtcNow);
                return Error(400, "{0}", error);
            }
            lockout.Success(client);
            return StartSession(http, sessions, Role.Admin);
        });

        api.MapPost("/login", (LoginRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout) =>
        {
            var client = Client(http);
            var now = DateTime.UtcNow;
            if (lockout.IsLocked(client, now)) return Error(429, L.N("Zu viele Fehlversuche – bitte 5 Minuten warten."));
            var role = auth.Login(req.Password ?? "");
            if (role == Role.None)
            {
                lockout.Fail(client, now);
                return Error(401, L.N("Passwort oder PIN falsch."));
            }
            lockout.Success(client);
            return StartSession(http, sessions, role);
        });

        api.MapPost("/logout", (HttpContext http, SessionStore sessions) =>
        {
            sessions.Remove(http.Request.Cookies[AuthContext.CookieName]);
            http.Response.Cookies.Delete(AuthContext.CookieName);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/session", (HttpContext http) =>
        {
            var auth = AuthContext.Of(http);
            return Results.Json(new { role = auth.Role.ToString(), csrf = auth.Session?.Csrf });
        });
    }

    private static IResult StartSession(HttpContext http, SessionStore sessions, Role role)
    {
        var session = sessions.Create(role, DateTime.UtcNow);
        http.Response.Cookies.Append(AuthContext.CookieName, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Expires = session.ExpiresUtc,
            Path = "/",
        });
        return Results.Json(new { role = role.ToString(), csrf = session.Csrf });
    }

    // ---------- Nur ansehen ----------

    private static void MapViewer(RouteGroupBuilder g)
    {
        g.MapGet("/status", async (HttpContext http, HubService hub) =>
            Results.Json(await hub.RunAsync(h => Dto.Status(h, AuthContext.Of(http).Role, DateTime.Now))));

        g.MapGet("/devices/{id}", async (string id, HttpContext http, HubService hub) =>
            Results.Json(await hub.RunAsync(h => Dto.Detail(h, Device(h, id), AuthContext.Of(http).Role))));

        g.MapGet("/devices/{id}/history", async (string id, string? range, HubService hub) =>
            Results.Json(await hub.RunAsync(h => Dto.History(h, id == "all" ? HistoryStore.AggregateHost : Device(h, id).Host, range ?? "1h", DateTime.Now))));

        g.MapGet("/plugs/{id}/history", async (string id, string? range, HubService hub) =>
            Results.Json(await hub.RunAsync(h => Dto.PlugHistory(h, id, range ?? "24h", DateTime.Now))));

        // Gesundheits-Frühwarnung: letzte 7 Tage gegen die 4 Wochen davor
        g.MapGet("/devices/{id}/health", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var (findings, recent, @base) = h.HealthOf(Device(h, id).Host, DateTime.Now);
            return new { findings = findings.Select(f => new { f.Code, f.Title, f.Text }).ToList(), recent, @base };
        })));

        g.MapGet("/devices/{id}/comparisons", async (string id, HubService hub) =>
            Results.Json(await hub.RunAsync(h => h.Comparisons(Device(h, id)).Select(Dto.Comparison).ToList())));

        g.MapGet("/events", (HttpContext http, EventStream events) => events.ServeAsync(http, AuthContext.Of(http).Role));

        // Vergleichsbericht zum Ausdrucken – ohne IP- und Wallet-Adressen, daher auch für „Nur ansehen“
        g.MapGet("/compare/report", async (string ids, string? range, string? values, string? charts, HubService hub) =>
        {
            static string[] Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var html = await hub.RunAsync(h =>
            {
                var devices = Split(ids).Distinct().Select(id => Device(h, id)).ToList();
                var report = Core.Reports.CompareReports.Build(h, devices, range ?? "24h",
                    values is null ? Core.Reports.CompareReports.DefaultValues : Split(values),
                    charts is null ? Core.Reports.CompareReports.DefaultCharts : Split(charts), DateTime.Now);
                return Core.Reports.CompareReports.Html(report);
            });
            return Results.Content(html, "text/html; charset=utf-8");
        });

        g.MapGet("/display", async (HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            status = h.DisplayStatus,
            settings = AuthContext.Of(http).Role == Role.Admin ? Dto.Copy(h.Config.Display) : null,
            rebootAvailable = h.Options.SystemReboot is not null,
        })));

        // Vorschau genau so, wie die Anzeige es zeigt (auch ohne Hardware)
        // Vorschau: ohne scene genau das, was als Nächstes käme; mit scene=Daily|Chart|… eine bestimmte Seite
        g.MapGet("/display/preview.png", async (string? scene, HubService hub) =>
        {
            var model = await hub.RunAsync(h => Enum.TryParse<Core.Display.DisplayScene>(scene, true, out var sc)
                ? h.PreviewScene(sc, DateTime.Now)
                : h.ComposeDisplay(DateTime.Now));
            using var img = Core.Display.StatusRenderer.RenderImage(model);
            var ms = new MemoryStream();
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(img, ms);
            return Results.File(ms.ToArray(), "image/png");
        });

        g.MapGet("/fans", async (HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            status = Dto.Fans(h, AuthContext.Of(http).Role),
            enabled = h.Config.Fans.Enabled,
            settings = AuthContext.Of(http).Role == Role.Admin ? Dto.Copy(h.Config.Fans) : null,
            miners = h.Devices.Select(d => new { id = Dto.DeviceId(d.Host), name = d.Title, host = AuthContext.Of(http).Role == Role.Admin ? d.Host : null }).ToList(),
        })));
    }

    // ---------- Admin: Geräte ----------

    private static void MapDevices(RouteGroupBuilder g)
    {
        g.MapPost("/devices", async (DeviceRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var host = req.Host?.Trim() ?? "";
            if (host.Length == 0) throw new InvalidOperationException(L.N("Adresse fehlt."));
            if (h.Config.Devices.Any(d => string.Equals(d.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L.N("Dieser Host ist bereits eingetragen."));
            h.Config.Devices.Add(new DeviceConfig { Name = string.IsNullOrWhiteSpace(req.Name) ? host : req.Name.Trim(), Host = host });
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { id = Dto.DeviceId(host) };
        })));

        // Netzwerksuche wie in der Desktop-App: nur die privaten /24-Netze des Servers, eine Suche zur Zeit
        g.MapPost("/devices/scan", async (HubService hub, CancellationToken ct) =>
        {
            if (Interlocked.Exchange(ref _scanning, 1) == 1) throw new LocalizedException("Eine Suche läuft bereits.");
            try
            {
                var found = await Core.Discovery.NetworkScanner.ScanAsync(ct: ct, include: NetworkRules.IsPrivate);
                var known = await hub.RunAsync(h => h.Config.Devices.Select(d => d.Host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase));
                return Results.Json(new
                {
                    networks = Core.Discovery.NetworkScanner.LocalIPv4Addresses().Where(NetworkRules.IsPrivate)
                        .Select(ip => { var b = ip.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}.0/24"; }).Distinct(),
                    miners = found.Select(f => new
                    {
                        address = f.Address,
                        name = f.Info.Hostname,
                        model = f.Info.DeviceModel ?? f.Info.AsicModel,
                        hashrate = f.Info.HashRateGh,
                        known = known.Contains(f.Address),
                    }),
                });
            }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        });

        // Einstellungen (Pool, Lüfter – nie Frequenz/Spannung) von einem Miner auf andere übertragen
        g.MapPost("/devices/copy/preview", async (CopySettingsRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            CopyResult(await h.CopySettingsPreviewAsync(Device(h, req.Source), CopyTargets(h, req), CopyGroups(req))))));

        g.MapPost("/devices/copy", async (CopySettingsRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
            CopyResult(await h.CopySettingsAsync(Device(h, req.Source), CopyTargets(h, req), CopyGroups(req))))));

        g.MapPut("/devices/{id}", async (string id, DeviceRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var c = Device(h, id).Config;
            if (req.Name is { } name && name.Trim().Length > 0) c.Name = name.Trim();
            if (req.WalletAddress is { } w) c.WalletAddress = w.Trim();
            if (req.Coin is "Auto" or "BTC" or "BCH") c.Coin = req.Coin;
            if (req.FirmwareRepo is { } repo) c.FirmwareRepo = repo.Trim();   // leer = keine Firmware-Prüfung (wie am Desktop)
            if (req.LogAlerts is { } la) c.LogAlerts = la;
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { ok = true };
        })));

        g.MapDelete("/devices/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException(L.N("Bitte zuerst den laufenden Benchmark stoppen."));
            h.Config.Devices.RemoveAll(c => string.Equals(c.Host.Trim(), d.Config.Host.Trim(), StringComparison.OrdinalIgnoreCase));
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { ok = true, note = "Verlauf in history.db und Steuerdaten bleiben erhalten." };
        })));

        g.MapPost("/devices/{id}/profile", async (string id, IdRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException(L.N("Während eines Benchmarks nicht möglich."));
            var profile = h.ProfilesFor(d).FirstOrDefault(p => p.Id == req.Id) ?? throw new KeyNotFoundException(L.N("Profil unbekannt."));
            h.SetProfile(d, profile);
            return new { ok = true };
        })));

        // Frequenz/Spannung: erst Vorschau (Text mit altem und neuem Wert), dann bestätigt ausführen
        g.MapPost("/devices/{id}/change/preview", async (string id, ChangeRequest req, HubService hub) =>
            Results.Json(await hub.RunAsync(async h => await h.PreviewChangeAsync(Device(h, id), req.Frequency, req.Voltage))));

        g.MapPost("/devices/{id}/change", async (string id, ChangeRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            if (req.SoakHours is { } hours && hours is < 1 or > 168) throw new InvalidOperationException(L.N("Dauertest: 1 bis 168 Stunden."));
            await h.ApplyChangeAsync(d, req.Frequency, req.Voltage);
            if (req.SoakHours is { } soakHours) h.ScheduleSoak(d, soakHours);
            return new { ok = true };
        })));

        g.MapPost("/devices/{id}/restart", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException(L.N("Während eines Benchmarks nicht möglich."));
            await d.Connection.RestartAsync(); // öffnet das Wartungsfenster: keine Offline-Meldung
            d.AddLog(L.T("Neustart ausgelöst (Browser)."), EventCategories.Connection);
            return new { ok = true };
        })));

        // Benchmark
        g.MapPost("/devices/{id}/benchmark/prepare", async (string id, BenchmarkRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var plan = await h.Benchmarks.PrepareAsync(d, req.Settings ?? BenchmarkSettings.FromProfile(d.Profile), req.Resume);
            return new { confirmText = plan.ConfirmText, settings = plan.Settings, estimated = BenchmarkManager.EstimatedDurationText(plan.Settings) };
        })));

        g.MapPost("/devices/{id}/benchmark/start", async (string id, BenchmarkRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var plan = await h.Benchmarks.PrepareAsync(d, req.Settings ?? BenchmarkSettings.FromProfile(d.Profile), req.Resume);
            d.AddLog(L.T("Benchmark im Browser bestätigt."), EventCategories.Benchmark);
            _ = h.Benchmarks.RunAsync(d, plan); // läuft im Hub weiter, Fortschritt über /events
            return new { ok = true };
        })));

        g.MapPost("/devices/{id}/benchmark/stop", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.Benchmarks.Stop(Device(h, id));
            return new { ok = true };
        })));

        g.MapPost("/devices/{id}/benchmark/pause", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
            new { paused = h.Benchmarks.TogglePause(Device(h, id)) })));

        // Automatik
        g.MapPut("/devices/{id}/automation", async (string id, AutomationRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            var c = d.Config;
            if (req.Presets is { } presets)
            {
                foreach (var p in presets)
                    if (MinerHub.CheckPreset(d, p) is { } error) throw new InvalidOperationException(error);
                c.Presets = presets;
            }
            // Freigaben bleiben serverseitig: nur die Prüfsumme entscheidet, ob eine geänderte Regel noch freigegeben ist
            if (req.ThermalGuard is { } g1)
            {
                g1.ApprovedSignature = c.ThermalGuard.ApprovedSignature;
                g1.ApprovedAt = c.ThermalGuard.ApprovedAt;
                c.ThermalGuard = g1;
            }
            if (req.Schedule is { } s)
            {
                s.ApprovedSignature = c.Schedule.ApprovedSignature;
                s.ApprovedAt = c.Schedule.ApprovedAt;
                c.Schedule = s;
            }
            h.Config.Save();
            d.AddLog(L.T("Automatik-Einstellungen gespeichert (Browser)."), EventCategories.Automation);
            return new { thermalGuardApproved = c.ThermalGuard.IsApproved(d.Host), scheduleApproved = c.Schedule.IsApproved(d.Host) };
        })));

        g.MapPost("/devices/{id}/automation/approval-text", async (string id, RuleRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            return new { text = req.Rule == "thermal" ? MinerHub.ThermalGuardApprovalText(d) : h.ScheduleApprovalText(d) };
        })));

        g.MapPost("/devices/{id}/automation/approve", async (string id, RuleRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            if (req.Rule == "thermal") h.ApproveRule(d, d.Config.ThermalGuard, "Temperaturschutz");
            else h.ApproveRule(d, d.Config.Schedule, "Zeitplan");
            return new { ok = true };
        })));

        // Dauertest
        g.MapPost("/devices/{id}/soak/prepare", async (string id, SoakRequest req, HubService hub) =>
            Results.Json(await hub.RunAsync(h => new { text = h.SoakConfirmText(Device(h, id), req.Hours) })));

        g.MapPost("/devices/{id}/soak/start", async (string id, SoakRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.StartSoak(Device(h, id), req.Hours);
            return new { ok = true };
        })));

        g.MapPost("/devices/{id}/soak/stop", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.StopSoak(Device(h, id));
            return new { ok = true };
        })));

        // Dauertest für mehrere Miner: Auswahl mit aktueller Einstellung, Start, alle abbrechen
        g.MapPost("/soak/prepare", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            miners = h.SoakBatchPreview().Select(e => new
            {
                id = Dto.DeviceId(e.Device.Host), name = e.Device.Title, e.Eligible, e.Reason, e.FrequencyMhz, e.CoreVoltageMv, e.Running,
                until = e.Device.Config.Soak?.Until,
            }).ToList(),
        })));

        g.MapPost("/soak/start", async (SoakBatchRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var hosts = (req.Ids ?? []).Select(id => Dto.Find(h, id)?.Host).OfType<string>().ToList();
            var results = h.StartSoakBatch(hosts, req.Hours);
            return new
            {
                started = results.Count(r => r.Started),
                results = results.Select(r => new { id = Dto.DeviceId(r.Device.Host), name = r.Device.Title, r.Started, r.Message }).ToList(),
            };
        })));

        // Effizienz-Ratgeber: Vorschläge je Miner (Ziel: efficiency | balanced | hashrate)
        g.MapGet("/advisor", async (string? goal, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var g = Enum.TryParse<Core.Advisor.AdvisorGoal>(goal, true, out var parsed) ? parsed : Core.Advisor.AdvisorGoal.Balanced;
            return new
            {
                goal = g.ToString(),
                currency = h.Config.Currency,
                ctPerKwh = Core.Plugs.EnergyCost.FixedCt(h.Config),
                miners = h.Advise(g, DateTime.Now).Select(r => new
                {
                    id = Dto.DeviceId(r.Host), r.Name, r.FrequencyMhz, r.CoreVoltageMv, r.HashrateGh, r.PowerW, r.Jth, r.Basis, r.Note, r.WallFactor,
                    candidates = r.Candidates.Select(c => new { goal = c.Goal.ToString(), c.FrequencyMhz, c.CoreVoltageMv, c.HashrateGh, c.PowerW, c.Jth, c.SoakPassed, c.Confidence, c.DeltaW, c.DeltaGh, c.MonthlyCostDelta, c.WallPowerW, c.WallJth }).ToList(),
                    recommended = r.Recommended is { } c ? new { goal = c.Goal.ToString(), c.FrequencyMhz, c.CoreVoltageMv, c.HashrateGh, c.PowerW, c.Jth, c.SoakPassed, c.Confidence, c.DeltaW, c.DeltaGh, c.MonthlyCostDelta, c.WallPowerW, c.WallJth } : null,
                }).ToList(),
            };
        })));

        g.MapPost("/soak/stop-all", async (HubService hub) => Results.Json(await hub.RunAsync(h => new { stopped = h.StopAllSoaks() })));

        g.MapPost("/devices/{id}/suggestion/dismiss", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.DismissSuggestion(Device(h, id));
            return new { ok = true };
        })));

        // Einstellungen des Miners sichern / wiederherstellen
        g.MapGet("/devices/{id}/snapshots", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
            h.Snapshots.List(Device(h, id).Host).Select(s => new
            {
                file = Path.GetFileName(s.FilePath), s.DisplayText, s.TakenAt, s.Reason, s.FirmwareVersion,
            }).ToList())));

        g.MapPost("/devices/{id}/snapshots", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var snap = await h.BackupSettingsAsync(Device(h, id));
            return new { file = Path.GetFileName(snap.FilePath), snap.DisplayText };
        })));

        g.MapPost("/devices/{id}/snapshots/diff", async (string id, SnapshotRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var (snaps, current) = await h.RestoreCandidatesAsync(d);
            var snap = snaps.FirstOrDefault(s => Path.GetFileName(s.FilePath) == req.File) ?? throw new KeyNotFoundException(L.N("Sicherung nicht gefunden."));
            return SettingsSnapshots.Diff(snap, current).Select(c => new { c.Field, c.Label, group = c.GroupText, c.Current, c.Saved }).ToList();
        })));

        g.MapPost("/devices/{id}/snapshots/restore", async (string id, SnapshotRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var (snaps, current) = await h.RestoreCandidatesAsync(d);
            var snap = snaps.FirstOrDefault(s => Path.GetFileName(s.FilePath) == req.File) ?? throw new KeyNotFoundException(L.N("Sicherung nicht gefunden."));
            var fields = req.Fields ?? [];
            var changes = SettingsSnapshots.Diff(snap, current).Where(c => fields.Contains(c.Field)).ToList();
            if (changes.Count == 0) throw new InvalidOperationException(L.N("Keine Felder ausgewählt."));
            await h.RestoreAsync(d, snap, changes);
            return new { ok = true, restored = changes.Count };
        })));

        // Live-Logs des Miners (eine WebSocket-Verbindung je Miner, geteilt mit Log-Alarmen)
        g.MapGet("/devices/{id}/minerlog", async (string id, HttpContext http, HubService hub) =>
        {
            var d = await hub.RunAsync(h => Device(h, id));
            await MinerLogStream.ServeAsync(http, d.Connection);
        });
    }

    // ---------- Admin: Server ----------

    private static void MapAdmin(RouteGroupBuilder g)
    {
        BackupEndpoints.Map(g);
        MqttEndpoints.Map(g);
        PlugEndpoints.Map(g);
        ReportEndpoints.Map(g);
        g.MapGet("/settings", async (HubService hub) => Results.Json(await hub.RunAsync(h => Dto.Copy(SettingsDto.From(h.Config)))));

        g.MapPut("/settings", async (SettingsDto req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            req.ApplyTo(h.Config);
            h.Config.Save();
            Loc.Configure(h.Config.Language);
            h.SyncLogAlerts();
            await h.ApplySettingsChangedAsync();
            return new { ok = true };
        })));

        // „Neu in dieser Version“ gesehen oder abgelehnt – bis zum nächsten Update nicht mehr fragen
        g.MapPost("/whatsnew/seen", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            WhatsNew.MarkSeen(h.Config, Version);
            h.Config.Save();
            return new { ok = true };
        })));

        // Einführung „Erste Schritte“ ausblenden (später wieder: Einstellungen → Allgemein)
        g.MapPost("/onboarding", async (OnboardingRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            Onboarding.SetVisible(h.Config, req.Show);
            h.Config.Save();
            return new { ok = true };
        })));

        // Test: ein Ziel (targetId) oder alle aktiven Ziele
        g.MapPost("/notifications/test", async (NotifyTestRequest? req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var target = req?.TargetId is { Length: > 0 } id ? h.Config.Notifications.Targets.FirstOrDefault(t => t.Id == id) : null;
            if (req?.TargetId is { Length: > 0 } && target is null) throw new LocalizedException("Push-Ziel nicht gefunden – zuerst speichern.") { Status = 404 };
            var error = target is not null ? await h.Notify.TestAsync(target) : await h.Notify.TestAsync(h.Config.Notifications);
            return new { ok = error is null, error };
        })));

        g.MapPost("/report/send", async (HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var error = await h.SendDailyReportAsync(DateTime.Now, markSent: false);
            return new { ok = error is null, error };
        })));

        g.MapPost("/password", (PasswordRequest req, AuthStore auth, SessionStore sessions) =>
        {
            if (auth.ChangePassword(req.Current ?? "", req.Password ?? "") is { } error) return Error(400, "{0}", error);
            sessions.Clear();
            return Results.Ok(new { ok = true, note = "Alle Browser-Sitzungen wurden abgemeldet." });
        });

        g.MapGet("/tokens", (AuthStore auth) => Results.Json(auth.Tokens.Select(t => new { t.Id, t.Name, t.CreatedUtc, t.LastUsedUtc })));

        g.MapPost("/tokens", (TokenRequest req, AuthStore auth) =>
        {
            var (entry, secret) = auth.CreateToken(req.Name ?? "Desktop");
            return Results.Json(new { entry.Id, entry.Name, token = secret, note = "Das Token wird nur jetzt angezeigt." });
        });

        g.MapDelete("/tokens/{tokenId}", (string tokenId, AuthStore auth) =>
            auth.RevokeToken(tokenId) ? Results.Ok(new { ok = true }) : Error(404, L.N("Token nicht gefunden.")));

        // ---------- Zusatzlüfter (Pico) ----------

        g.MapPut("/fans", async (Core.Config.FanSettings req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            ValidateFans(req, h);
            var before = System.Text.Json.JsonSerializer.Deserialize<Core.Config.FanSettings>(System.Text.Json.JsonSerializer.Serialize(h.Config.Fans))!;
            h.Config.Fans = req;
            h.Config.Save();
            h.LogFanChanges(before, req);
            await h.ApplyFanSettingsAsync();
            return new { ok = true, status = Dto.Fans(h, Role.Admin) };
        })));

        g.MapPost("/fans/firmware", async (HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            if (!h.Config.Fans.Enabled) throw new InvalidOperationException(L.N("Lüftersteuerung ist ausgeschaltet."));
            await h.ApplyFanSettingsAsync(reinstallFirmware: true);
            return new { ok = h.FanStatus.Connected, status = Dto.Fans(h, Role.Admin) };
        })));

        g.MapPost("/fans/override", async (OverrideRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var mode = req.Mode switch
            {
                "off" => Core.Fans.FanOverride.Off,
                "full" => Core.Fans.FanOverride.Full,
                "auto" => Core.Fans.FanOverride.None,
                _ => throw new InvalidOperationException(L.N("Modus: off, auto oder full.")),
            };
            await h.SetFanOverrideAsync(mode, "Browser");
            return new { ok = true, status = Dto.Fans(h, Role.Admin) };
        })));

        g.MapPost("/system/reboot", async (HubService hub, HttpContext http) =>
        {
            // Antwort zuerst, dann neu starten
            var message = await hub.RunAsync(h => h.Options.SystemReboot is not null && h.Config.Display.AllowSystemReboot
                ? L.N("Pico und Rechner werden neu gestartet – die Seite verbindet sich danach von selbst wieder.")
                : L.N("Pico wird neu gestartet (Neustart des Rechners ist hier nicht eingerichtet)."));
            _ = hub.RunAsync(h => h.RebootAsync("Browser"));
            return Results.Json(new { ok = true, message = LangOf(http).T(message) });
        });

        // ---------- E-Paper-Anzeige ----------

        g.MapPut("/display", async (Core.Config.DisplaySettings req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            req.IntervalMinutes = Math.Clamp(req.IntervalMinutes, Core.Config.DisplaySettings.MinIntervalMinutes, 240);
            req.BlockFoundHoldHours = Math.Clamp(req.BlockFoundHoldHours, 1, 168);
            req.Pages ??= new Core.Config.DisplayPages();
            req.QuietFromHour = Math.Clamp(req.QuietFromHour, 0, 23);
            req.QuietToHour = Math.Clamp(req.QuietToHour, 0, 23);
            req.Title = string.IsNullOrWhiteSpace(req.Title) ? "BitaxeTuner" : req.Title.Trim()[..Math.Min(40, req.Title.Trim().Length)];
            h.Config.Display = req;
            h.Config.Save();
            h.RequestDisplayRefresh();
            await h.ApplyFanSettingsAsync();
            return new { ok = true, status = h.DisplayStatus };
        })));

        g.MapPost("/display/refresh", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.RequestDisplayRefresh();
            return new { ok = true, status = h.DisplayStatus };
        })));

        // Wie Taste 1: Sonderanzeige quittieren oder nächste Seite
        g.MapPost("/display/next", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.DisplayNextOrAcknowledge("Browser");
            return new { ok = true, status = h.DisplayStatus };
        })));

        g.MapGet("/fans/ports", () => Results.Json(new { pico = Core.Fans.PicoFanDevice.FindPorts(), all = System.IO.Ports.SerialPort.GetPortNames() }));

        // ---------- Betrieb mit der Desktop-App ----------

        // Server-Update
        g.MapGet("/admin/update", (ServerUpdater updater) => Results.Json(new
        {
            current = Version,
            kind = ServerUpdater.DetectKind().ToString(),
            canInstall = updater.CanInstall,
            latest = updater.Latest?.Tag,
            notes = updater.Latest?.Notes,
            url = updater.Latest?.ReleaseUrl,
            lastCheck = updater.LastCheck,
            nextCheck = updater.NextCheck,
            message = updater.LastMessage,
        }));

        g.MapPost("/admin/update/check", async (ServerUpdater updater) =>
        {
            var r = await updater.CheckAsync(notify: false);
            return Results.Json(new { available = r.Status == Core.Update.UpdateCheckStatus.UpdateAvailable, latest = r.Update?.Tag, message = r.Message });
        });

        g.MapPost("/admin/update/install", (ServerUpdater updater, ILogger<ServerUpdater> log, HttpContext http) =>
        {
            if (updater.Latest is null) return Error(400, L.N("Kein Update verfügbar – zuerst nach Updates suchen."));
            if (!updater.CanInstall) return Error(400, L.N("Diese Installation aktualisiert sich nicht selbst. Docker: „docker compose pull && docker compose up -d“."));
            _ = Task.Run(async () =>
            {
                try { await updater.InstallAsync(); }
                catch (Exception ex) { log.LogError(ex, "Update fehlgeschlagen"); }
            });
            return Results.Json(new { ok = true, message = LangOf(http).T("Update wird geladen und installiert – der Server startet danach neu (ca. 1 Minute).") });
        });

        g.MapPost("/admin/pause", async (PauseRequest req, HubService hub) =>
        {
            await hub.SetPausedAsync(req.Paused);
            return Results.Json(new { paused = req.Paused });
        });

        g.MapGet("/admin/export", async (HubService hub) =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"bitaxetuner-export-{Guid.NewGuid():N}.zip");
            await hub.RunAsync(h =>
            {
                using var fs = File.Create(file);
                DataArchive.Create(h.DataDirectory, h.History, fs, "server", Version);
                return true;
            });
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"bitaxetuner-server-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        });

        g.MapPost("/admin/import", async (HttpContext http, bool? replace, HubService hub) =>
        {
            // history.db kann groß werden – für diesen Aufruf bis 2 GB zulassen
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
            if (!await hub.IsEmptyAsync() && replace != true)
                return Error(409, L.N("Der Server enthält bereits Daten (Geräte, Verlauf oder Steuerdaten). Übernahme nur nach ausdrücklicher Bestätigung – der Server sichert seinen Stand vorher."));

            var dir = hub.Settings.DataDirectory;
            var upload = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            var staging = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40]);
            try
            {
                await using (var fs = File.Create(upload)) await http.Request.Body.CopyToAsync(fs, http.RequestAborted);
                ArchiveManifest manifest;
                try
                {
                    await using var zip = File.OpenRead(upload);
                    manifest = DataArchive.ExtractAndVerify(zip, staging);
                }
                catch (InvalidDataException ex) { return Error(400, L.N("Archiv abgelehnt: {0}"), ex.Message); }
                var backup = await hub.ReplaceDataAsync(staging);
                var samples = manifest.HistoryRows.GetValueOrDefault("samples");
                return Results.Json(new
                {
                    ok = true,
                    message = LangOf(http).T("Übernommen: {0} Gerät(e), {1:N0} Verlaufswerte, {2} Dateien (Quelle {3}, {4:g}). Vorheriger Server-Stand gesichert in {5}.",
                        manifest.Devices, samples, manifest.Files.Count, manifest.Source, manifest.CreatedUtc.ToLocalTime(), Path.GetFileName(backup)),
                });
            }
            finally
            {
                try { File.Delete(upload); } catch { /* egal */ }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* egal */ }
            }
        });

        // Dauerhaftes Protokoll (history.db): Tuning, Benchmark, Dauertest, Automatik, Lüfter, Verbindung, Einstellungen, Server.
        // Nur Admin – Meldungen können Pool-Adressen enthalten.
        g.MapGet("/journal", async (string? range, string? device, string? cats, string? q, string? format, HubService hub) =>
        {
            var span = range switch { "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };
            var categories = (cats ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => EventCategories.All.Contains(c)).ToList();
            var (entries, names) = await hub.RunAsync(h =>
            {
                string? host = device is null or "" ? null : device == "server" ? "" : Device(h, device).Host;
                var now = DateTime.Now;
                var list = h.History?.QueryEvents(now - span, now, host, categories, q) ?? [];
                var titles = h.Devices.ToDictionary(d => d.Host, d => (Id: Dto.DeviceId(d.Host), d.Title), StringComparer.OrdinalIgnoreCase);
                return (list, titles);
            });
            string Name(string? host) => host is null ? L.T("Server") : names.TryGetValue(host, out var n) ? n.Title : host;
            if (format == "csv")
            {
                var sb = new System.Text.StringBuilder("\uFEFFZeit;Miner;Kategorie;Meldung\r\n");
                static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
                foreach (var e in entries)
                    sb.Append($"{e.Time:yyyy-MM-dd HH:mm:ss};{Csv(Name(e.Host))};{Csv(EventCategories.Label(e.Category))};{Csv(e.Message)}\r\n");
                return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"BitaxeTuner-Protokoll-{DateTime.Now:yyyyMMdd-HHmm}.csv");
            }
            return Results.Json(new
            {
                entries = entries.Select(e => new
                {
                    time = e.Time, device = e.Host is not null && names.TryGetValue(e.Host, out var n) ? n.Id : null,
                    name = Name(e.Host), category = e.Category, message = e.Message,
                }),
                truncated = entries.Count >= 5000,
            });
        });

        g.MapGet("/tax/rewards", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            wallets = h.TaxMonitor.Wallets,
            rewards = h.TaxMonitor.LoadRewards().OrderByDescending(r => r.ReceivedAtUtc).ToList(),
            status = h.TaxMonitor.LastPollUtc,
        })));

        g.MapGet("/tax/rewards.csv", async (HubService hub) =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"bitaxetuner-zufluesse-{Guid.NewGuid():N}.csv");
            await hub.RunAsync(h => { h.TaxRepository.ExportCsv(file, h.TaxMonitor.LoadRewards()); return true; });
            var bytes = await File.ReadAllBytesAsync(file);
            File.Delete(file);
            return Results.File(bytes, "text/csv; charset=utf-8", $"zufluesse-{DateTime.Now:yyyyMMdd}.csv");
        });
    }
}
