using BitaxeTuner.Server.Api;

namespace BitaxeTuner.Tests;

public class MinerWebUrlTests
{
    [Theory]
    [InlineData("10.0.0.5", "http://10.0.0.5/")]
    [InlineData("bitaxe.local", "http://bitaxe.local/")]
    [InlineData("10.0.0.5:8080", "http://10.0.0.5:8080/")]
    [InlineData("https://miner.example/", "https://miner.example/")]
    [InlineData(" HTTP://10.0.0.6 ", "http://10.0.0.6/")]
    public void Builds_a_link_to_the_miner_web_interface(string host, string expected) =>
        Assert.Equal(expected, Dto.MinerWebUrl(host));

    [Theory]
    [InlineData("javascript://%0aalert(1)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/windows")]
    [InlineData("")]
    public void Never_links_anything_but_http(string host)
    {
        var url = Dto.MinerWebUrl(host);
        Assert.True(url is null || url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal), url);
    }
}
