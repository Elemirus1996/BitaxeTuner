using System.IO.Compression;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Tests;

/// <summary>Fund P2 aus dem Audit vom 02.10.2026: Tokens nicht im Klartext in config.json (und damit nicht in Sicherungen).</summary>
[Collection("SecretScope")]
public class ConfigSecretsTests
{
    private const string Telegram = "123456:telegram-token-test";
    private const string Tibber = "tibber-token-test-0815";

    private static AppConfig Sample()
    {
        var c = new AppConfig();
        c.Notifications.Targets.Add(new PushTarget { TelegramBotToken = Telegram, NtfyTopic = "bt-test-topic" });
        c.PriceSource.TibberToken = Tibber;
        c.BlockchairApiKey = "blockchair-key-test";
        c.Server.Token = "btk_desktop-token-test";
        return c;
    }

    [Fact]
    public void Tokens_go_to_secrets_json_and_come_back_on_load()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        Sample().Save(file);

        var text = File.ReadAllText(file);
        foreach (var secret in new[] { Telegram, Tibber, "bt-test-topic", "blockchair-key-test", "btk_desktop-token-test" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains(ConfigSecrets.RefPrefix, text);
        Assert.DoesNotContain(Telegram, File.ReadAllText(Path.Combine(dir.Path, "secrets.json")));   // auch dort nicht im Klartext (Windows)

        var back = AppConfig.Load(file);
        Assert.Equal(Telegram, back.Notifications.Targets[0].TelegramBotToken);
        Assert.Equal("bt-test-topic", back.Notifications.Targets[0].NtfyTopic);
        Assert.Equal(Tibber, back.PriceSource.TibberToken);
        Assert.Equal("btk_desktop-token-test", back.Server.Token);
        Assert.Empty(back.UnresolvedSecrets);
    }

    [Fact]
    public void Old_plain_config_is_migrated_without_losing_anything()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        // bisheriges Format: alles im Klartext
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(Sample()));
        var loaded = AppConfig.Load(file);
        Assert.Equal(Telegram, loaded.Notifications.Targets[0].TelegramBotToken);
        loaded.Save(file);
        Assert.DoesNotContain(Telegram, File.ReadAllText(file));
        Assert.Equal(Telegram, AppConfig.Load(file).Notifications.Targets[0].TelegramBotToken);
    }

    [Fact]
    public void Unresolvable_reference_survives_saving()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "config.json");
        Sample().Save(file);
        File.Delete(Path.Combine(dir.Path, "secrets.json"));          // z. B. Sicherung auf einem anderen Rechner eingespielt
        var loaded = AppConfig.Load(file);
        Assert.Equal("", loaded.PriceSource.TibberToken);
        Assert.NotEmpty(loaded.UnresolvedSecrets);
        var before = File.ReadAllText(file);
        loaded.Save(file);
        // die Verweise bleiben stehen (z. B. secrets.json kommt zurück) statt still gelöscht zu werden
        foreach (var r in loaded.UnresolvedSecrets.Values) Assert.Contains(r, File.ReadAllText(file));
        Assert.Contains(ConfigSecrets.RefPrefix, before);
    }

    [Fact]
    public void Backup_archive_has_no_tokens_but_transfer_archive_keeps_them()
    {
        using var dir = new TempDir();
        Sample().Save(Path.Combine(dir.Path, "config.json"));
        string ConfigIn(string source)
        {
            using var ms = new MemoryStream();
            DataArchive.Create(dir.Path, null, ms, source, "0.0.0");
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            using var r = new StreamReader(zip.Entries.First(e => e.FullName.EndsWith("config.json")).Open());
            return r.ReadToEnd();
        }
        Assert.DoesNotContain(Telegram, ConfigIn("backup"));
        Assert.Contains(Telegram, ConfigIn("desktop"));
        Assert.DoesNotContain(Telegram, File.ReadAllText(Path.Combine(dir.Path, "config.json")));   // Original unverändert
    }
}
