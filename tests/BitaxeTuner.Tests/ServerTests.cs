using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Server;
using BitaxeTuner.Server.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BitaxeTuner.Tests;

/// <summary>Server-API mit simulierten Minern: Einrichtung, Anmeldung, Rollen, CSRF, Token, Befehle, Live-Ereignisse.</summary>
public sealed class ServerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ServerTests()
    {
        File.WriteAllText(_dir.File("config.json"), """
            { "IntervalSeconds": 60, "Devices": [ { "Name": "Gamma Wohnzimmer", "Host": "192.168.50.10", "WalletAddress": "bc1qtestwalletadresse" } ] }
            """);
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ServerSettings>();
                s.AddSingleton(new ServerSettings { DataDirectory = _dir.Path });
                // Neustart beendet sonst den Prozess – in Tests nur mitzählen
                s.RemoveAll<ServerRestart>();
                s.AddSingleton<ServerRestart>(sp => _restart = new FakeRestart(sp.GetRequiredService<HubService>()));
                s.AddSingleton(new MinerHubOptions
                {
                    ClientFactory = h => new SimulatedMinerClient(gamma, 3, h),
                    OnlineChecks = false,
                    BenchmarkDelay = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                });
            });
        });
    }

    private FakeRestart? _restart;

    private sealed class FakeRestart(HubService hub) : ServerRestart(hub, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerRestart>.Instance)
    {
        public List<string> Reasons { get; } = [];
        public override void Schedule(string reason) => Reasons.Add(reason);
    }

    [Fact]
    public async Task Https_can_be_switched_on_and_the_app_gets_the_fingerprint()
    {
        // Audit S4: bestehende Installation (HTTP) → umstellen speichert die Wahl, liefert den Fingerabdruck, startet neu
        var admin = await AdminAsync();
        var status = await Json(await admin.GetAsync("/api/v1/admin/https"));
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("configurable").GetBoolean());

        var r = await Json(await admin.PostAsJsonAsync("/api/v1/admin/https", new { enable = true }));
        Assert.True(r.GetProperty("restarting").GetBoolean());
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", r.GetProperty("fingerprint").GetString()!);
        Assert.True(ServerSettings.ReadStoredHttps(_dir.Path));
        Assert.Single(_restart!.Reasons);
        Assert.Contains("HTTPS", (await Json(await admin.GetAsync("/api/v1/journal?range=24h&cats=settings"))).GetRawText());

        // nur für Admins
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/admin/https", new { enable = true })).StatusCode);
    }

    [Fact]
    public void New_installations_start_with_https_existing_ones_keep_http()
    {
        using var fresh = new TempDir();
        Assert.True(ServerSettings.ResolveHttps(fresh.Path, null));                 // Neuinstallation
        Assert.False(File.Exists(Path.Combine(fresh.Path, ServerSettings.StoredFile)));   // Lesen schreibt nichts
        using var existing = new TempDir();
        File.WriteAllText(Path.Combine(existing.Path, "config.json"), "{}");
        Assert.False(ServerSettings.ResolveHttps(existing.Path, null));             // bestehend: wie bisher HTTP
        ServerSettings.WriteStoredHttps(existing.Path, true);
        Assert.True(ServerSettings.ResolveHttps(existing.Path, null));              // gespeicherte Wahl gilt
        Assert.False(ServerSettings.ResolveHttps(existing.Path, false));            // fest vorgegeben gewinnt

        // Erster Start einer Neuinstallation: Datenordner gibt es noch nicht – Zertifikat darf nicht abstürzen
        var notYet = Path.Combine(fresh.Path, "neu", "data");
        var cert = Certificates.LoadOrCreate(notYet);
        Assert.True(File.Exists(Path.Combine(notYet, "server-cert.pfx")));
        Assert.Equal(Certificates.Fingerprint(cert), Certificates.Fingerprint(Certificates.LoadOrCreate(notYet)));   // bleibt gleich
    }

    public void Dispose()
    {
        _factory.Dispose();
        _dir.Dispose();
    }

    private AuthStore Auth => _factory.Services.GetRequiredService<AuthStore>();

    /// <summary>Eingerichteter Server, angemeldeter Admin-Browser (mit CSRF-Wert).</summary>
    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        var r = await client.PostAsJsonAsync("/api/v1/setup", new { code = Auth.SetupCode, password = "sehr-geheim-123" });
        r.EnsureSuccessStatusCode();
        var csrf = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrf").GetString()!;
        client.DefaultRequestHeaders.Add(AuthContext.CsrfHeader, csrf);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> DeviceIdAsync(HttpClient c) =>
        (await Json(await c.GetAsync("/api/v1/status"))).GetProperty("devices")[0].GetProperty("id").GetString()!;

    [Fact]
    public async Task Soak_batch_api_needs_admin_and_csrf_and_starts_selected_miners()
    {
        var admin = await AdminAsync();
        var id = await DeviceIdAsync(admin);
        for (var i = 0; i < 50 && !(await Json(await admin.GetAsync("/api/v1/status"))).GetProperty("devices")[0].GetProperty("online").GetBoolean(); i++)
            await Task.Delay(100);

        // ohne CSRF-Kopf abgelehnt
        var noCsrf = _factory.CreateClient();
        foreach (var c in admin.DefaultRequestHeaders.Where(h => h.Key != AuthContext.CsrfHeader)) noCsrf.DefaultRequestHeaders.Add(c.Key, c.Value);
        Assert.False((await noCsrf.PostAsJsonAsync("/api/v1/soak/stop-all", new { })).IsSuccessStatusCode);

        var prep = await Json(await admin.PostAsJsonAsync("/api/v1/soak/prepare", new { }));
        var m = prep.GetProperty("miners")[0];
        Assert.True(m.GetProperty("eligible").GetBoolean());
        Assert.True(m.GetProperty("frequencyMhz").GetInt32() > 0);

        var start = await Json(await admin.PostAsJsonAsync("/api/v1/soak/start", new { hours = 12, ids = new[] { id } }));
        Assert.Equal(1, start.GetProperty("started").GetInt32());
        var status = await Json(await admin.GetAsync("/api/v1/status"));
        Assert.Equal(JsonValueKind.Object, status.GetProperty("devices")[0].GetProperty("soak").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/soak/start", new { hours = 12, ids = Array.Empty<string>() })).StatusCode);
        Assert.Equal(1, (await Json(await admin.PostAsJsonAsync("/api/v1/soak/stop-all", new { }))).GetProperty("stopped").GetInt32());
    }

    [Fact]
    public async Task Error_messages_follow_the_language_of_the_request()
    {
        var client = _factory.CreateClient();
        async Task<string> SetupError(string? language, string password)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup") { Content = JsonContent.Create(new { code = Auth.SetupCode, password }) };
            if (language is not null) req.Headers.AcceptLanguage.ParseAdd(language);
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        }

        // Platzhalter in einem übersetzten Text (Mindestlänge) – ohne Angabe gilt die Sprache des Servers (Tests: Deutsch)
        Assert.Equal("The password needs at least 10 characters.", await SetupError("en", "kurz"));
        Assert.Equal("Das Passwort braucht mindestens 10 Zeichen.", await SetupError("de", "kurz"));
        Assert.Equal("Das Passwort braucht mindestens 10 Zeichen.", await SetupError(null, "kurz"));
        Assert.Equal("Das Passwort braucht mindestens 10 Zeichen.", await SetupError("fr-FR", "kurz"));

        // Ohne Anmeldung: feste Meldung
        var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        status.Headers.AcceptLanguage.ParseAdd("en-US");
        var unauth = await client.SendAsync(status);
        Assert.Equal("Please sign in.", (await unauth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // Übersetzungstabelle für die Browser-Oberfläche: öffentlich, mit ETag
        var table = await client.GetAsync("/api/v1/i18n/en");
        Assert.Equal("Sign in", (await Json(table)).GetProperty("Anmelden").GetString());
        Assert.NotNull(table.Headers.ETag);
    }

    [Fact]
    public async Task Copies_fan_settings_to_other_miners_after_preview_never_frequency()
    {
        var admin = await AdminAsync();
        await Json(await admin.PostAsJsonAsync("/api/v1/devices", new { name = "Supra Büro", host = "192.168.50.11" }));
        var devices = (await Json(await admin.GetAsync("/api/v1/status"))).GetProperty("devices").EnumerateArray().ToList();
        var source = devices.First(d => d.GetProperty("name").GetString() == "Gamma Wohnzimmer").GetProperty("id").GetString()!;
        var target = devices.First(d => d.GetProperty("name").GetString() == "Supra Büro").GetProperty("id").GetString()!;

        // Quelle: Lüfter manuell 80 %, andere Frequenz – die darf nie mitkommen
        var hub = _factory.Services.GetRequiredService<HubService>();
        await hub.RunAsync(async h =>
        {
            var d = h.Devices.First(x => x.Config.Host == "192.168.50.10");
            await d.Connection.PatchSettingsAsync(new Dictionary<string, object> { ["autofanspeed"] = 0, ["manualFanSpeed"] = 80, ["frequency"] = 575 });
        });

        var body = new { source, targets = new[] { target }, groups = new[] { "fan" } };
        var preview = (await Json(await admin.PostAsJsonAsync("/api/v1/devices/copy/preview", body)))[0];
        var fields = preview.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("label").GetString()).ToList();
        Assert.Contains("Lüfter manuell (%)", fields);
        Assert.DoesNotContain("Frequenz (MHz)", fields);

        await Json(await admin.PostAsJsonAsync("/api/v1/devices/copy", body));
        var after = await hub.RunAsync(async h => await h.Devices.First(x => x.Config.Host == "192.168.50.11").Connection.GetInfoAsync());
        Assert.Equal(80, after.FanPercent);
        Assert.NotEqual(575, after.FrequencyMhz);
        // Danach keine Unterschiede mehr
        var again = (await Json(await admin.PostAsJsonAsync("/api/v1/devices/copy/preview", body)))[0];
        Assert.Equal(0, again.GetProperty("changes").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/devices/copy/preview",
            new { source, targets = new[] { target }, groups = new[] { "tuning" } })).StatusCode);
    }

    [Fact]
    public async Task Onboarding_checklist_in_status_for_admins_until_hidden()
    {
        var admin = await AdminAsync();
        var hub = _factory.Services.GetRequiredService<HubService>();
        await hub.RunAsync(h => { BitaxeTuner.Core.Config.Onboarding.SetVisible(h.Config, true); return true; });

        var status = await Json(await admin.GetAsync("/api/v1/status"));
        var steps = status.GetProperty("onboarding").EnumerateArray().ToList();
        Assert.Equal("miners", steps[0].GetProperty("id").GetString());
        Assert.True(steps[0].GetProperty("done").GetBoolean());       // Testserver hat schon einen Miner
        Assert.Equal("Geräte", steps[0].GetProperty("section").GetString());

        await Json(await admin.PostAsJsonAsync("/api/v1/onboarding", new { show = false }));
        Assert.Equal(JsonValueKind.Null, (await Json(await admin.GetAsync("/api/v1/status"))).GetProperty("onboarding").ValueKind);
        Assert.True(await hub.RunAsync(h => h.Config.OnboardingDone));
    }

    [Fact]
    public async Task Push_targets_are_offered_saved_validated_and_mirrored()
    {
        var admin = await AdminAsync();
        var hub = _factory.Services.GetRequiredService<HubService>();
        await hub.RunAsync(h =>
        {
            h.Config.Notifications.Provider = "ntfy";
            h.Config.Notifications.NtfyTopic = "bisher";
            h.Config.DailyReport.LastMonthlySent = "2026-08";
            return true;
        });

        // Bisherige Einzel-Einstellung erscheint als erstes Ziel
        var settings = System.Text.Json.Nodes.JsonNode.Parse((await Json(await admin.GetAsync("/api/v1/settings"))).GetRawText())!;
        var targets = settings["notifications"]!["targets"]!.AsArray();
        Assert.Equal("bisher", Assert.Single(targets)!["ntfyTopic"]!.GetValue<string>());

        // Zweites Ziel: Discord nur für Blockfunde eines Miners (+ unbekannter Miner wird entfernt)
        targets.Add(System.Text.Json.Nodes.JsonNode.Parse("""
            {"id":"a1b2c3d4","name":"Community","enabled":true,"provider":"discord","discordWebhookUrl":"https://discord.com/api/webhooks/1/x",
             "categories":["Finds","Quatsch"],"miners":["192.168.50.10","10.9.9.9"]}
            """));
        await Json(await admin.PutAsync("/api/v1/settings", JsonContent.Create(settings)));
        var saved = await hub.RunAsync(h => h.Config.Notifications.Clone());
        Assert.Equal(2, saved.Targets.Count);
        var community = saved.Targets.Single(t => t.Name == "Community");
        Assert.Equal(["Finds"], community.Categories);
        Assert.Equal(["192.168.50.10"], community.Miners);
        Assert.Equal("ntfy", saved.Provider);                                   // erstes Ziel gespiegelt
        Assert.Equal("2026-08", await hub.RunAsync(h => h.Config.DailyReport.LastMonthlySent));

        // Test eines einzelnen Ziels / unbekanntes Ziel
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/notifications/test", new { targetId = "ffffffff" })).StatusCode);

        // Unbekannter Dienst → 400
        settings["notifications"]!["targets"]![1]!["provider"] = "fax";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/v1/settings", JsonContent.Create(settings))).StatusCode);

        // Alle entfernt → auch Einzel-Einstellung aus
        settings["notifications"]!["targets"] = new System.Text.Json.Nodes.JsonArray();
        await Json(await admin.PutAsync("/api/v1/settings", JsonContent.Create(settings)));
        Assert.Equal("none", await hub.RunAsync(h => h.Config.Notifications.Provider));
        Assert.False(await hub.RunAsync(h => h.Notify.Enabled));
    }

    [Fact]
    public async Task Reports_are_available_as_json_csv_and_html_for_admins()
    {
        var admin = await AdminAsync();
        await Json(await admin.GetAsync("/api/v1/status"));                 // einmal abfragen → Minutenwert
        var list = await Json(await admin.GetAsync("/api/v1/reports"));
        var month = DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(month, list.GetProperty("periods").EnumerateArray().Select(p => p.GetString()));

        var json = await Json(await admin.GetAsync($"/api/v1/reports/{month}"));
        Assert.True(json.GetProperty("partial").GetBoolean());
        Assert.Equal("Gamma Wohnzimmer", json.GetProperty("miners")[0].GetProperty("name").GetString());

        var csv = await admin.GetAsync($"/api/v1/reports/{month}?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var html = await admin.GetAsync($"/api/v1/reports/{DateTime.Now.Year}?format=html");
        Assert.Equal("text/html", html.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("<script", await html.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/reports/2026-13")).StatusCode);
        var deviceId = (await Json(await admin.GetAsync("/api/v1/status"))).GetProperty("devices")[0].GetProperty("id").GetString();
        var health = await Json(await admin.GetAsync($"/api/v1/devices/{deviceId}/health"));
        Assert.Equal(JsonValueKind.Array, health.GetProperty("findings").ValueKind);
        Assert.True(health.TryGetProperty("recent", out _));

        var months = await Json(await admin.GetAsync($"/api/v1/reports/{DateTime.Now.Year}/months"));
        Assert.Equal(DateTime.Now.Month, months.GetProperty("months").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/reports/1999/months")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/reports/{month}")).StatusCode);
    }

    [Fact]
    public async Task Smart_plugs_are_saved_measured_and_only_local_addresses_are_allowed()
    {
        var admin = await AdminAsync();
        var settings = new
        {
            items = new[] { new { id = "", name = "Steckdose Gamma", host = "sim", channel = 0, user = "admin", role = "miners", miners = new[] { "192.168.50.10", "10.9.9.9" } } },
            useForCosts = true,
            intervalSeconds = 1,
        };
        var saved = await Json(await admin.PutAsJsonAsync("/api/v1/plugs", new { settings, passwords = new Dictionary<string, string>() }));
        var plug = saved.GetProperty("settings").GetProperty("items")[0];
        var id = plug.GetProperty("id").GetString()!;
        Assert.Matches("^[a-z0-9]{8}$", id);
        Assert.Equal(["192.168.50.10"], plug.GetProperty("miners").EnumerateArray().Select(m => m.GetString()));   // unbekannter Miner entfernt
        Assert.Equal(5, saved.GetProperty("settings").GetProperty("intervalSeconds").GetInt32());                   // Untergrenze

        // Passwort nur schreibbar
        var withPw = new { settings = saved.GetProperty("settings"), passwords = new Dictionary<string, string> { [id] = "plug-geheim-1" } };
        var again = await Json(await admin.PutAsJsonAsync("/api/v1/plugs", withPw));
        Assert.True(again.GetProperty("passwordSet").GetProperty(id).GetBoolean());
        Assert.DoesNotContain("plug-geheim-1", again.GetRawText());
        Assert.DoesNotContain("plug-geheim-1", File.ReadAllText(_dir.File("config.json")));

        var status = await Json(await admin.GetAsync("/api/v1/status"));
        var p = status.GetProperty("plugs")[0];
        Assert.True(p.GetProperty("online").GetBoolean());
        Assert.True(p.GetProperty("overheadW").GetDouble() > 0);
        Assert.True(status.GetProperty("totals").GetProperty("wallPower").GetDouble() > status.GetProperty("totals").GetProperty("power").GetDouble());
        Assert.True(status.GetProperty("devices")[0].GetProperty("wallPower").GetDouble() > 0);

        foreach (var host in new[] { "8.8.8.8", "http://192.168.1.60", "192.168.1.60/rpc/Switch.Set" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/plugs/probe", new { host, channel = 0 })).StatusCode);
        var bad = new { settings = new { items = new[] { new { id = "", name = "x", host = "1.1.1.1", channel = 0, user = "admin", role = "total", miners = Array.Empty<string>() } }, useForCosts = true, intervalSeconds = 10 } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/plugs", bad)).StatusCode);
        Assert.True((await Json(await admin.PostAsJsonAsync("/api/v1/plugs/probe", new { host = "sim", channel = 0 }))).GetProperty("ok").GetBoolean());

        var hist = await Json(await admin.GetAsync($"/api/v1/plugs/{id}/history?range=1h"));
        Assert.True(hist.GetProperty("plug").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Array, hist.GetProperty("axeos").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/plugs/unbekannt/history")).StatusCode);

        // Entfernen löscht das gespeicherte Passwort
        await Json(await admin.PutAsJsonAsync("/api/v1/plugs", new { settings = new { items = Array.Empty<object>(), useForCosts = true, intervalSeconds = 10 } }));
        var hub = _factory.Services.GetRequiredService<HubService>();
        Assert.False(await hub.RunAsync(h => h.Secrets.Has(BitaxeTuner.Core.Config.SmartPlugConfig.SecretKey(id))));
    }

    [Fact]
    public async Task Fresh_server_needs_setup_code_and_locks_after_failures()
    {
        var client = _factory.CreateClient();
        var info = await Json(await client.GetAsync("/api/v1/info"));
        Assert.True(info.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/status")).StatusCode);

        // zu kurzes Passwort
        var shortPw = await client.PostAsJsonAsync("/api/v1/setup", new { code = Auth.SetupCode, password = "kurz" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);

        for (var i = 0; i < Lockout.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/setup", new { code = "0000-0000-0000", password = "sehr-geheim-123" })).StatusCode);
        var locked = await client.PostAsJsonAsync("/api/v1/setup", new { code = Auth.SetupCode, password = "sehr-geheim-123" });
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.False(Auth.IsSetUp);
    }

    [Fact]
    public async Task Admin_sees_everything_and_password_is_stored_hashed()
    {
        var admin = await AdminAsync();
        Assert.Null(Auth.SetupCode);
        var status = await Json(await admin.GetAsync("/api/v1/status"));
        var device = status.GetProperty("devices")[0];
        Assert.Equal("192.168.50.10", device.GetProperty("host").GetString());

        var detail = await Json(await admin.GetAsync($"/api/v1/devices/{device.GetProperty("id").GetString()}"));
        Assert.Equal("bc1qtestwalletadresse", detail.GetProperty("config").GetProperty("walletAddress").GetString());

        var stored = File.ReadAllText(_dir.File("server-auth.json"));
        Assert.DoesNotContain("sehr-geheim-123", stored);
        Assert.Contains("pbkdf2-sha256$", stored);
    }

    [Fact]
    public async Task Browser_writes_need_csrf_token()
    {
        var admin = await AdminAsync();
        var id = await DeviceIdAsync(admin);
        admin.DefaultRequestHeaders.Remove(AuthContext.CsrfHeader);
        var r = await admin.PostAsJsonAsync($"/api/v1/devices/{id}/soak/stop", new { });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("CSRF", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_pin_gives_read_only_access_without_addresses()
    {
        var admin = await AdminAsync();
        var settings = await Json(await admin.GetAsync("/api/v1/settings"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
        body["newViewerPin"] = "471100";
        await Json(await admin.PutAsJsonAsync("/api/v1/settings", body));

        var viewer = _factory.CreateClient();
        var login = await Json(await viewer.PostAsJsonAsync("/api/v1/login", new { password = "471100" }));
        Assert.Equal("Viewer", login.GetProperty("role").GetString());
        viewer.DefaultRequestHeaders.Add(AuthContext.CsrfHeader, login.GetProperty("csrf").GetString());

        var status = await Json(await viewer.GetAsync("/api/v1/status"));
        var device = status.GetProperty("devices")[0];
        Assert.Equal(JsonValueKind.Null, device.GetProperty("host").ValueKind);
        var raw = status.GetRawText();
        Assert.DoesNotContain("192.168.50.10", raw);
        Assert.DoesNotContain("bc1q", raw);

        var detail = await Json(await viewer.GetAsync($"/api/v1/devices/{device.GetProperty("id").GetString()}"));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("config").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("log").ValueKind);
        Assert.DoesNotContain("192.168.50.10", detail.GetRawText());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync($"/api/v1/devices/{device.GetProperty("id").GetString()}/change", new { frequency = 500, voltage = 1100 })).StatusCode);
    }

    [Fact]
    public async Task Viewer_access_per_group_sees_only_its_miners_and_can_be_revoked()
    {
        var admin = await AdminAsync();
        var keller = (await Json(await admin.PostAsJsonAsync("/api/v1/devices", new { name = "Supra Keller", host = "192.168.50.11" }))).GetProperty("id").GetString()!;
        var wohnen = await DeviceIdAsync(admin);
        await Json(await admin.PutAsJsonAsync($"/api/v1/devices/{keller}", new { groups = new[] { "Keller" } }));
        await Json(await admin.PutAsJsonAsync($"/api/v1/devices/{wohnen}", new { groups = new[] { "Wohnung" } }));

        // PIN-Regeln: mindestens 6 Ziffern, keine doppelte PIN
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/viewers", new { name = "Kurz", pin = "1234", groups = new[] { "Keller" } })).StatusCode);
        var created = await Json(await admin.PostAsJsonAsync("/api/v1/viewers", new { name = "Werkstatt", pin = "246813", groups = new[] { "keller" } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/viewers", new { name = "Doppelt", pin = "246813" })).StatusCode);
        Assert.DoesNotContain("246813", File.ReadAllText(_dir.File("server-auth.json")));
        var listed = await Json(await admin.GetAsync("/api/v1/viewers"));
        Assert.Equal(1, listed.GetArrayLength());
        Assert.False(listed[0].TryGetProperty("pinHash", out _));

        var viewer = _factory.CreateClient();
        var login = await Json(await viewer.PostAsJsonAsync("/api/v1/login", new { password = "246813" }));
        Assert.Equal("Viewer", login.GetProperty("role").GetString());
        var session = await Json(await viewer.GetAsync("/api/v1/session"));
        Assert.Equal("keller", session.GetProperty("groups")[0].GetString(), ignoreCase: true);

        var status = await Json(await viewer.GetAsync("/api/v1/status"));
        var devices = status.GetProperty("devices");
        Assert.Equal(1, devices.GetArrayLength());
        Assert.Equal(keller, devices[0].GetProperty("id").GetString());
        Assert.Equal(1, status.GetProperty("totals").GetProperty("count").GetInt32());
        Assert.Equal(0, status.GetProperty("history").GetArrayLength());                 // kein Gesamtverlauf über alle Miner
        Assert.DoesNotContain("Wohnung", status.GetRawText());                            // fremde Gruppen nicht sichtbar
        Assert.DoesNotContain("Gamma Wohnzimmer", status.GetRawText());

        await Json(await viewer.GetAsync($"/api/v1/devices/{keller}"));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/devices/{wohnen}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/devices/{wohnen}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/devices/{wohnen}/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/devices/all/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/compare/report?ids={keller},{wohnen}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/display/preview.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/viewers")).StatusCode);

        // Widerruf beendet die Sitzung sofort, die PIN gilt nicht mehr; alles steht im Protokoll
        await Json(await admin.DeleteAsync($"/api/v1/viewers/{created.GetProperty("id").GetString()}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/v1/login", new { password = "246813" })).StatusCode);
        var journal = (await Json(await admin.GetAsync("/api/v1/journal?range=24h&cats=settings"))).GetRawText();
        Assert.Contains("Werkstatt", journal);
        Assert.Contains("widerrufen", journal);

        // Die allgemeine PIN sieht weiterhin alle Miner
        var settings = await Json(await admin.GetAsync("/api/v1/settings"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
        body["newViewerPin"] = "471100";
        await Json(await admin.PutAsJsonAsync("/api/v1/settings", body));
        var all = _factory.CreateClient();
        await Json(await all.PostAsJsonAsync("/api/v1/login", new { password = "471100" }));
        Assert.Equal(2, (await Json(await all.GetAsync("/api/v1/status"))).GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task Benchmark_result_can_be_saved_as_preset_without_touching_the_miner()
    {
        var admin = await AdminAsync();
        var id = await DeviceIdAsync(admin);
        var detail = await Json(await admin.GetAsync($"/api/v1/devices/{id}"));
        var max = detail.GetProperty("profile").GetProperty("maxFrequencyMhz").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/devices/{id}/presets", new { name = "Zu hoch", frequencyMhz = max + 100, coreVoltageMv = 1150 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/devices/{id}/presets", new { name = " ", frequencyMhz = 500, coreVoltageMv = 1150 })).StatusCode);

        var first = await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/presets", new { name = "Effizienz", frequencyMhz = 500, coreVoltageMv = 1150 }));
        Assert.False(first.GetProperty("replaced").GetBoolean());
        var second = await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/presets", new { name = "effizienz", frequencyMhz = 525, coreVoltageMv = 1150 }));
        Assert.True(second.GetProperty("replaced").GetBoolean());
        var presets = (await Json(await admin.GetAsync($"/api/v1/devices/{id}"))).GetProperty("config").GetProperty("presets");
        Assert.Equal(1, presets.GetArrayLength());
        Assert.Equal(525, presets[0].GetProperty("frequencyMhz").GetInt32());

        var journal = (await Json(await admin.GetAsync("/api/v1/journal?range=24h&cats=automation"))).GetRawText();
        Assert.Contains("Voreinstellung ersetzt", journal);
        Assert.DoesNotContain("\"tuning\"", (await Json(await admin.GetAsync("/api/v1/journal?range=24h&cats=tuning"))).GetProperty("entries").GetRawText());

        var viewer = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync($"/api/v1/devices/{id}/presets", new { name = "x", frequencyMhz = 500, coreVoltageMv = 1150 })).StatusCode);
    }

    [Fact]
    public void Schedule_knows_which_presets_it_uses()
    {
        var s = new Core.Config.PresetScheduleRule { DefaultPreset = "Tag", Entries = [new Core.Config.ScheduleEntry { Preset = "Nacht" }] };
        Assert.True(s.UsesPreset("nacht"));
        Assert.True(s.UsesPreset("Tag"));
        Assert.False(s.UsesPreset("Effizienz"));
    }

    [Fact]
    public async Task Prometheus_metrics_are_off_by_default_and_need_their_own_token()
    {
        var admin = await AdminAsync();
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/metrics")).StatusCode);   // standardmäßig aus

        await Json(await admin.PutAsJsonAsync("/api/v1/metrics/settings", new { enabled = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/metrics")).StatusCode);   // ohne Token nichts
        var token = (await Json(await admin.PostAsJsonAsync("/api/v1/metrics/token", new { }))).GetProperty("token").GetString()!;
        Assert.StartsWith("btm_", token);
        Assert.DoesNotContain(token, File.ReadAllText(_dir.File("config.json")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/metrics")).StatusCode);
        anon.DefaultRequestHeaders.Authorization = new("Bearer", "btm_falsch");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/metrics")).StatusCode);

        var prom = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        prom.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await admin.GetAsync("/api/v1/status");   // eine Abfragerunde abwarten
        for (var i = 0; i < 50 && !(await Json(await admin.GetAsync("/api/v1/status"))).GetProperty("devices")[0].GetProperty("online").GetBoolean(); i++)
            await Task.Delay(100);
        var r = await prom.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType!.MediaType);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("bitaxetuner_info{version=", text);
        Assert.Contains("bitaxetuner_miner_up{miner=\"Gamma Wohnzimmer\"", text);
        Assert.Contains("bitaxetuner_miner_hashrate_ghs{", text);
        Assert.Contains("# TYPE bitaxetuner_miner_shares_accepted_total counter", text);
        Assert.DoesNotContain("192.168.50.10", text);   // keine IP-Adressen
        Assert.DoesNotContain("bc1q", text);            // keine Wallets
        // Prometheus-Token ist kein Admin-Token
        Assert.Equal(HttpStatusCode.Unauthorized, (await prom.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Api_token_for_desktop_works_without_cookie_and_can_be_revoked()
    {
        var admin = await AdminAsync();
        var created = await Json(await admin.PostAsJsonAsync("/api/v1/tokens", new { name = "Desktop-PC" }));
        var token = created.GetProperty("token").GetString()!;
        Assert.StartsWith("btk_", token);
        Assert.DoesNotContain(token, File.ReadAllText(_dir.File("server-auth.json")));

        var desktop = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        desktop.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var id = await DeviceIdAsync(desktop);
        await Json(await desktop.PostAsJsonAsync($"/api/v1/devices/{id}/soak/stop", new { })); // ohne CSRF: Token-Aufruf

        await Json(await admin.DeleteAsync($"/api/v1/tokens/{created.GetProperty("id").GetString()}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/v1/status")).StatusCode);

        desktop.DefaultRequestHeaders.Authorization = new("Bearer", "btk_gefaelscht");
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task Change_via_api_checks_limits_and_shows_old_and_new_value()
    {
        var admin = await AdminAsync();
        var id = await DeviceIdAsync(admin);
        var detail = await Json(await admin.GetAsync($"/api/v1/devices/{id}"));
        var max = detail.GetProperty("profile").GetProperty("maxFrequencyMhz").GetInt32();

        var tooHigh = await admin.PostAsJsonAsync($"/api/v1/devices/{id}/change/preview", new { frequency = max + 100, voltage = 1150 });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        Assert.Contains("außerhalb der Grenzen", await tooHigh.Content.ReadAsStringAsync());

        var preview = await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/change/preview", new { frequency = 550, voltage = 1150 }));
        Assert.Contains("→  550 MHz", preview.GetProperty("confirmText").GetString());
        await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/change", new { frequency = 550, voltage = 1150 }));

        var log = (await Json(await admin.GetAsync($"/api/v1/devices/{id}"))).GetProperty("log").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains(log, l => l!.Contains("→ 550 MHz / 1150 mV"));
    }

    [Fact]
    public async Task Benchmark_started_in_browser_runs_on_server()
    {
        var admin = await AdminAsync();
        var id = await DeviceIdAsync(admin);
        for (var i = 0; i < 50; i++) // Profilerkennung nach der ersten Runde abwarten
        {
            var p = (await Json(await admin.GetAsync($"/api/v1/devices/{id}"))).GetProperty("profile").GetProperty("id").GetString();
            if (p == "bitaxe-gamma") break;
            await Task.Delay(100);
        }
        var settings = new
        {
            startFrequencyMhz = 525, maxFrequencyMhz = 600, frequencyStepMhz = 25, startVoltageMv = 1150, minVoltageMv = 1100,
            maxVoltageMv = 1200, voltageStepMv = 25, warmupSeconds = 30, measureSeconds = 150, sampleIntervalSeconds = 15, minSamples = 7,
            maxChipTempC = 66, maxVrTempC = 85, maxPowerW = 40, minHashRateRatio = 0.9, maxErrorPercent = 2,
        };
        var prepare = await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/benchmark/prepare", new { settings, resume = false }));
        Assert.Contains("Benchmark starten", prepare.GetProperty("confirmText").GetString());
        await Json(await admin.PostAsJsonAsync($"/api/v1/devices/{id}/benchmark/start", new { settings, resume = false }));

        JsonElement bench = default;
        for (var i = 0; i < 300; i++)
        {
            bench = (await Json(await admin.GetAsync($"/api/v1/devices/{id}"))).GetProperty("summary").GetProperty("benchmark");
            if (bench.ValueKind == JsonValueKind.Object && !bench.GetProperty("running").GetBoolean()) break;
            await Task.Delay(100);
        }
        Assert.Equal("Fertig", bench.GetProperty("phase").GetString());
        var session = (await Json(await admin.GetAsync($"/api/v1/devices/{id}"))).GetProperty("session");
        Assert.True(session.GetProperty("isFinished").GetBoolean());
        Assert.True(session.GetProperty("results").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Event_stream_starts_with_current_status()
    {
        var admin = await AdminAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await admin.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var lines = new List<string>();
        while (lines.Count < 4 && await reader.ReadLineAsync(cts.Token) is { } line) lines.Add(line);
        Assert.Contains("event: status", lines);
        Assert.Contains(lines, l => l.StartsWith("data: {") && l.Contains("\"totals\""));
    }

    [Fact]
    public async Task Web_ui_is_served_with_security_headers()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/html", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/gibtsnicht")).StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]      // Docker-Netz
    [InlineData("100.101.102.103", true)] // Tailscale
    [InlineData("127.0.0.1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void Only_home_network_and_vpn_are_private(string ip, bool expected) =>
        Assert.Equal(expected, NetworkRules.IsPrivate(IPAddress.Parse(ip)));
}
