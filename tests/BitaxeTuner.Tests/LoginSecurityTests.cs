using BitaxeTuner.Core.Config;
using BitaxeTuner.Server.Security;

namespace BitaxeTuner.Tests;

/// <summary>Funde S6 und S7 aus dem Audit vom 02.10.2026: Ansicht-PIN und Anmeldung.</summary>
[Collection("SecretScope")]
public class LoginSecurityTests
{
    [Fact]
    public void New_pins_are_salted_old_ones_keep_working()
    {
        var hash = WebViewSettings.HashPin("246813");
        Assert.StartsWith("pbkdf2-", hash);
        Assert.NotEqual(hash, WebViewSettings.HashPin("246813"));                 // mit Salz: jedes Mal anders
        Assert.True(WebViewSettings.VerifyPin("246813", hash));
        Assert.False(WebViewSettings.VerifyPin("246814", hash));

        // bisheriges Format (SHA-256 mit festem Präfix) bleibt gültig, gilt aber als „alt“
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("bitaxetuner|4711")));
        Assert.True(WebViewSettings.VerifyPin("4711", legacy));
        Assert.False(WebViewSettings.VerifyPin("4712", legacy));
        Assert.True(new WebViewSettings { PinHash = legacy }.PinIsLegacy);
        Assert.False(new WebViewSettings { PinHash = hash }.PinIsLegacy);
        Assert.False(WebViewSettings.VerifyPin("", hash));
        Assert.False(WebViewSettings.VerifyPin("246813", ""));
    }

    [Theory]
    [InlineData("4711", false)]
    [InlineData("12345", false)]
    [InlineData("123456", true)]
    [InlineData("123456789012", true)]
    [InlineData("1234567890123", false)]
    [InlineData("12345a", false)]
    public void New_pins_need_six_to_twelve_digits(string pin, bool ok) =>
        Assert.Equal(ok, WebViewSettings.ValidateNewPin(pin) is null);

    [Fact]
    public void Pin_hash_is_kept_out_of_config_json()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        var c = new AppConfig();
        c.WebView.PinHash = WebViewSettings.HashPin("246813");
        c.Save(file);
        Assert.DoesNotContain("pbkdf2-", File.ReadAllText(file));
        Assert.True(WebViewSettings.VerifyPin("246813", AppConfig.Load(file).WebView.PinHash));
    }

    [Fact]
    public void Many_failures_from_anywhere_slow_down_further_logins()
    {
        var lockout = new Lockout();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++) lockout.FailGlobal(now);
        Assert.Equal(TimeSpan.Zero, lockout.GlobalDelay(now));                   // bis 10 Fehlversuche: keine Bremse
        for (var i = 0; i < 4; i++) lockout.FailGlobal(now);
        Assert.Equal(TimeSpan.FromSeconds(2), lockout.GlobalDelay(now));
        for (var i = 0; i < 50; i++) lockout.FailGlobal(now);
        Assert.Equal(TimeSpan.FromSeconds(5), lockout.GlobalDelay(now));          // höchstens 5 s
        Assert.Equal(TimeSpan.Zero, lockout.GlobalDelay(now + Lockout.LockTime + TimeSpan.FromSeconds(1)));   // nach 5 min vorbei
    }
}
