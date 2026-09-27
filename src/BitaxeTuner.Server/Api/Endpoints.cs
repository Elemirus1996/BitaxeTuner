using System.Reflection;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Benchmark;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Server.Api;

public sealed record LoginRequest(string Password);
public sealed record SetupRequest(string Code, string Password);
public sealed record PasswordRequest(string Current, string Password);
public sealed record ChangeRequest(int Frequency, int Voltage);
public sealed record BenchmarkRequest(BenchmarkSettings? Settings, bool Resume);
public sealed record SoakRequest(int Hours);
public sealed record IdRequest(string Id);
public sealed record RuleRequest(string Rule);
public sealed record AutomationRequest(List<TuningPreset>? Presets, ThermalGuardRule? ThermalGuard, PresetScheduleRule? Schedule);
public sealed record DeviceRequest(string? Name, string? Host, string? WalletAddress, string? Coin, string? FirmwareRepo, bool? LogAlerts);
public sealed record TokenRequest(string? Name);
public sealed record SnapshotRequest(string File, List<string>? Fields);
public sealed record PauseRequest(bool Paused);

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
        if (auth.Role == Role.None) return Error(401, "Bitte anmelden.");
        if (auth.Role < role) return Error(403, "Dafür sind Admin-Rechte nötig.");
        if (!auth.CsrfOk(ctx.HttpContext.Request)) return Error(403, "CSRF-Prüfung fehlgeschlagen – Seite neu laden.");
        return await next(ctx);
    };

    private static async ValueTask<object?> HandleErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (InvalidOperationException ex) { return Error(400, ex.Message); }
        catch (MinerApiException ex) { return Error(502, ex.Message); }
        catch (KeyNotFoundException ex) { return Error(404, ex.Message); }
    }

    public static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static HubDevice Device(MinerHub hub, string id) =>
        Dto.Find(hub, id) ?? throw new KeyNotFoundException("Gerät nicht gefunden.");

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
        }));

        // Desktop-App: aus dem API-Token eine Browser-Sitzung für die eingebettete Oberfläche machen
        api.MapPost("/token-session", (HttpContext http, SessionStore sessions) =>
        {
            var auth = AuthContext.Of(http);
            if (!auth.ViaToken || auth.Role != Role.Admin) return Error(401, "Nur mit API-Token.");
            var session = sessions.Create(Role.Admin, DateTime.UtcNow);
            return Results.Json(new { session = session.Id, csrf = session.Csrf, expiresUtc = session.ExpiresUtc });
        });

        api.MapPost("/setup", (SetupRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout) =>
        {
            var client = Client(http);
            if (lockout.IsLocked(client, DateTime.UtcNow)) return Error(429, "Zu viele Fehlversuche – bitte 5 Minuten warten.");
            if (auth.Setup(req.Code ?? "", req.Password ?? "") is { } error)
            {
                if (error.Contains("Code")) lockout.Fail(client, DateTime.UtcNow);
                return Error(400, error);
            }
            lockout.Success(client);
            return StartSession(http, sessions, Role.Admin);
        });

        api.MapPost("/login", (LoginRequest req, HttpContext http, AuthStore auth, SessionStore sessions, Lockout lockout) =>
        {
            var client = Client(http);
            var now = DateTime.UtcNow;
            if (lockout.IsLocked(client, now)) return Error(429, "Zu viele Fehlversuche – bitte 5 Minuten warten.");
            var role = auth.Login(req.Password ?? "");
            if (role == Role.None)
            {
                lockout.Fail(client, now);
                return Error(401, "Passwort oder PIN falsch.");
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

        g.MapGet("/devices/{id}/comparisons", async (string id, HubService hub) =>
            Results.Json(await hub.RunAsync(h => h.Comparisons(Device(h, id)).Select(Dto.Comparison).ToList())));

        g.MapGet("/events", (HttpContext http, EventStream events) => events.ServeAsync(http, AuthContext.Of(http).Role));
    }

    // ---------- Admin: Geräte ----------

    private static void MapDevices(RouteGroupBuilder g)
    {
        g.MapPost("/devices", async (DeviceRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var host = req.Host?.Trim() ?? "";
            if (host.Length == 0) throw new InvalidOperationException("Adresse fehlt.");
            if (h.Config.Devices.Any(d => string.Equals(d.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Dieser Host ist bereits eingetragen.");
            h.Config.Devices.Add(new DeviceConfig { Name = string.IsNullOrWhiteSpace(req.Name) ? host : req.Name.Trim(), Host = host });
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { id = Dto.DeviceId(host) };
        })));

        g.MapPut("/devices/{id}", async (string id, DeviceRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var c = Device(h, id).Config;
            if (req.Name is { } name && name.Trim().Length > 0) c.Name = name.Trim();
            if (req.WalletAddress is { } w) c.WalletAddress = w.Trim();
            if (req.Coin is "Auto" or "BTC" or "BCH") c.Coin = req.Coin;
            if (req.FirmwareRepo is { } repo && repo.Trim().Length > 0) c.FirmwareRepo = repo.Trim();
            if (req.LogAlerts is { } la) c.LogAlerts = la;
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { ok = true };
        })));

        g.MapDelete("/devices/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException("Bitte zuerst den laufenden Benchmark stoppen.");
            h.Config.Devices.RemoveAll(c => string.Equals(c.Host.Trim(), d.Config.Host.Trim(), StringComparison.OrdinalIgnoreCase));
            h.Config.Save();
            await h.ApplySettingsChangedAsync();
            return new { ok = true, note = "Verlauf in history.db und Steuerdaten bleiben erhalten." };
        })));

        g.MapPost("/devices/{id}/profile", async (string id, IdRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException("Während eines Benchmarks nicht möglich.");
            var profile = h.ProfilesFor(d).FirstOrDefault(p => p.Id == req.Id) ?? throw new KeyNotFoundException("Profil unbekannt.");
            h.SetProfile(d, profile);
            return new { ok = true };
        })));

        // Frequenz/Spannung: erst Vorschau (Text mit altem und neuem Wert), dann bestätigt ausführen
        g.MapPost("/devices/{id}/change/preview", async (string id, ChangeRequest req, HubService hub) =>
            Results.Json(await hub.RunAsync(async h => await h.PreviewChangeAsync(Device(h, id), req.Frequency, req.Voltage))));

        g.MapPost("/devices/{id}/change", async (string id, ChangeRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            await h.ApplyChangeAsync(Device(h, id), req.Frequency, req.Voltage);
            return new { ok = true };
        })));

        g.MapPost("/devices/{id}/restart", async (string id, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            if (d.IsBenchmarkRunning) throw new InvalidOperationException("Während eines Benchmarks nicht möglich.");
            await d.Connection.RestartAsync(); // öffnet das Wartungsfenster: keine Offline-Meldung
            d.AddLog("Neustart ausgelöst (Browser).");
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
            d.AddLog("Benchmark im Browser bestätigt.");
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
            d.AddLog("Automatik-Einstellungen gespeichert (Browser).");
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
            var snap = snaps.FirstOrDefault(s => Path.GetFileName(s.FilePath) == req.File) ?? throw new KeyNotFoundException("Sicherung nicht gefunden.");
            return SettingsSnapshots.Diff(snap, current).Select(c => new { c.Field, c.Label, group = c.GroupText, c.Current, c.Saved }).ToList();
        })));

        g.MapPost("/devices/{id}/snapshots/restore", async (string id, SnapshotRequest req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var d = Device(h, id);
            var (snaps, current) = await h.RestoreCandidatesAsync(d);
            var snap = snaps.FirstOrDefault(s => Path.GetFileName(s.FilePath) == req.File) ?? throw new KeyNotFoundException("Sicherung nicht gefunden.");
            var fields = req.Fields ?? [];
            var changes = SettingsSnapshots.Diff(snap, current).Where(c => fields.Contains(c.Field)).ToList();
            if (changes.Count == 0) throw new InvalidOperationException("Keine Felder ausgewählt.");
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
        g.MapGet("/settings", async (HubService hub) => Results.Json(await hub.RunAsync(h => SettingsDto.From(h.Config))));

        g.MapPut("/settings", async (SettingsDto req, HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            req.ApplyTo(h.Config);
            h.Config.Save();
            h.SyncLogAlerts();
            await h.ApplySettingsChangedAsync();
            return new { ok = true };
        })));

        g.MapPost("/notifications/test", async (HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var error = await h.Notify.TestAsync(h.Config.Notifications);
            return new { ok = error is null, error };
        })));

        g.MapPost("/report/send", async (HubService hub) => Results.Json(await hub.RunAsync(async h =>
        {
            var error = await h.SendDailyReportAsync(DateTime.Now, markSent: false);
            return new { ok = error is null, error };
        })));

        g.MapPost("/password", (PasswordRequest req, AuthStore auth, SessionStore sessions) =>
        {
            if (auth.ChangePassword(req.Current ?? "", req.Password ?? "") is { } error) return Error(400, error);
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
            auth.RevokeToken(tokenId) ? Results.Ok(new { ok = true }) : Error(404, "Token nicht gefunden."));

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
            message = updater.LastMessage,
        }));

        g.MapPost("/admin/update/check", async (ServerUpdater updater) =>
        {
            var r = await updater.CheckAsync(notify: false);
            return Results.Json(new { available = r.Status == Core.Update.UpdateCheckStatus.UpdateAvailable, latest = r.Update?.Tag, message = r.Message });
        });

        g.MapPost("/admin/update/install", (ServerUpdater updater, ILogger<ServerUpdater> log) =>
        {
            if (updater.Latest is null) return Error(400, "Kein Update verfügbar – zuerst nach Updates suchen.");
            if (!updater.CanInstall) return Error(400, "Diese Installation aktualisiert sich nicht selbst. Docker: „docker compose pull && docker compose up -d“.");
            _ = Task.Run(async () =>
            {
                try { await updater.InstallAsync(); }
                catch (Exception ex) { log.LogError(ex, "Update fehlgeschlagen"); }
            });
            return Results.Json(new { ok = true, message = "Update wird geladen und installiert – der Server startet danach neu (ca. 1 Minute)." });
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
                return Error(409, "Der Server enthält bereits Daten (Geräte, Verlauf oder Steuerdaten). Übernahme nur nach ausdrücklicher Bestätigung – der Server sichert seinen Stand vorher.");

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
                catch (InvalidDataException ex) { return Error(400, "Archiv abgelehnt: " + ex.Message); }
                var backup = await hub.ReplaceDataAsync(staging);
                var samples = manifest.HistoryRows.GetValueOrDefault("samples");
                return Results.Json(new
                {
                    ok = true,
                    message = $"Übernommen: {manifest.Devices} Gerät(e), {samples:N0} Verlaufswerte, {manifest.Files.Count} Dateien (Quelle {manifest.Source}, {manifest.CreatedUtc.ToLocalTime():g}). " +
                              $"Vorheriger Server-Stand gesichert in {Path.GetFileName(backup)}.",
                });
            }
            finally
            {
                try { File.Delete(upload); } catch { /* egal */ }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* egal */ }
            }
        });

        g.MapGet("/tax/rewards", async (HubService hub) => Results.Json(await hub.RunAsync(h => new
        {
            wallets = h.TaxMonitor.Wallets,
            rewards = h.TaxMonitor.LoadRewards().OrderByDescending(r => r.ReceivedAtUtc),
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
