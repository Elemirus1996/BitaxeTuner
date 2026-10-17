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
public sealed record MinerFanRequest(bool Auto, int TargetTemp, int Percent, int? MinPercent = null);
public sealed record SoakRequest(int Hours);
/// <summary>Einstellungen übertragen: Quelle, Ziele (Geräte-IDs) und Bereiche („pool“, „fan“).</summary>
public sealed record CopySettingsRequest(string Source, string[] Targets, string[] Groups);
public sealed record SoakBatchRequest(int Hours, List<string>? Ids);
public sealed record IdRequest(string Id);
public sealed record RuleRequest(string Rule);
public sealed record AutomationRequest(List<TuningPreset>? Presets, ThermalGuardRule? ThermalGuard, PresetScheduleRule? Schedule);
public sealed record DeviceRequest(string? Name, string? Host, string? WalletAddress, string? Coin, string? FirmwareRepo, bool? LogAlerts, List<string>? Groups = null, bool? LogArchive = null,
    string? MonitorOnlyMode = null, Core.Config.DevicePurchase? Purchase = null);
public sealed record TokenRequest(string? Name);
public sealed record WalletConsentRequest(bool Allow);
public sealed record MetricsRequest(bool Enabled);
/// <summary>0.9.14 Darstellung als Standard für alle Browser.</summary>
public sealed record AppearanceRequest(string? Bg, int Veil, bool Anim, bool Numbers, bool Hover, bool Party);
/// <summary>0.9.12 Pool-Konto. ApiKey: null = unverändert, leer = löschen.</summary>
public sealed record PoolAccountRequest(bool Enabled, int IntervalMinutes, string? TaxBasis, bool TaxImport, string? ApiKey, bool? AutoExchange = null,
    bool? NotifyCoinSwitch = null);
