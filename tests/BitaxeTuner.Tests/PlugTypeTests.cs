using System.Net;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Plugs;

namespace BitaxeTuner.Tests;

/// <summary>0.9.14: weitere Steckdosen – Tasmota und FRITZ!DECT (nur lesen).</summary>
public class PlugTypeTests
{
    private sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    [Fact]
    public async Task Tasmota_reads_power_and_energy_per_channel_and_reports_bad_passwords()
    {
        var fake = new Fake(r => r.RequestUri!.Query.Contains("Status%200")
            ? (HttpStatusCode.OK, """{"Status":{"DeviceName":"Regal","FriendlyName":["Regal"]},"StatusFWR":{"Version":"14.1.0(tasmota)"}}""")
            : (HttpStatusCode.OK, """{"StatusSNS":{"ENERGY":{"Total":12.345,"Power":[45,7],"Voltage":231,"Current":[0.21,0.05]}}}"""));
        using var client = new TasmotaClient("10.0.0.20", "admin", "geheim", fake);
        var id = await client.IdentifyAsync();
        Assert.Equal("Tasmota 14.1.0 · Regal", id.Model);
        var r = await client.ReadAsync(1);
        Assert.Equal((7d, 12345d, 231d, 0.05d), (r.PowerW, r.EnergyWh!.Value, r.Voltage!.Value, r.Current!.Value));
        Assert.Contains("&user=admin&password=geheim", fake.Asked[1]);

        var single = TasmotaClient.Parse(JsonDocument.Parse("""{"StatusSNS":{"ENERGY":{"Total":0.5,"Power":18}}}""").RootElement, 0);
        Assert.Equal((18d, 500d), (single.PowerW, single.EnergyWh!.Value));

        using var denied = new TasmotaClient("10.0.0.20", null, null, new Fake(_ => (HttpStatusCode.OK, """{"WARNING":"Need user=<username>&password=<password>"}""")));
        await Assert.ThrowsAsync<LocalizedException>(() => denied.ReadAsync(0));
    }

    [Fact]
    public void Fritz_login_response_matches_the_examples_from_avm()
    {
        // AVM, „Session-IDs im FRITZ!OS“: PBKDF2 (ab FRITZ!OS 7.24) und MD5 (ältere Versionen)
        Assert.Equal("5A1722$1798a1672bca7c6463d6b245f82b53703b0f50813401b03e4045a5861e689adb",
            FritzDectClient.Response("2$10000$5A1711$2000$5A1722", "1example!"));
        Assert.Equal("1234567z-9e224a41eeefa284df7bb0f26c2913e2", FritzDectClient.Response("1234567z", "äbc"));
    }

    [Fact]
    public async Task Fritz_logs_in_reads_milliwatts_and_renews_an_expired_session()
    {
        var logins = 0;
        var expired = true;
        var fake = new Fake(r =>
        {
            var q = r.RequestUri!.PathAndQuery;
            if (q.StartsWith("/login_sid.lua") && r.Method == HttpMethod.Get)
                return (HttpStatusCode.OK, "<SessionInfo><SID>0000000000000000</SID><Challenge>2$10000$5A1711$2000$5A1722</Challenge><BlockTime>0</BlockTime><Users><User last=\"1\">fritz1234</User></Users></SessionInfo>");
            if (q.StartsWith("/login_sid.lua"))
            {
                logins++;
                var body = r.Content!.ReadAsStringAsync().Result;
                Assert.Contains("username=fritz1234", body);
                return (HttpStatusCode.OK, $"<SessionInfo><SID>abcdef012345678{logins}</SID></SessionInfo>");
            }
            if (expired) { expired = false; return (HttpStatusCode.Forbidden, ""); }   // erste Sitzung gleich abgelaufen
            return q.Contains("getswitchpower") ? (HttpStatusCode.OK, "45210\n") : (HttpStatusCode.OK, "8800\n");
        });
        using var client = new FritzDectClient("fritz.box", "11657 0123456", null, "1example!", fake);
        var r = await client.ReadAsync(0);
        Assert.Equal((45.21, 8800d), (r.PowerW, r.EnergyWh!.Value));
        Assert.Equal(2, logins);                                                   // nach 403 neu angemeldet
        Assert.Contains(fake.Asked, a => a.Contains("ain=116570123456"));          // AIN ohne Leerzeichen
        Assert.Throws<LocalizedException>(() => new FritzDectClient("fritz.box", " ", null, "x"));
    }
}
