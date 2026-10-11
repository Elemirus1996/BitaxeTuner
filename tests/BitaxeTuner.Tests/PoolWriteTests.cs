using System.Text.Json.Nodes;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Tests;

public class PoolWriteTests
{
    // Wie ESP-Miner 2.15.3 (Gamma): Liste mit allen Feldern, die alten Felder spiegeln nur Haupt-/Ersatz-Pool
    private const string Indexed = """
        {"primaryPoolIndex":1,"secondaryPoolIndex":0,
         "pools":[{"id":0,"stratumURL":"b.example","stratumPort":4444,"stratumUser":"w.alt","stratumPassword":"*****","stratumTLS":1,"stratumProtocol":"SV1"},
                  {"id":1,"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.alt","stratumPassword":"*****","stratumTLS":0,"stratumSuggestedDifficulty":1000}],
         "stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.alt",
         "fallbackStratumURL":"b.example","fallbackStratumPort":4444,"fallbackStratumUser":"w.alt"}
        """;

    [Fact]
    public void Indexed_firmware_gets_pool_entries_instead_of_legacy_fields()
    {
        var patch = PoolWrite.Adapt(Indexed, new Dictionary<string, object>
        {
            ["stratumURL"] = "c.example", ["stratumPort"] = 5555, ["stratumUser"] = "w.neu",
            ["fallbackStratumUser"] = "w.neu", ["manualFanSpeed"] = 50,
        });
        Assert.DoesNotContain(patch.Keys, k => k.StartsWith("stratum") || k.StartsWith("fallbackStratum"));
        Assert.Equal(50, patch["manualFanSpeed"]);
        var pools = Assert.IsType<JsonArray>(patch["pools"]);
        var primary = pools.OfType<JsonObject>().Single(p => (int)p["id"]! == 1);
        Assert.Equal("c.example", (string)primary["stratumURL"]!);
        Assert.Equal(5555, (int)primary["stratumPort"]!);
        Assert.Equal("w.neu", (string)primary["stratumUser"]!);
        Assert.Equal(1000, (int)primary["stratumSuggestedDifficulty"]!);   // übrige Felder bleiben
        Assert.Equal("*****", (string)primary["stratumPassword"]!);         // Passwort bleibt auf dem Gerät
        var fallback = pools.OfType<JsonObject>().Single(p => (int)p["id"]! == 0);
        Assert.Equal("b.example", (string)fallback["stratumURL"]!);
        Assert.Equal("w.neu", (string)fallback["stratumUser"]!);
        Assert.Equal(1, (int)fallback["stratumTLS"]!);
        Assert.False(patch.ContainsKey("primaryPoolIndex"));
    }

    [Fact]
    public void Missing_fallback_slot_uses_a_free_index()
    {
        const string raw = """
            {"primaryPoolIndex":0,"secondaryPoolIndex":0,
             "pools":[{"id":0,"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1"},{"id":1,"stratumURL":"","stratumPort":3333,"stratumUser":""}]}
            """;
        var patch = PoolWrite.Adapt(raw, new Dictionary<string, object>
        {
            ["fallbackStratumURL"] = "b.example", ["fallbackStratumPort"] = 4444, ["fallbackStratumUser"] = "w.1",
        });
        Assert.Equal(1, patch["secondaryPoolIndex"]);
        var entry = Assert.IsType<JsonArray>(patch["pools"]).OfType<JsonObject>().Single();
        Assert.Equal(1, (int)entry["id"]!);
        Assert.Equal("b.example", (string)entry["stratumURL"]!);
    }

    [Fact]
    public void Legacy_firmware_and_patches_without_pool_fields_stay_unchanged()
    {
        const string legacy = """{"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1"}""";
        var patch = PoolWrite.Adapt(legacy, new Dictionary<string, object> { ["stratumURL"] = "c.example" });
        Assert.Equal("c.example", patch["stratumURL"]);
        Assert.False(patch.ContainsKey("pools"));

        var fan = PoolWrite.Adapt(Indexed, new Dictionary<string, object> { ["autofanspeed"] = 1 });
        Assert.Single(fan);
    }

    [Fact]
    public void Empty_user_is_rejected_on_indexed_firmware()
    {
        Assert.Throws<InvalidOperationException>(() => PoolWrite.Adapt(Indexed, new Dictionary<string, object> { ["stratumUser"] = "" }));
    }

    [Fact]
    public void Decode_coinbase_goes_into_the_pool_entry_or_only_to_firmware_that_knows_it()
    {
        var patch = PoolWrite.Adapt(Indexed, new Dictionary<string, object> { ["stratumDecodeCoinbase"] = false });
        var entry = Assert.IsType<JsonArray>(patch["pools"]).OfType<JsonObject>().Single();
        Assert.Equal(1, (int)entry["id"]!);
        Assert.False((bool)entry["stratumDecodeCoinbase"]!);
        Assert.Equal("a.example", (string)entry["stratumURL"]!);              // Rest des Eintrags bleibt

        const string legacyWith = """{"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1","stratumDecodeCoinbase":true}""";
        Assert.Equal(false, PoolWrite.Adapt(legacyWith, new Dictionary<string, object> { ["stratumDecodeCoinbase"] = false })["stratumDecodeCoinbase"]);
        const string legacyWithout = """{"stratumURL":"a.example","stratumPort":3333,"stratumUser":"w.1"}""";
        Assert.Empty(PoolWrite.Adapt(legacyWithout, new Dictionary<string, object> { ["stratumDecodeCoinbase"] = false }));
    }
}