public sealed record HttpsRequest(bool Enable);
public sealed record ViewerRequest(string? Name, string? Pin, List<string>? Groups, string? Level = null);
public sealed record PresetApplyRequest(string? Name);
public sealed record OperatePauseRequest(bool Paused);
public sealed record SnapshotRequest(string File, List<string>? Fields);
public sealed record PauseRequest(bool Paused);
public sealed record FanFirmwareRequest(bool Reinstall);
public sealed record OverrideRequest(string Mode);
public sealed record KioskLoginRequest(string? Token);
public sealed record PicoSetupRequest(string? Role, string? Port, string? Ssid, string? Password, string? Host, string? DisplayId = null,
    bool UseServerWifi = false);

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
        MapMetrics(app);

        var viewer = api.MapGroup("").AddEndpointFilter(Require(Role.Viewer));
        MapViewer(viewer);
        KioskEndpoints.MapViewer(viewer);
        ExtraDisplayEndpoints.MapViewer(viewer);
        OverviewEndpoints.MapViewer(viewer);

        // 0.9.23 Bedienen ohne Admin-Rechte (Admins dürfen das natürlich auch)
        var operate = api.MapGroup("").AddEndpointFilter(Require(Role.Operator));
        MapOperator(operate);

        var admin = api.MapGroup("").AddEndpointFilter(Require(Role.Admin));
        MapDevices(admin);
        MapAdmin(admin);
    }

    /// <summary>Anzeige-Einstellungen prüfen und begrenzen (erste und weitere Anzeigen).</summary>
    /// <summary>0.9.16: hochgeladene Bilddatei lesen (Rohdaten im Body, höchstens <see cref="Core.Display.DisplayPicture.MaxUploadBytes"/>).</summary>
    public static async Task<byte[]> ReadPicture(HttpContext http)
    {
        var max = Core.Display.DisplayPicture.MaxUploadBytes;
        if (http.Request.ContentLength > max) throw new LocalizedException("Das Bild ist zu groß (höchstens {0} MB).", max / 1048576);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > max) throw new LocalizedException("Das Bild ist zu groß (höchstens {0} MB).", max / 1048576);
        }
        return ms.ToArray();
    }

    internal static void NormalizeDisplay(Core.Config.DisplaySettings req)
    {
        req.IntervalMinutes = Math.Clamp(req.IntervalMinutes, Core.Config.DisplaySettings.MinIntervalMinutes, 240);
        req.BlockFoundHoldHours = Math.Clamp(req.BlockFoundHoldHours, 1, 168);
        req.BlockFoundUntil = req.BlockFoundUntil == "button" ? "button" : "hours";
        req.SpecialUntil = req.SpecialUntil is "hours" or "button" ? req.SpecialUntil : "each";
        req.ImageFit = req.ImageFit == "fill" ? "fill" : "fit";
        req.Panel = Core.Config.DisplaySettings.Panels.Contains(req.Panel) ? req.Panel : "bwr75";   // 0.9.17
        req.MonthCompareMonths = req.MonthCompareMonths == 12 ? 12 : 6;
        // 0.9.16 Seitenfolge nach Tageszeit: höchstens 6 Fenster, Stunden 0–23, nur bekannte Seiten
        if (req.PageWindows is { } windows)
            req.PageWindows = windows.Where(w => w is not null).Take(6).Select(w => new Core.Config.DisplayPageWindow
            {
                From = Math.Clamp(w.From, 0, 23),
                To = Math.Clamp(w.To, 0, 23),
                IntervalMinutes = w.IntervalMinutes <= 0 ? 0 : Math.Clamp(w.IntervalMinutes, Core.Config.DisplaySettings.MinIntervalMinutes, 240),
                Pages = (w.Pages ?? []).Where(p => Enum.TryParse<Core.Display.DisplayScene>(p, true, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            }).ToList();
        req.ImageCaption = (req.ImageCaption ?? "").Trim() is var cap && cap.Length > 60 ? cap[..60] : cap;
        // 0.9.16: Regel je Sonderanzeige – nur bekannte Arten, gültige Werte
        if (req.SpecialRules is { } rules)
        {
            req.SpecialRules = rules.Where(kv => Core.Config.DisplaySettings.SpecialKinds.Contains(kv.Key) && kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => new Core.Config.SpecialRule
                {
                    Until = Core.Config.SpecialRule.Modes.Contains(kv.Value.Until) ? kv.Value.Until : "button",
                    Hours = Math.Clamp(kv.Value.Hours, 1, 168),
                });
        }
        req.HistoryChart = req.HistoryChart is "temp" or "power" or "efficiency" ? req.HistoryChart : "hashrate";
        req.Pages ??= new Core.Config.DisplayPages();
        req.QuietFromHour = Math.Clamp(req.QuietFromHour, 0, 23);
        req.QuietToHour = Math.Clamp(req.QuietToHour, 0, 23);
        req.Title = string.IsNullOrWhiteSpace(req.Title) ? "BitaxeTuner" : req.Title.Trim()[..Math.Min(40, req.Title.Trim().Length)];
        req.Device = req.Device == "own" ? "own" : "fans";
        req.PriceCoins = req.PriceCoins is "bch" or "both" or "mined" ? req.PriceCoins : "btc";
        req.DailyChart = req.DailyChart is "hashrate" or "power" or "efficiency" or "temp" ? req.DailyChart : "none";
        req.MonthlyChart = req.MonthlyChart is "cost" or "income" or "hashrate" ? req.MonthlyChart : "kwh";
        req.QrUrl = (req.QrUrl ?? "").Trim();
        if (req.QrUrl.Length > 0 && (req.QrUrl.Length > 200 || !Uri.TryCreate(req.QrUrl, UriKind.Absolute, out var qr) || qr.Scheme is not ("http" or "https")))
            throw new LocalizedException("QR-Code: bitte eine Adresse mit http:// oder https:// eintragen (höchstens 200 Zeichen).");
        req.Port = string.IsNullOrWhiteSpace(req.Port) ? "auto" : req.Port.Trim();
        (req.Connection, req.NetworkHost) = ValidatePicoConnection(req.Connection, req.NetworkHost);
        req.NetworkIp = System.Net.IPAddress.TryParse(req.NetworkIp ?? "", out _) ? req.NetworkIp! : "";
        req.NewsKinds = (req.NewsKinds ?? []).Where(k => Core.Network.NewsFeed.Kinds.Contains(k)).Distinct().ToList();
    }

    // ---------- Filter ----------

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(Role role) => async (ctx, next) =>
    {
        var auth = AuthContext.Of(ctx.HttpContext);
        if (auth.Role == Role.None) return Error(401, L.N("Bitte anmelden."));
        if (auth.Role < role) return Error(403, role == Role.Admin ? L.N("Dafür sind Admin-Rechte nötig.") : L.N("Dafür sind Bedien-Rechte nötig."));
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

    /// <summary>0.9.15 Update-Sperre: Kennung der eigenen Sperre (Browser) und Fehlertext beim Freigeben.</summary>
    public sealed record UpdateLockRequest(string? Id, string? Error);

    private static string UpdateClient(HttpContext http) =>
        http.Request.Headers.UserAgent.ToString().Contains("BitaxeTuner-Desktop", StringComparison.Ordinal) ? L.T("Desktop-App") : L.T("Browser");

    private static IResult UpdateBusy(UpdateRun run) =>
        Error(409, L.N("Update auf {0} läuft bereits (gestartet {1:t} über {2}, Schritt: {3}). Bitte warten, bis der Server neu gestartet ist."),
            run.Version, run.StartedUtc.ToLocalTime(), run.By, MinerHub.UpdatePhaseText(run.Phase));

    private sealed class TranslatedError(int status, string message, object?[] args) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            var loc = LangOf(http);
            var text = args.Length == 0 ? loc.T(message) : loc.T(message, args);
            return Results.Json(new { error = text }, statusCode: status).ExecuteAsync(http);
        }
    }

    /// <summary>Verbindung zu einem Pico prüfen: USB oder WLAN mit Gerätename/IP (optional :Port).</summary>
    internal static (string Connection, string Host) ValidatePicoConnection(string? connection, string? host)
    {
        var c = connection == "wlan" ? "wlan" : "usb";
        var h = (host ?? "").Trim();
        if (h.Length > 253 || h.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':')))
            throw new LocalizedException("WLAN: Gerätename oder IP-Adresse ungültig.");
        if (c == "wlan" && h.Length == 0) throw new LocalizedException("WLAN: Gerätename oder IP-Adresse des Pico eintragen.");
        return (c, h);
    }

    internal static void ValidateFans(Core.Config.FanSettings f, MinerHub hub)
    {
        f.Port = string.IsNullOrWhiteSpace(f.Port) ? "auto" : f.Port.Trim();
        (f.Connection, f.NetworkHost) = ValidatePicoConnection(f.Connection, f.NetworkHost);
        f.NetworkIp = System.Net.IPAddress.TryParse(f.NetworkIp ?? "", out _) ? f.NetworkIp! : "";
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

    /// <summary>Gerät für eine Ansicht: Miner außerhalb der Gruppen des Zugangs gelten als nicht vorhanden.</summary>
    private static HubDevice Device(MinerHub hub, string id, ViewScope scope) =>
        Dto.Find(hub, id) is { } d && scope.Allows(d) ? d : throw new KeyNotFoundException(L.N("Gerät nicht gefunden."));

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "?";

    /// <summary>0.9.23: Wer hat es ausgelöst – „Browser“ (Admin) oder der Name des Bedien-Zugangs (fürs Protokoll).</summary>
    internal static string Who(HttpContext http)
    {
        var auth = AuthContext.Of(http);
        if (auth.Role == Role.Admin) return L.T("Browser");
        var name = http.RequestServices.GetService<AuthStore>()?.ViewerName(auth.Session?.AccessId);
        return L.T("Zugang „{0}“", name ?? "?");
    }

    // ---------- Bedienen (0.9.23) ----------

    /// <summary>
    /// Bedien-Zugang: Motor pausieren, Wartungsmodus, Neustart, gespeicherte Voreinstellung anwenden (mit Vorschau alt → neu).
    /// Miner außerhalb der Gruppen des Zugangs gelten als nicht vorhanden. Keine freien Frequenz-/Spannungswerte.
    /// </summary>
    private static void MapOperator(RouteGroupBuilder g)
    {
        // Motor betrifft alle Miner – nur ohne Gruppen-Beschränkung
        g.MapPost("/operate/pause", async (OperatePauseRequest req, HttpContext http, HubService hub) =>
        {
            if (AuthContext.Of(http).Scope.Restricted) return Error(403, L.N("Den Motor dürfen nur Zugänge für alle Miner pausieren."));
            await hub.SetPausedAsync(req.Paused);
            var who = Who(http);
            await hub.RunAsync(h => { h.LogEvent(null, EventCategories.Settings, req.Paused ? L.T("Motor pausiert ({0}).", who) : L.T("Motor läuft wieder ({0}).", who)); return true; });
            return Results.Json(new { paused = req.Paused });
        });

        // 0.9.11 Wartungsmodus: Überwachung des Miners pausieren, während an ihm gearbeitet wird
        g.MapPost("/devices/{id}/maintenance", async (string id, MaintenanceRequest req, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id, AuthContext.Of(http).Scope);
            h.SetMaintenanceMode(d, req.On, req.Hours, Who(http));
            return new { ok = true, text = MinerHub.MaintenanceText(d.Config), until = d.Config.MaintenanceUntil is { } mu ? new DateTimeOffset(mu) : (DateTimeOffset?)null };
        })));

        g.MapPost("/devices/{id}/restart", async (string id, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id, AuthContext.Of(http).Scope);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException(L.N("Während eines Benchmarks nicht möglich."));
            await d.Connection.RestartAsync(); // öffnet das Wartungsfenster: keine Offline-Meldung
            d.AddLog(L.T("Neustart ausgelöst ({0}).", Who(http)), EventCategories.Connection);
            return new { ok = true };
        })));

        // Gespeicherte Voreinstellung anwenden: erst Vorschau (alter → neuer Wert), dann bestätigt ausführen
        g.MapPost("/devices/{id}/presets/preview", async (string id, PresetApplyRequest req, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id, AuthContext.Of(http).Scope);
            var p = Preset(d, req.Name);
            return await h.PreviewChangeAsync(d, p.FrequencyMhz, p.CoreVoltageMv, L.T("Voreinstellung „{0}“", p.Name));
        })));

        g.MapPost("/devices/{id}/presets/apply", async (string id, PresetApplyRequest req, HttpContext http, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id, AuthContext.Of(http).Scope);
            var p = Preset(d, req.Name);
            await h.ApplyChangeAsync(d, p.FrequencyMhz, p.CoreVoltageMv);
            d.AddLog(L.T("Voreinstellung „{0}“ angewendet ({1}).", p.Name, Who(http)), EventCategories.Automation);
            return new { ok = true };
        })));
    }

    private static TuningPreset Preset(HubDevice d, string? name)
    {
        var p = d.Config.Presets.FirstOrDefault(x => string.Equals(x.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new LocalizedException("Voreinstellung nicht gefunden.") { Status = 404 };
        if (MinerHub.CheckPreset(d, p) is { } error) throw new InvalidOperationException(error);
        return p;
    }

    // ---------- Prometheus ----------

    public const string MetricsTokenPrefix = "btm_";

    /// <summary>
    /// /metrics für Prometheus/Grafana: standardmäßig aus (404), sonst nur mit „Authorization: Bearer btm_…“. Ohne IP-
    /// und Wallet-Adressen. Fehlversuche zählen in dieselbe Sperre wie die Anmeldung.
    /// </summary>
    private static void MapMetrics(WebApplication app)
    {
        app.MapGet("/metrics", async (HttpContext http, HubService hub, Lockout lockout) =>
        {
            var (enabled, hash) = await hub.RunAsync(h => (h.Config.Metrics.Enabled, h.Config.Metrics.TokenHash));
            if (!enabled || hash.Length == 0) return Results.NotFound();
            var client = Client(http);
            var now = DateTime.UtcNow;
            if (lockout.IsLocked(client, now)) return Results.StatusCode(429);
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : "";
            if (!token.StartsWith(MetricsTokenPrefix, StringComparison.Ordinal) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Core.Transfer.Secrets.TokenHash(token)), System.Text.Encoding.ASCII.GetBytes(hash)))
            {
                lockout.Fail(client, now);
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.StatusCode(401);
            }
            lockout.Success(client);
            var text = await hub.RunAsync(h => PrometheusMetrics.Build(h, Version, Dto.DeviceId, DateTime.Now));
            return Results.Text(text, PrometheusMetrics.ContentType);
        });
    }

    // ---------- Öffentlich ----------

    private static void MapPublic(RouteGroupBuilder api)
    {
        // Audit S10: ohne Anmeldung nur das Nötigste (kein Betriebssystem, keine Geräteanzahl)
        api.MapGet("/info", (HttpContext http, AuthStore auth, HubService hub) =>
        {
            var role = AuthContext.Of(http).Role;
            return role >= Role.Viewer
                ? Results.Json(new
                {
                    name = "BitaxeTuner-Server", version = Version, apiVersion = ApiVersion, setupRequired = !auth.IsSetUp,
                    role = role.ToString(), language = Core.I18n.Loc.Current.Language,
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    paused = hub.Hub.IsPaused,
                    devices = hub.Hub.Config.Devices.Count,
                })
                : Results.Json(new
                {
                    name = "BitaxeTuner-Server", version = Version, apiVersion = ApiVersion, setupRequired = !auth.IsSetUp,
                    role = role.ToString(), language = Core.I18n.Loc.Current.Language,
                });
        });

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

        api.MapPost("/login", async (LoginRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout, HubService hub) =>
        {
            var client = Client(http);
            var now = DateTime.UtcNow;
            if (lockout.IsLocked(client, now)) return Error(429, L.N("Zu viele Fehlversuche – bitte 5 Minuten warten."));
            if (lockout.GlobalDelay(now) is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay);
            var result = await auth.LoginAsAsync(req.Password ?? "");
            if (result.Role == Role.None)
            {
                lockout.Fail(client, now);
                lockout.FailGlobal(now);
                return Error(401, L.N("Passwort oder PIN falsch."));
            }
            lockout.Success(client);
            if (result.Access is { } access)
                await hub.RunAsync(h => { h.LogEvent(null, EventCategories.Settings, L.T("Ansicht-Zugang „{0}“ angemeldet.", access.Name)); return true; });
            return StartSession(http, sessions, result.Role, ViewScope.For(result.Access), result.Access?.Id);
        });

        // Kiosk-Link (Wand-Tablet): Schlüssel → Sitzung „Nur ansehen“ (ggf. nur bestimmte Gruppen); gleiche Sperre wie /login
        api.MapPost("/kiosk/login", async (KioskLoginRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout, HubService hub) =>
        {
            var client = Client(http);
            var now = DateTime.UtcNow;
            if (lockout.IsLocked(client, now)) return Error(429, L.N("Zu viele Fehlversuche – bitte 5 Minuten warten."));
            if (lockout.GlobalDelay(now) is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay);
            if (auth.VerifyKiosk(req.Token ?? "") is not { } kiosk)
            {
                lockout.Fail(client, now);
                lockout.FailGlobal(now);
                return Error(401, L.N("Kiosk-Link ungültig oder widerrufen."));
            }
            lockout.Success(client);
            await hub.RunAsync(h => { h.LogEvent(null, EventCategories.Settings, L.T("Kiosk „{0}“ angemeldet.", kiosk.Name)); return true; });
            return StartSession(http, sessions, Role.Viewer, ViewScope.ForGroups(kiosk.Groups), kiosk.Id);
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
            return Results.Json(new { role = auth.Role.ToString(), csrf = auth.Session?.Csrf, groups = auth.Scope.Groups });
        });
    }

    private static IResult StartSession(HttpContext http, SessionStore sessions, Role role, ViewScope? scope = null, string? accessId = null)
    {
        var session = sessions.Create(role, DateTime.UtcNow, scope, accessId);
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
        // 0.9.16 „Wunsch einreichen“ ohne GitHub-Konto (Wunsch-Briefkasten → Projekt-Board), für alle Angemeldeten
        g.MapPost("/wish", async (WishRequest req, HttpContext http) =>
        {
            await Core.Network.WishBox.SendAsync(req.Idea ?? "", req.Why, Version, LangOf(http).Language, http.RequestAborted);
            return Results.Json(new { ok = true });
        });

        // Ansicht-Zugänge mit Gruppen sehen nur ihre Miner: fremde Geräte gelten als nicht vorhanden (404)
        g.MapGet("/status", async (HttpContext http, HubService hub) =>
        {
            var auth = AuthContext.Of(http);
            return Results.Json(await hub.RunAsync(h => Dto.Status(h, auth.Role, DateTime.Now, auth.Scope)));
        });

        g.MapGet("/devices/{id}", async (string id, HttpContext http, HubService hub) =>
        {
            var auth = AuthContext.Of(http);
            return Results.Json(await hub.RunAsync(h => Dto.Detail(h, Device(h, id, auth.Scope), auth.Role)));
        });

        g.MapGet("/devices/{id}/history", async (string id, string? range, HttpContext http, HubService hub) =>
        {
            var scope = AuthContext.Of(http).Scope;
            if (id == "all" && scope.Restricted) throw new KeyNotFoundException(L.N("Gerät nicht gefunden."));
            return Results.Json(await hub.RunAsync(h => Dto.History(h, id == "all" ? HistoryStore.AggregateHost : Device(h, id, scope).Host, range ?? "1h", DateTime.Now)));
        });

        g.MapGet("/plugs/{id}/history", async (string id, string? range, HttpContext http, HubService hub) =>
        {
            var scope = AuthContext.Of(http).Scope;
            return Results.Json(await hub.RunAsync(h =>
            {
                if (h.Config.Plugs.Items.FirstOrDefault(p => p.Id == id) is { } plug && !Dto.PlugVisible(h, scope, plug.Role, plug.Miners))
                    throw new LocalizedException("Smart Plug nicht gefunden.") { Status = 404 };
                return Dto.PlugHistory(h, id, range ?? "24h", DateTime.Now);
            }));
        });

        // Gesundheits-Frühwarnung: letzte 7 Tage gegen die 4 Wochen davor
        g.MapGet("/devices/{id}/health", async (string id, HttpContext http, HubService hub) =>
        {
            var scope = AuthContext.Of(http).Scope;
            return Results.Json(await hub.RunAsync(h =>
            {
                var d = Device(h, id, scope);
                var (findings, recent, @base) = h.HealthOf(d.Host, DateTime.Now);
                return new
                {
                    findings = findings.Select(f => new { f.Code, f.Title, f.Text }).ToList(), recent, @base,
                    // 0.9.21 Amortisation (Ertragsanteil − Strom seit Kauf) – Erträge nur für Admin
                    amortization = AuthContext.Of(http).Role == Role.Admin && !scope.Restricted && h.AmortizationOf(d, DateTime.Now) is { } am
                        ? new { am.Price, am.PurchaseDate, am.TrackedFrom, am.Income, am.Cost, am.Net, am.Remaining, am.NetPerDay, am.Eta, am.Paid, am.Partial, am.Source, am.Currency, am.WarrantyUntil, progress = am.Progress }
                        : null,
                    // 0.9.16 Wartungs-Erinnerungen
                    upkeep = new
                    {
                        hours = d.Config.Upkeep?.Hours,
                        settings = h.Config.Upkeep,
                        tasks = h.UpkeepOf(d).Select(t => new { key = t.Key, label = t.Label, intervalDays = t.IntervalDays, dueInDays = Math.Round(t.DueInDays, 1), due = t.Due, lastDone = t.LastDone }).ToList(),
                    },
                };
            }));
        });

        g.MapGet("/devices/{id}/comparisons", async (string id, HttpContext http, HubService hub) =>
        {
            var scope = AuthContext.Of(http).Scope;
            return Results.Json(await hub.RunAsync(h => h.Comparisons(Device(h, id, scope)).Select(Dto.Comparison).ToList()));
        });

        g.MapGet("/events", (HttpContext http, EventStream events) => events.ServeAsync(http, AuthContext.Of(http).Role, AuthContext.Of(http).Scope));

        // Vergleichsbericht zum Ausdrucken – ohne IP- und Wallet-Adressen, daher auch für „Nur ansehen“
        g.MapGet("/compare/report", async (string ids, string? range, string? values, string? charts, HttpContext http, HubService hub) =>
        {
            static string[] Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var scope = AuthContext.Of(http).Scope;
            var html = await hub.RunAsync(h =>
            {
                var devices = Split(ids).Distinct().Select(id => Device(h, id, scope)).ToList();
                var report = Core.Reports.CompareReports.Build(h, devices, range ?? "24h",
                    values is null ? Core.Reports.CompareReports.DefaultValues : Split(values),
                    charts is null ? Core.Reports.CompareReports.DefaultCharts : Split(charts), DateTime.Now);
                return Core.Reports.CompareReports.Html(report);
            });
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // E-Paper zeigt Summen über alle Miner – nicht für Ansicht-Zugänge mit Gruppen
        g.MapGet("/display", async (HttpContext http, HubService hub) => AuthContext.Of(http).Scope.Restricted ? Results.Json<object?>(null) : Results.Json(await hub.RunAsync(h => new
        {
            status = h.DisplayStatus,
            device = AuthContext.Of(http).Role == Role.Admin ? h.DisplayDeviceDescription : null,
            settings = AuthContext.Of(http).Role == Role.Admin ? Dto.Copy(h.Config.Display) : null,
            hasImage = h.HasDisplayPicture(null),
            rebootAvailable = h.Options.SystemReboot is not null,
        })));

        // 0.9.11 Neuigkeiten aus der Solo-Mining-Welt (öffentliche news.json, ohne Nutzerdaten) – Sprache des Browsers
        g.MapGet("/news", async (string? lang, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            _ = h.RefreshNewsAsync(force: false);   // höchstens alle 3 h, Antwort kommt aus dem Zwischenspeicher
            var language = lang is not null && Loc.Languages.Contains(lang) ? lang : null;
            return new
            {
                updated = h.News.UpdatedUtc,
                error = h.News.LastError,
                kinds = h.Config.Display.NewsKinds,
                items = h.News.Items(null, 30, language).Select(i => new { i.Id, date = i.DateUtc, i.Kind, i.Coin, i.Title, i.Text, i.Url }),
            };
        })));

        // Vorschau genau so, wie die Anzeige es zeigt (auch ohne Hardware)
        // Vorschau: ohne scene genau das, was als Nächstes käme; mit scene=Daily|Chart|… eine bestimmte Seite
        g.MapGet("/display/preview.png", async (string? scene, HttpContext http, HubService hub) =>
        {
            if (AuthContext.Of(http).Scope.Restricted) return Error(403, L.N("Für diesen Ansicht-Zugang nicht freigegeben."));
            var model = await hub.RunAsync(h => Enum.TryParse<Core.Display.DisplayScene>(scene, true, out var sc)
                ? h.PreviewScene(sc, DateTime.Now)
                : h.ComposeDisplay(DateTime.Now));
            var panel = await hub.RunAsync(h => h.Config.Display.Panel);   // 0.9.17: Spectra 6 in Farbe
            using var img = Core.Display.StatusRenderer.RenderImage(model, panel);
            var ms = new MemoryStream();
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(img, ms);
            return Results.File(ms.ToArray(), "image/png");
        });

        g.MapGet("/fans", async (HttpContext http, HubService hub) =>
        {
            var auth = AuthContext.Of(http);
            return Results.Json(await hub.RunAsync(h => new
            {
                status = Dto.Fans(h, auth.Role, auth.Scope),
                enabled = h.Config.Fans.Enabled,
                settings = auth.Role == Role.Admin ? Dto.Copy(h.Config.Fans) : null,
                miners = h.Devices.Where(auth.Scope.Allows).Select(d => new { id = Dto.DeviceId(d.Host), name = d.Title, host = auth.Role == Role.Admin ? d.Host : null }).ToList(),
            }));
        });
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
            if (req.LogArchive is { } lk) c.LogArchive = lk;
            if (req.Groups is { } groups) c.Groups = MinerGroups.Normalize(groups);
            // 0.9.17: „auto“ (nach Firmware), „on“ (nur überwachen), „off“ (Tuning erlauben)
            if (req.MonitorOnlyMode is "auto" or "on" or "off")
                c.MonitorOnly = req.MonitorOnlyMode switch { "on" => true, "off" => false, _ => null };
            // 0.9.21: Kaufpreis/-datum/Garantie (alle leer = löschen)
            if (req.Purchase is { } purchase) h.SetPurchase(Device(h, id), purchase);
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


        // Benchmark
        // Lüfter des Miners (AxeOS/NerdQAxe): Automatik mit Zieltemperatur oder fester Wert – Bestätigung im Browser
        g.MapPost("/devices/{id}/fan", async (string id, MinerFanRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.SetMinerFanAsync(Device(h, id), req.Auto, req.TargetTemp, req.Percent, L.T("Browser"), req.MinPercent);
            return new { ok = true };
        })));

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

        // Ein Benchmark-Ergebnis als Voreinstellung speichern (gleicher Name = ersetzen). Am Miner ändert sich dabei nichts.
        g.MapPost("/devices/{id}/presets", async (string id, TuningPreset req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) throw new LocalizedException("Bitte einen Namen angeben.");
            if (name.Length > 40) name = name[..40];
            var preset = new TuningPreset(name, req.FrequencyMhz, req.CoreVoltageMv);
            if (MinerHub.CheckPreset(d, preset) is { } error) throw new InvalidOperationException(error);
            var c = d.Config;
            var i = c.Presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var old = i >= 0 ? c.Presets[i] : null;
            if (old is not null) c.Presets[i] = preset; else c.Presets.Add(preset);
            h.Config.Save();
            d.AddLog(old is null ? L.T("Voreinstellung gespeichert: {0}", preset) : L.T("Voreinstellung ersetzt: {0} → {1}", old, preset), EventCategories.Automation);
            return new { ok = true, replaced = old is not null, presets = c.Presets.ToList() };
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

        // 0.9.11: gespeicherte Miner-Logs (nur Admin – Logs können Pool-Benutzer/Wallet enthalten)
        g.MapGet("/devices/{id}/minerlog/stored", async (string id, int? hours, string? levels, string? q, int? limit, HubService hub) =>
            Results.Json(await hub.RunAsync(h =>
            {
                var d = Device(h, id);
                var now = DateTime.Now;
                var span = Math.Clamp(hours ?? h.Config.MinerLogKeepHours, 1, 168);
                h.MinerLogs.Flush();   // eben Gesammeltes gleich mit anzeigen
                var lines = h.History?.QueryMinerLog(d.Host, now.AddHours(-span), now, levels, q, Math.Clamp(limit ?? 5000, 1, 20000)) ?? [];
                return new
                {
                    enabled = d.Config.LogArchive,
                    keepHours = h.Config.MinerLogKeepHours,
                    total = h.History?.CountMinerLog(d.Host) ?? 0,
                    lines = lines.Select(l => new { time = Dto.Unix(l.Time), level = l.Level, tag = l.Tag, message = l.Message,
                        category = LogCategories.Of(l.Tag, l.Message).ToString() }).ToList(),
                };
            })));

        g.MapGet("/devices/{id}/minerlog/stored.txt", async (string id, int? hours, HubService hub) =>
        {
            var (name, text) = await hub.RunAsync(h =>
            {
                var d = Device(h, id);
                var now = DateTime.Now;
                h.MinerLogs.Flush();
                var lines = h.History?.QueryMinerLog(d.Host, now.AddHours(-Math.Clamp(hours ?? h.Config.MinerLogKeepHours, 1, 168)), now, limit: 50000) ?? [];
                var sb = new System.Text.StringBuilder();
                foreach (var l in lines)
                    sb.Append(l.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                      .Append(l.Level).Append(' ').Append(l.Tag.Length > 0 ? l.Tag + ": " : "").Append(l.Message).Append('\n');
                return (d.Title, sb.ToString());
            });
            var file = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + $"-log-{DateTime.Now:yyyyMMdd-HHmm}.txt";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", file);
        });

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
        ExtraPoolEndpoints.Map(g);   // 0.9.20 weitere Pool-Konten
        ChatBotEndpoints.Map(g);     // 0.9.24 Chat-Bots
        PiFanEndpoints.Map(g);   // 0.9.20 Lüfter des Servers (Pi 5)
        SolarEndpoints.Map(g);   // 0.9.17 PV-Quelle
        ReportEndpoints.Map(g);
        TaxEndpoints.Map(g);
        KioskEndpoints.Map(g);
        GroupEndpoints.Map(g);
        PoolEndpoints.Map(g);
        ProfileEndpoints.Map(g);
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

        g.MapPost("/wallet-consent", async (WalletConsentRequest req, HubService hub) =>
            Results.Json(await hub.RunAsync(async h => { await h.SetWalletConsentAsync(req.Allow); return new { ok = true }; })));

        // Verbindung HTTP/HTTPS (Audit S4). Umstellen speichert die Wahl und startet den Dienst neu; der Fingerabdruck des
        // selbst signierten Zertifikats geht an die (angemeldete) App, die ihn damit ohne Rückfrage festhalten kann.
        g.MapGet("/admin/https", (ServerSettings settings) => Results.Json(new
        {
            enabled = settings.Https,
            configurable = !settings.HttpsFixed,
            fingerprint = settings.Https ? Certificates.Fingerprint(Certificates.LoadOrCreate(settings.DataDirectory)) : null,
            port = settings.Port,
        }));

        // Öffentliches Zertifikat (PEM, ohne Schlüssel) – z. B. als ca_file für Prometheus statt insecure_skip_verify (Audit N-Sec4)
        g.MapGet("/admin/https/certificate", (ServerSettings settings) =>
        {
            if (!settings.Https) return Error(404, L.N("HTTPS ist nicht eingeschaltet."));
            using var cert = Certificates.LoadOrCreate(settings.DataDirectory);
            return Results.File(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem() + "\n"), "application/x-pem-file", "bitaxetuner-server.crt");
        });

        g.MapPost("/admin/https", async (HttpsRequest req, ServerSettings settings, ServerRestart restart, HubService hub) =>
        {
            if (settings.HttpsFixed)
                return Error(400, L.N("HTTPS ist beim Start fest vorgegeben (--https bzw. BITAXETUNER_HTTPS) und lässt sich hier nicht umstellen."));
            if (req.Enable == settings.Https) return Results.Json(new { ok = true, restarting = false, fingerprint = (string?)null });
            var fingerprint = req.Enable ? Certificates.Fingerprint(Certificates.LoadOrCreate(settings.DataDirectory)) : null;
            ServerSettings.WriteStoredHttps(settings.DataDirectory, req.Enable);
            await hub.RunAsync(h =>
            {
                h.LogEvent(null, EventCategories.Settings, req.Enable
                    ? L.T("Verbindung auf HTTPS umgestellt – der Server startet neu.")
                    : L.T("Verbindung auf HTTP (unverschlüsselt) umgestellt – der Server startet neu."));
                return true;
            });
            restart.Schedule(req.Enable ? "HTTPS eingeschaltet" : "HTTPS ausgeschaltet");
            return Results.Json(new { ok = true, restarting = true, fingerprint, port = settings.Port });
        });

        // 0.9.14: Darstellung als Standard für alle Browser und die Desktop-App
        g.MapPut("/appearance", async (AppearanceRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var a = h.Config.Appearance;
            a.Background = req.Bg ?? a.Background;
            a.Veil = req.Veil;
            a.Animations = req.Anim;
            a.SmoothNumbers = req.Numbers;
            a.HoverLift = req.Hover;
            a.BlockParty = req.Party;
            a.Normalize();
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Darstellung als Standard für alle Browser gespeichert."));
            return new { ok = true };
        })));

        // Prometheus-Export: ein-/ausschalten, Token erzeugen (wird nur einmal angezeigt)
        g.MapGet("/metrics/settings", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            enabled = h.Config.Metrics.Enabled,
            tokenSet = h.Config.Metrics.TokenHash.Length > 0,
            tokenCreatedUtc = h.Config.Metrics.TokenCreatedUtc,
        })));

        g.MapPut("/metrics/settings", async (MetricsRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            if (h.Config.Metrics.Enabled != req.Enabled)
            {
                h.Config.Metrics.Enabled = req.Enabled;
                h.Config.Save();
                h.LogEvent(null, EventCategories.Settings, req.Enabled ? L.T("Prometheus-Export eingeschaltet.") : L.T("Prometheus-Export ausgeschaltet."));
            }
            return new { ok = true };
        })));

        g.MapPost("/metrics/token", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var token = MetricsTokenPrefix + AuthStore.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            h.Config.Metrics.TokenHash = Core.Transfer.Secrets.TokenHash(token);
            h.Config.Metrics.TokenCreatedUtc = DateTime.UtcNow;
            h.Config.Save();
            h.LogEvent(null, EventCategories.Settings, L.T("Neues Prometheus-Token erzeugt (das alte gilt nicht mehr)."));
            return new { token };
        })));

        // 0.9.12 Pool-Konto (Mining-Dutch): Stand, Einstellungen, sofort abfragen. Der Schlüssel wird nie zurückgegeben.
        g.MapGet("/pool-account", async (HubService hub) => Results.Json(await hub.RunAsync(Dto.PoolAccount)));

        g.MapPut("/pool-account", async (PoolAccountRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var p = h.Config.PoolAccount;
            var basis = string.Equals(req.TaxBasis, "payout", StringComparison.OrdinalIgnoreCase) ? Core.Pools.PoolIncomeBasis.Payout : Core.Pools.PoolIncomeBasis.Credit;
            var changes = new List<string>();
            if (p.Enabled != req.Enabled) changes.Add(req.Enabled ? L.T("Pool-Konto eingeschaltet.") : L.T("Pool-Konto ausgeschaltet."));
            if (req.ApiKey is { } key && key.Trim() != p.ApiKey)
                changes.Add(key.Trim().Length > 0 ? L.T("API-Schlüssel des Pool-Kontos geändert.") : L.T("API-Schlüssel des Pool-Kontos gelöscht."));
            if (p.TaxBasis != basis)
                changes.Add(basis == Core.Pools.PoolIncomeBasis.Payout ? L.T("Pool-Zuflüsse für die Steuer: je Auszahlung.") : L.T("Pool-Zuflüsse für die Steuer: Gutschriften je Tag."));
            if (p.TaxImport != req.TaxImport)
                changes.Add(req.TaxImport ? L.T("Pool-Buchungen werden in die Steuer übernommen.") : L.T("Pool-Buchungen werden nicht mehr in die Steuer übernommen."));
            p.Enabled = req.Enabled;
            p.IntervalMinutes = req.IntervalMinutes;
            p.TaxBasis = basis;
            p.TaxImport = req.TaxImport;
            if (req.NotifyCoinSwitch is { } ncs) p.NotifyCoinSwitch = ncs;
            if (req.AutoExchange is { } ax && ax != p.AutoExchange)
            {
                changes.Add(ax ? L.T("Pool-Konto: Altcoins werden automatisch in BTC umgetauscht.") : L.T("Pool-Konto: kein automatischer Umtausch in BTC."));
                p.AutoExchange = ax;
            }
            if (req.ApiKey is not null) p.ApiKey = req.ApiKey;
            p.Normalize();
            h.Config.Save();
            foreach (var c in changes) h.LogEvent(null, EventCategories.Settings, c);
            await h.ApplyPoolAccountSettingsAsync();
            return Dto.PoolAccount(h);
        })));

        g.MapPost("/pool-account/refresh", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var p = h.Config.PoolAccount;
            if (!p.Enabled || p.ApiKey.Length == 0) throw new LocalizedException("Pool-Konto einschalten und API-Schlüssel eintragen.");
            _ = h.PoolAccountTickAsync();   // dauert wegen der Pausen zwischen den Anfragen gut eine Minute
            return Dto.PoolAccount(h);
        })));

        g.MapGet("/tokens", (AuthStore auth) => Results.Json(auth.Tokens.Select(t => new { t.Id, t.Name, t.CreatedUtc, t.LastUsedUtc })));

        g.MapPost("/tokens", (TokenRequest req, AuthStore auth) =>
        {
            var (entry, secret) = auth.CreateToken(req.Name ?? "Desktop");
            return Results.Json(new { entry.Id, entry.Name, token = secret, note = "Das Token wird nur jetzt angezeigt." });
        });

        g.MapDelete("/tokens/{tokenId}", (string tokenId, AuthStore auth) =>
            auth.RevokeToken(tokenId) ? Results.Ok(new { ok = true }) : Error(404, L.N("Token nicht gefunden.")));

        // Eigene Ansicht-Zugänge (PIN je Person, optional auf Gruppen beschränkt); die PIN wird nie zurückgegeben
        g.MapGet("/viewers", (AuthStore auth) =>
            Results.Json(auth.Viewers.Select(v => new { v.Id, v.Name, v.Groups, v.CreatedUtc, v.LastUsedUtc, v.Level })));

        g.MapPost("/viewers", async (ViewerRequest req, AuthStore auth, HubService hub) =>
        {
            var v = auth.CreateViewer(req.Name ?? "", req.Pin ?? "", req.Groups, req.Level);
            await hub.RunAsync(h =>
            {
                h.LogEvent(null, EventCategories.Settings, v.Groups.Count == 0
                    ? L.T("Ansicht-Zugang „{0}“ angelegt (alle Miner).", v.Name)
                    : L.T("Ansicht-Zugang „{0}“ angelegt (Gruppen: {1}).", v.Name, string.Join(", ", v.Groups)));
                if (v.Level == ViewerAccess.LevelOperate)
                    h.LogEvent(null, EventCategories.Settings, L.T("Zugang „{0}“ darf bedienen (Pause, Voreinstellung, Neustart, Wartung).", v.Name));
                return true;
            });
            return Results.Json(new { v.Id, v.Name, v.Groups, v.CreatedUtc, v.Level });
        });

        // Kiosk-Links (Wand-Tablet): der Schlüssel wird nur beim Anlegen einmal zurückgegeben
        g.MapGet("/kiosks", (AuthStore auth) =>
            Results.Json(auth.Kiosks.Select(k => new { k.Id, k.Name, k.Groups, k.CreatedUtc, k.LastUsedUtc, k.DesignId })));

        g.MapPost("/kiosks", async (ViewerRequest req, AuthStore auth, HubService hub) =>
        {
            var (k, secret) = auth.CreateKiosk(req.Name ?? "", req.Groups);
            await hub.RunAsync(h =>
            {
                h.LogEvent(null, EventCategories.Settings, k.Groups.Count == 0
                    ? L.T("Kiosk-Link „{0}“ angelegt (alle Miner).", k.Name)
                    : L.T("Kiosk-Link „{0}“ angelegt (Gruppen: {1}).", k.Name, string.Join(", ", k.Groups)));
                return true;
            });
            return Results.Json(new { k.Id, k.Name, k.Groups, k.CreatedUtc, token = secret });
        });

        g.MapDelete("/kiosks/{kioskId}", async (string kioskId, AuthStore auth, SessionStore sessions, HubService hub) =>
        {
            if (auth.RevokeKiosk(kioskId) is not { } k) return Error(404, L.N("Kiosk-Link nicht gefunden."));
            var ended = sessions.RemoveAccess(k.Id);
            await hub.RunAsync(h => { h.LogEvent(null, EventCategories.Settings, L.T("Kiosk-Link „{0}“ widerrufen.", k.Name)); return true; });
            return Results.Ok(new { ok = true, sessions = ended });
        });

        g.MapDelete("/viewers/{viewerId}", async (string viewerId, AuthStore auth, SessionStore sessions, HubService hub) =>
        {
            if (auth.RevokeViewer(viewerId) is not { } v) return Error(404, L.N("Zugang nicht gefunden."));
            var ended = sessions.RemoveAccess(v.Id);
            await hub.RunAsync(h => { h.LogEvent(null, EventCategories.Settings, L.T("Ansicht-Zugang „{0}“ widerrufen.", v.Name)); return true; });
            return Results.Ok(new { ok = true, sessions = ended });
        });

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
            NormalizeDisplay(req);
            h.Config.Display = req;
            h.Config.Save();
            h.RequestDisplayRefresh();
            await h.ApplyFanSettingsAsync();
            return new { ok = true, status = h.DisplayStatus };
        })));

        ExtraDisplayEndpoints.MapAdmin(g);
        PicoEndpoints.MapAdmin(g);   // 0.9.23
        OverviewEndpoints.MapAdmin(g);
        PublicHttpsEndpoints.Map(g);

        // 0.9.16: eigenes Bild für die Seite „Eigenes Bild“ (Datei als Rohdaten im Body)
        g.MapPost("/display/image", async (HttpContext http, HubService hub) =>
        {
            // 0.9.19 (Issue #21): prüfen und verkleinern außerhalb des Hub-Ablaufs (mit Speichergrenze)
            var png = await Task.Run(async () => Core.Display.DisplayPicture.Normalize(await ReadPicture(http)));
            return Results.Json(await hub.RunAsync(h =>
            {
                h.SaveDisplayPicture(null, png, L.T("Browser"));
                h.RequestDisplayRefresh();
                return new { ok = true };
            }));
        });
        g.MapDelete("/display/image", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.DeleteDisplayPicture(null, L.T("Browser"));
            h.RequestDisplayRefresh();
            return new { ok = true };
        })));

        // 0.9.16 weitere Kurse („ETH“ oder „XYZ=coingecko-id“)
        g.MapGet("/coins/watch", async (HubService hub) => Results.Json(await hub.RunAsync(h => new { coins = h.Config.WatchCoins, hidden = h.Config.HiddenCoins })));
        // 0.9.21: Kurse ausblenden / wieder anzeigen
        g.MapPut("/coins/hidden", async (WatchCoinsRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SetHiddenCoins(req.Coins ?? [], L.T("Browser"));
            return new { ok = true, hidden = h.Config.HiddenCoins };
        })));
        g.MapPut("/coins/watch", async (WatchCoinsRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SetWatchCoins(req.Coins ?? [], L.T("Browser"));
            return new { ok = true, coins = h.Config.WatchCoins };
        })));

        // 0.9.16 Wartungs-Erinnerungen: Aufgabe erledigt, Abstände einstellen
        g.MapPost("/devices/{id}/upkeep/{task}/done", async (string id, string task, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.UpkeepDone(Device(h, id), task, L.T("Browser"));
            return new { ok = true };
        })));
        g.MapPut("/upkeep", async (Core.Config.UpkeepSettings req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SetUpkeepSettings(req, L.T("Browser"));
            return new { ok = true, settings = h.Config.Upkeep };
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

        // 0.9.12 Push im Browser: öffentlicher VAPID-Schlüssel und Anmeldung dieses Geräts (wird ein Push-Ziel „webpush“)
        g.MapGet("/webpush/key", async (HubService hub) => Results.Json(await hub.RunAsync(h => new { key = h.WebPushPublicKey() })));
        g.MapPost("/webpush/subscribe", async (WebPushRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var t = h.AddWebPushTarget(req.Endpoint, req.Keys?.P256dh, req.Keys?.Auth, req.Name);
            return new { ok = true, id = t.Id, name = t.Title };
        })));

        // 0.9.12: Pico am USB als Lüfter- oder Display-Pico festlegen – ohne WLAN, ohne Thonny
        g.MapPost("/fans/pico-role", async (PicoSetupRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var role = req.Role == "display" ? Core.Fans.PicoFanDevice.RoleDisplay : Core.Fans.PicoFanDevice.RoleFans;
            var (r, port) = await h.SetPicoRoleAsync(role, req.Port);
            await h.ApplyFanSettingsAsync();
            return new { ok = true, role = r, port };
        })));

        // 0.9.17: WLAN des Servers für die Pico-Einrichtung – nur der Name geht an den Browser, nie das Passwort
        g.MapGet("/fans/server-wifi", async (HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            if (!h.ServerWifiSupported) return new { supported = false, ssid = (string?)null, error = (string?)null };
            try { return new { supported = true, ssid = (string?)(await h.ReadServerWifiAsync()).Ssid, error = (string?)null }; }
            catch (LocalizedException ex) { return new { supported = true, ssid = (string?)null, error = (string?)ex.Message }; }
        })));

        // Pico am USB für WLAN einrichten (Lüfter- oder Display-Pico). Das WLAN-Passwort geht nur auf den Pico.
        g.MapPost("/fans/wlan-setup", async (PicoSetupRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var role = req.Role == "display" ? Core.Fans.PicoFanDevice.RoleDisplay : Core.Fans.PicoFanDevice.RoleFans;
            // 0.9.11: Display-Pico einer weiteren Anzeige – eigener Gerätename, damit sich mehrere Picos im WLAN nicht stören
            var extraId = role == Core.Fans.PicoFanDevice.RoleDisplay && req.DisplayId is { Length: > 0 } id ? id : null;
            var host = extraId is not null && string.IsNullOrWhiteSpace(req.Host) ? $"bitaxetuner-display-{extraId}" : req.Host;
            var result = await h.SetupPicoNetworkAsync(role, req.Port, req.Ssid, req.Password, host, extraId, req.UseServerWifi);
            if (extraId is null && role == Core.Fans.PicoFanDevice.RoleDisplay) h.Config.Display.Enabled = true;
            h.Config.Save();
            await h.ApplyFanSettingsAsync();
            return new { ok = true, result.Role, result.Host, result.Ip, result.Port };
        })));

        // ---------- Betrieb mit der Desktop-App ----------

        // Server-Update
        g.MapGet("/admin/update", (ServerUpdater updater, HubService hub) => Results.Json(new
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
            // 0.9.15: laufendes Update (Sperre für alle Oberflächen)
            running = hub.Hub.RunningUpdate is { } run
                ? new { version = run.Version, by = run.By, started = run.StartedUtc, phase = run.Phase, phaseText = MinerHub.UpdatePhaseText(run.Phase) }
                : null,
            // 0.9.16 (Issue #11): warum das letzte Update abgebrochen wurde
            lastError = hub.Hub.LastUpdateError is { } le ? new { version = le.Version, error = le.Error, time = le.AtUtc } : null,
        }));

        g.MapPost("/admin/update/check", async (ServerUpdater updater) =>
        {
            var r = await updater.CheckAsync(notify: false);
            return Results.Json(new { available = r.Status == Core.Update.UpdateCheckStatus.UpdateAvailable, latest = r.Update?.Tag, message = r.Message });
        });

        // 0.9.15: Sperre holen, bevor der Browser die (lange) Sicherung startet – andere Geräte sehen dann „läuft bereits“
        g.MapPost("/admin/update/begin", async (ServerUpdater updater, HttpContext http, HubService hub) =>
        {
            if (updater.Latest is null) return Error(400, L.N("Kein Update verfügbar – zuerst nach Updates suchen."));
            var tag = updater.Latest.Tag;
            var by = UpdateClient(http);
            var (started, run) = await hub.RunAsync(h => (h.TryBeginUpdate(tag, by, null, out var r), r));
            return started ? Results.Json(new { id = run.Id }) : UpdateBusy(run);
        });

        g.MapPost("/admin/update/end", async (UpdateLockRequest req, HubService hub) =>
        {
            await hub.RunAsync(h => { h.EndUpdate(req.Error, req.Id); return 0; });
            return Results.Json(new { ok = true });
        });

        g.MapPost("/admin/update/install", async (UpdateLockRequest? req, ServerUpdater updater, ILogger<ServerUpdater> log, HttpContext http, HubService hub) =>
        {
            if (updater.Latest is null) return Error(400, L.N("Kein Update verfügbar – zuerst nach Updates suchen."));
            if (!updater.CanInstall) return Error(400, L.N("Diese Installation aktualisiert sich nicht selbst. Docker: „docker compose pull && docker compose up -d“."));
            // 0.9.15: nur ein Update gleichzeitig – egal ob aus Browser, Desktop-App oder einem zweiten Gerät
            var tag = updater.Latest.Tag;
            var by = UpdateClient(http);
            var (started, running) = await hub.RunAsync(h => (h.TryBeginUpdate(tag, by, req?.Id, out var r), r));
            if (!started) return UpdateBusy(running);
            // 0.9.16 (Issue #11): Sicherung und Installation laufen ganz auf dem Server – die Antwort kommt sofort. Bisher wartete
            // die Anfrage die ganze (auf dem Pi minutenlange) Sicherung ab; schlief das Handy oder riss die Verbindung ab, ging
            // es nach der erfolgreichen Sicherung nicht weiter. Fortschritt und Abbruchgrund zeigt GET /admin/update.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Erst die Sicherung (Datenordner + USB/NAS; eine der letzten 15 min wird verwendet) – schlägt sie fehl,
                    // wird nichts installiert
                    await hub.RunAsync(h => h.BackupBeforeUpdateAsync(tag));
                    await updater.InstallAsync();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Update fehlgeschlagen");
                    await hub.RunAsync(h => { h.EndUpdate(ex.Message); return 0; });
                }
            });
            return Results.Json(new { ok = true, message = LangOf(http).T("Sicherung und Update laufen jetzt auf dem Server, danach startet er neu. Die Seite darf dabei geschlossen werden.") });
        });

        g.MapPost("/admin/pause", async (PauseRequest req, HubService hub) =>
        {
            await hub.SetPausedAsync(req.Paused);
            return Results.Json(new { paused = req.Paused });
        });

        g.MapGet("/admin/export", async (HubService hub) =>
        {
            // Audit N-Sec5: im geschützten Datenordner statt im allgemeinen Temp-Ordner (Archiv enthält aufgelöste Tokens)
            var file = Path.Combine(hub.Settings.DataDirectory, $".export-{Guid.NewGuid():N}.zip");
            // 0.9.14: Archiv nicht im Hub-Kontext bauen – das dauert auf einem Pi mit großer history.db Minuten, und so lange
            // stünden Abfragen, Lüfterbefehle und die Oberfläche still (wirkt wie ein Absturz)
            var (dir, history) = await hub.RunAsync(h => (h.DataDirectory, h.History));
            try
            {
                await Task.Run(() =>
                {
                    using var fs = File.Create(file);
                    DataArchive.Create(dir, history, fs, "server", Version);
                });
            }
            catch
            {
                try { File.Delete(file); } catch { /* egal */ }
                throw;
            }
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
            // Audit E4: vorab prüfen, ob Upload, Entpacken und die Sicherung des bisherigen Stands auf den Datenträger passen
            if (http.Request.ContentLength is { } length) DiskSpace.Require(dir, length);
            var upload = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            var staging = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40]);
            try
            {
                await using (var fs = File.Create(upload)) await DiskSpace.CopyWithSpaceCheckAsync(http.Request.Body, fs, dir, http.RequestAborted);
                ArchiveManifest manifest;
                try { DiskSpace.Require(dir, DiskSpace.UncompressedSize(upload) + DiskSpace.FolderSize(dir) - new FileInfo(upload).Length); }
                catch (InvalidDataException ex) { return Error(400, L.N("Archiv abgelehnt: {0}"), ex.Message); }
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
            // Erklärung je Eintrag als Verweis; jede Erklärung nur einmal (Antwort bleibt klein)
            var explanations = new Dictionary<string, EventExplanation>();
            var rows = entries.Select(e =>
            {
                var x = EventExplanations.For(e);
                explanations.TryAdd(x.Id, x);
                return new
                {
                    time = e.Time, device = e.Host is not null && names.TryGetValue(e.Host, out var n) ? n.Id : null,
                    name = Name(e.Host), category = e.Category, message = e.Message, explain = x.Id,
                };
            }).ToList();
            return Results.Json(new
            {
                entries = rows,
                explanations = explanations.ToDictionary(kv => kv.Key, kv => new { title = kv.Value.Title, meaning = kv.Value.Meaning, action = kv.Value.Action }),
                truncated = entries.Count >= 5000,
            });
        });
    }
}

/// <summary>0.9.11: Wartungsmodus an/aus; Hours &gt; 0 = endet danach von selbst.</summary>
public sealed record MaintenanceRequest(bool On, double? Hours);

/// <summary>0.9.12: Push-Anmeldung eines Browsers (PushSubscription.toJSON()).</summary>
public sealed record WebPushKeys(string? P256dh, string? Auth);
public sealed record WebPushRequest(string? Endpoint, WebPushKeys? Keys, string? Name);

/// <summary>0.9.16: weitere Kurse.</summary>
public sealed record WatchCoinsRequest(List<string>? Coins);

/// <summary>0.9.16: Wunsch aus der App.</summary>
public sealed record WishRequest(string? Idea, string? Why);
