using System.Net;
using System.Net.Sockets;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Tests;

/// <summary>0.9.21 HTTPS-Assistent: zeigt der DuckDNS-Name auf diesen Server, ist es eine Heimnetz-Adresse?</summary>
public class HttpsDiagnosisTests
{
    private static readonly IPAddress[] Server = [IPAddress.Parse("10.0.14.5"), IPAddress.Parse("fe80::1")];

    [Fact]
    public async Task Name_pointing_to_this_server_is_recognised_as_private()
    {
        var r = await HttpsDiagnosis.CheckAsync("meinminer.duckdns.org", _ => Task.FromResult(new[] { IPAddress.Parse("10.0.14.5") }), Server);
        Assert.True(r.PointsHere);
        Assert.True(r.Private);
        Assert.Equal(["10.0.14.5"], r.Resolved);
        Assert.Equal(["10.0.14.5"], r.ServerIps);                       // nur IPv4
    }

    [Fact]
    public async Task Wrong_address_unresolvable_and_slow_names_are_reported()
    {
        var other = await HttpsDiagnosis.CheckAsync("x.duckdns.org", _ => Task.FromResult(new[] { IPAddress.Parse("10.0.14.9") }), Server);
        Assert.False(other.PointsHere);
        Assert.True(other.Private);

        var none = await HttpsDiagnosis.CheckAsync("x.duckdns.org", _ => throw new SocketException(11001), Server);
        Assert.Empty(none.Resolved);
        Assert.NotNull(none.Error);

        Assert.False((await HttpsDiagnosis.CheckAsync("", _ => throw new InvalidOperationException(), Server)).PointsHere);   // kein Name: keine Abfrage
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.9", true)]
    [InlineData("100.64.1.1", true)]       // Tailscale/CGNAT
    [InlineData("8.8.8.8", false)]
    public void Private_ranges(string ip, bool expected) => Assert.Equal(expected, HttpsDiagnosis.IsPrivate(IPAddress.Parse(ip)));
}
