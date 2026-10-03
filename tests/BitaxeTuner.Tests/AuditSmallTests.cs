using System.Net;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Tax;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.Transfer;
using BitaxeTuner.Server;

namespace BitaxeTuner.Tests;

/// <summary>Kleinpunkte aus dem Audit vom 02.10.2026 (S5, S9, L1, F4–F6, I2, E3, E4).</summary>
public class AuditSmallTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir!, .. parts]);
    }

    [Fact]
    public void Tibber_quarter_hour_prices_end_at_the_next_start()
    {
        var json = """
            {"data":{"viewer":{"homes":[{"currentSubscription":{"priceInfo":{"today":[
              {"total":0.30,"startsAt":"2026-10-03T10:00:00.000+02:00"},
              {"total":0.20,"startsAt":"2026-10-03T10:15:00.000+02:00"},
              {"total":0.10,"startsAt":"2026-10-03T10:30:00.000+02:00"}],"tomorrow":[]}}}]}}}
            """;
        var list = TibberPriceSource.Parse(json);
        Assert.All(list, p => Assert.Equal(TimeSpan.FromMinutes(15), p.EndUtc - p.StartUtc));   // auch der letzte
        Assert.Equal(list[1].StartUtc, list[0].EndUtc);                                         // keine Überlappung
    }

    [Fact]
    public void Holding_period_uses_the_tax_time_zone_not_the_host()
    {
        try
        {
            TaxTime.Configure("Europe/Berlin");
            // 31.12. 23:30 UTC ist in Berlin schon der 1. Januar – zählt zum neuen Jahr
            var r = new MinedReward { ReceivedAtUtc = new DateTime(2025, 12, 31, 23, 30, 0, DateTimeKind.Utc), Amount = 1m };
            Assert.Equal(new DateTime(2026, 1, 1), r.ReceivedAtLocal.Date);
            Assert.Equal(new DateTime(2027, 1, 2), r.TaxFreeFrom);
            Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), TaxTime.FromTax(new DateTime(2026, 6, 1, 12, 0, 0)));   // Sommerzeit
            TaxTime.Configure("gibt-es-nicht");
            Assert.Equal(new DateTime(2026, 1, 1), r.ReceivedAtLocal.Date);                       // unbekannt → Europe/Berlin
        }
        finally { TaxTime.Configure(TaxTime.DefaultZone); }
    }

    [Fact]
    public void Gain_without_cost_basis_is_reported_separately()
    {
        var now = DateTime.UtcNow;
        var rewards = new List<MinedReward>
        {
            new() { Coin = CoinType.Bitcoin, Amount = 0.001m, ReceivedAtUtc = now.AddDays(-20), EurPriceAtReceipt = 50000m, TxId = "a" },
            new() { Coin = CoinType.Bitcoin, Amount = 0.001m, ReceivedAtUtc = now.AddDays(-10), EurPriceAtReceipt = null, TxId = "b" },
        };
        var sale = new Disposal { Coin = CoinType.Bitcoin, Amount = 0.003m, ProceedsEur = 300m, SoldAtUtc = now };
        var r = HoldingCalculator.Apply(rewards, [sale]).Single();
        Assert.Equal(250m, Math.Round(r.TaxableGainEur, 2));   // (100 − 50) + 100 ohne Kurs + 100 ohne Zufluss
        Assert.Equal(200m, Math.Round(r.UncertainGainEur, 2));  // davon unsicher
    }

    private sealed class Mempool429 : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task Mempool_429_pauses_all_further_requests()
    {
        MempoolLimit.Reset();
        try
        {
            var handler = new Mempool429();
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<HttpRequestException>(() => MempoolLimit.GetAsync(http, "https://mempool.space/api/v1/blocks", default));
            Assert.True(MempoolLimit.BlockedUntilUtc > DateTime.UtcNow.AddSeconds(100));
            await Assert.ThrowsAsync<HttpRequestException>(() => MempoolLimit.GetAsync(http, "https://mempool.space/api/address/x", default));
            Assert.Equal(1, handler.Calls);                                                      // zweite Anfrage gar nicht gesendet
        }
        finally { MempoolLimit.Reset(); }
    }

    [Fact]
    public void Import_needs_enough_free_space()
    {
        using var dir = new TempDir();
        Assert.NotNull(DiskSpace.FreeBytes(dir.Path));
        DiskSpace.Require(dir.Path, 1024);                                                       // passt
        var ex = Assert.Throws<LocalizedException>(() => DiskSpace.Require(dir.Path, long.MaxValue / 4));
        Assert.Equal(507, ex.Status);
    }

    [Theory]
    [InlineData("192.168.1.5", 1)]
    [InlineData("10.0.0.2, 10.0.0.3", 2)]
    [InlineData("kein-ip", 0)]
    [InlineData(null, 0)]
    public void Trusted_proxies_are_parsed(string? value, int count) =>
        Assert.Equal(count, ServerSettings.ParseProxies(value).Count);

    [Fact]
    public void Linux_update_switches_versions_active_in_the_new_layout()
    {
        if (!OperatingSystem.IsLinux()) return;   // Symlinks: nur unter Linux (CI unter Windows überspringt)
        using var dir = new TempDir();
        var versions = Directory.CreateDirectory(Path.Combine(dir.Path, "versions")).FullName;
        Directory.CreateDirectory(Path.Combine(versions, "0.9.7"));
        var target = Path.Combine(versions, "0.9.8");
        Assert.Equal((Path.Combine(dir.Path, "current"), target), ServerUpdater.SwapLink(dir.Path, "0.9.8", target));   // altes Layout
        File.CreateSymbolicLink(Path.Combine(versions, "active"), "0.9.7");
        Assert.Equal((Path.Combine(versions, "active"), "0.9.8"), ServerUpdater.SwapLink(dir.Path, "0.9.8", target));
    }

    [Fact]
    public void Every_package_is_listed_in_the_third_party_notices_and_pinned()
    {
        var notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.txt"));
        foreach (var csproj in Directory.GetFiles(RepoFile("src"), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(csproj), "<PackageReference Include=\"([^\"]+)\" Version=\"([^\"]+)\""))
            {
                var name = m.Groups[1].Value;
                Assert.DoesNotContain("*", m.Groups[2].Value);                                  // feste Version (Audit L1)
                var parts = name.Split('.');
                var listed = Enumerable.Range(2, Math.Max(0, parts.Length - 1)).Select(n => string.Join('.', parts.Take(n)))
                    .Append(name).Any(prefix => notices.Contains(prefix, StringComparison.OrdinalIgnoreCase));
                Assert.True(listed, $"{name} ({Path.GetFileName(csproj)}) fehlt in THIRD-PARTY-NOTICES.txt");
            }
        }
    }

    [Fact]
    public void Docker_compose_points_to_the_current_version()
    {
        var version = Regex.Match(File.ReadAllText(RepoFile("Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
        var compose = File.ReadAllText(RepoFile("deploy", "docker", "docker-compose.yml"));
        Assert.Contains($"bitaxetuner-server:{version}", compose);                             // Audit E3: kein ungeprüftes :latest
        Assert.Contains("mem_limit", compose);
    }
}
