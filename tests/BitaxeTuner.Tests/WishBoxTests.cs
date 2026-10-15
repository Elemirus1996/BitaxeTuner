using System.Text.Json;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;

namespace BitaxeTuner.Tests;

/// <summary>0.9.16: „Wunsch einreichen“ ohne GitHub-Konto.</summary>
public class WishBoxTests
{
    [Fact]
    public async Task Wish_is_sent_as_json_and_spam_is_slowed_down()
    {
        var sent = new List<string>();
        WishBox.TransportForTest = body => { sent.Add(body); return Task.CompletedTask; };
        WishBox.ResetForTest();
        try
        {
            await Assert.ThrowsAsync<LocalizedException>(() => WishBox.SendAsync("ab", null, "0.9.16", "de"));         // zu kurz
            await Assert.ThrowsAsync<LocalizedException>(() => WishBox.SendAsync(new string('x', 1001), null, "0.9.16", "de"));
            await WishBox.SendAsync("  Mehr Kurse  ", "weil", "0.9.16", "de");
            using (var doc = JsonDocument.Parse(sent.Single()))
            {
                var r = doc.RootElement;
                Assert.Equal(("bitaxetuner", 1, "Mehr Kurse", "weil", "0.9.16", "de"),
                    (r.GetProperty("app").GetString(), r.GetProperty("v").GetInt32(), r.GetProperty("idea").GetString(), r.GetProperty("why").GetString(),
                     r.GetProperty("version").GetString(), r.GetProperty("lang").GetString()));
            }
            await WishBox.SendAsync("Zweiter Wunsch", null, "0.9.16", "de");
            await WishBox.SendAsync("Dritter Wunsch", null, "0.9.16", "de");
            await Assert.ThrowsAsync<LocalizedException>(() => WishBox.SendAsync("Vierter Wunsch", null, "0.9.16", "de"));   // höchstens 3 je 10 min
            Assert.Equal(3, sent.Count);
        }
        finally
        {
            WishBox.TransportForTest = null;
            WishBox.ResetForTest();
        }
    }
}
