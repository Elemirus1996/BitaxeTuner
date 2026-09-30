using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Tests;

public class LogCategoryTests
{
    [Theory]
    [InlineData("I (35451) asic_result: Ver: 20000000 Nonce 1A2B3C4D diff 2345.6 of 1024.", LogCategory.Shares)]
    [InlineData("I (35460) stratum_task: message result accepted", LogCategory.Shares)]
    [InlineData("W (35470) stratum_task: message result rejected: Duplicate share", LogCategory.Shares)]
    [InlineData("I (1200) stratum_task: Set pool difficulty: 1024", LogCategory.Pool)]
    [InlineData("I (1300) stratum_api: rx: {\"method\":\"mining.notify\",\"params\":[]}", LogCategory.Pool)]
    [InlineData("I (5000) fan_controller: Temp: 48.7°C, Fan: 55%", LogCategory.Thermal)]
    [InlineData("I (5001) power_management: VR: 52.0°C, Power: 15.2W", LogCategory.Thermal)]
    [InlineData("I (6000) create_jobs_task: New Work Dequeued 1a2b", LogCategory.Asic)]
    [InlineData("I (6001) bm1370Module: Setting Frequency to 525.00MHz", LogCategory.Asic)]
    [InlineData("I (100) wifi_rssi: Signal -58 dBm", LogCategory.System)]
    [InlineData("I (101) http_server: Starting HTTP server", LogCategory.System)]
    [InlineData("I (102) mystery: something", LogCategory.Other)]
    public void Lines_get_the_expected_category(string raw, LogCategory expected) =>
        Assert.Equal(expected, LogParser.Parse(raw, DateTime.Now).Category);

    [Fact]
    public void App_lines_count_as_other() =>
        Assert.Equal(LogCategory.Other, new LogLine(DateTime.Now, LogLevel.App, null, "Tuning", "── accepted ──").Category);

    [Fact]
    public void Every_category_has_a_label() =>
        Assert.All(LogCategories.All, c => Assert.False(string.IsNullOrWhiteSpace(LogCategories.Label(c))));
}
