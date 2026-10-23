using System.Text.Json;
using BitaxeTuner.Core.Chat;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Tests;

/// <summary>0.9.26: Steuern per Chat – jede Rechtestufe, Bestätigung, Protokoll.</summary>
public class ChatControlTests
{
    private static readonly ChatActor Alice = new("Discord", "111", "@alice", true);
    private static readonly ChatActor Bob = new("Discord", "222", "@bob", true);
    private static readonly ChatActor Reader = new("Discord", "333", "@leser", false);

    private static MinerHub Hub(TempDir dir, bool control = true)
    {
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig { FilePath = Path.Combine(dir.Path, "config.json") };
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.26.1", ProfileId = "bitaxe-gamma", MonitorOnly = false,
            Presets = [new TuningPreset("Sparsam", 500, 1150)] });
        config.ChatBot.ControlEnabled = control;
        return new MinerHub(config, new MinerHubOptions { DataDirectory = dir.Path, OnlineChecks = false, ClientFactory = h => new SimulatedMinerClient(gamma, 1, h) });
    }

    private static string Token(ChatReply r) => Assert.Single(r.Buttons!, b => b.Command.StartsWith("x:")).Command[2..];

    [Fact]
    public async Task Without_switch_or_rights_nothing_happens()
    {
        using var dir = new TempDir();
        using (var off = Hub(dir, control: false))
            Assert.Contains("ausgeschaltet", (await off.AnswerChatAsAsync("/restart Gamma", Alice, DateTime.Now)).Text);
        using var dir2 = new TempDir();
        using var hub = Hub(dir2);
        var r = await hub.AnswerChatAsAsync("/neustart Gamma", Reader, DateTime.Now);
        Assert.Contains("BitaxeTuner-Admin", r.Text);
        Assert.Null(r.Buttons);
    }

    [Fact]
    public async Task Restart_needs_confirmation_by_the_same_user_within_two_minutes()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        var now = DateTime.Now;
        var ask = await hub.AnswerChatAsAsync("/restart gam", Alice, now);
        Assert.Contains("wirklich neu starten", ask.Text);
        var token = Token(ask);
        Assert.Contains(ask.Buttons!, b => b.Command == "n:" + token);

        Assert.Contains("nur, wer gefragt hat", (await hub.AnswerChatAsAsync("!ok " + token, Bob, now)).Text);
        Assert.Contains("startet neu", (await hub.AnswerChatAsAsync("!ok " + token, Alice, now.AddSeconds(30))).Text);
        Assert.Contains(hub.Devices[0].LogLines, l => l.Contains("ausgelöst von Discord @alice"));
        Assert.Contains("gibt es nicht mehr", (await hub.AnswerChatAsAsync("!ok " + token, Alice, now)).Text);   // nur einmal

        var late = Token(await hub.AnswerChatAsAsync("/restart Gamma", Alice, now));
        Assert.Contains("Zu spät", (await hub.AnswerChatAsAsync("!ok " + late, Alice, now.AddMinutes(3))).Text);
        var cancel = Token(await hub.AnswerChatAsAsync("/restart Gamma", Alice, now));
        Assert.Contains("Abgebrochen", (await hub.AnswerChatAsAsync("!no " + cancel, Alice, now)).Text);
    }

    [Fact]
    public async Task Only_saved_presets_and_rights_are_checked_again_on_confirm()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        var now = DateTime.Now;
        Assert.Contains("Gespeichert: Sparsam", (await hub.AnswerChatAsAsync("/preset Gamma | 600 MHz", Alice, now)).Text);   // keine freien Werte
        var ask = await hub.AnswerChatAsAsync("/voreinstellung Gamma | sparsam", Alice, now);
        Assert.Contains("500 MHz", ask.Text);
        var applied = new List<int>();
        hub.TuningApplied += e => applied.Add(e.NewFrequencyMhz);
        Assert.Contains("angewendet", (await hub.AnswerChatAsAsync("!ok " + Token(ask), Alice, now)).Text);
        Assert.Equal([500], applied);

        var again = Token(await hub.AnswerChatAsAsync("/preset Gamma | Sparsam", Alice, now));
        hub.Config.ChatBot.ControlEnabled = false;                         // inzwischen ausgeschaltet
        Assert.Contains("Berechtigung", (await hub.AnswerChatAsAsync("!ok " + again, Alice, now)).Text);
        Assert.Single(applied);
    }

    [Fact]
    public async Task Maintenance_pause_and_upkeep_are_logged_with_the_user()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        var now = DateTime.Now;
        var d = hub.Devices[0];
        var ask = await hub.AnswerChatAsAsync("/wartung Gamma | an | 2 | Abstauben", Alice, now);
        Assert.Contains("Abstauben", ask.Text);
        await hub.AnswerChatAsAsync("!ok " + Token(ask), Alice, now);
        Assert.True(d.Config.MaintenanceMode);
        Assert.NotNull(d.Config.MaintenanceUntil);

        bool? paused = null;
        hub.PauseHandler = p => { paused = p; return Task.CompletedTask; };
        await hub.AnswerChatAsAsync("!ok " + Token(await hub.AnswerChatAsAsync("/pause", Alice, now)), Alice, now);
        Assert.True(paused);

        var done = await hub.AnswerChatAsAsync("!done Gamma | dust", Alice, now);
        await hub.AnswerChatAsAsync("!ok " + Token(done), Alice, now);
        Assert.True(d.Config.Upkeep!.Done.ContainsKey("dust"));
        Assert.Contains(d.LogLines, l => l.Contains("ausgelöst von Discord @alice"));
    }

    [Fact]
    public async Task Miner_details_get_control_buttons_only_for_operators()
    {
        using var dir = new TempDir();
        using var hub = Hub(dir);
        await hub.PollNowAsync();
        var now = DateTime.Now;
        var op = await hub.AnswerChatAsAsync("/miner Gamma", Alice, now);
        Assert.Contains(op.Buttons!, b => b.Command == "r:Gamma");
        Assert.Contains(op.Buttons!, b => b.Command == "mm:Gamma");
        Assert.Null((await hub.AnswerChatAsAsync("/miner Gamma", Reader, now)).Buttons);

        hub.Config.ChatBot.AlertButtons = true;
        hub.OnChatAlert("offline:10.0.26.1", "Gamma offline", "10.0.26.1");
        Assert.Contains(hub.ChatAlertControlsPosted.Single(), b => b.Command == "r:Gamma");
    }

    [Fact]
    public void Buttons_map_to_commands_on_both_platforms()
    {
        Assert.Equal("!ok abc123", MinerHub.ButtonToCommand("x:abc123"));
        Assert.Equal("/restart Gamma", DiscordBot.ButtonCommand("bt:r:Gamma"));
        Assert.Equal("/status", DiscordBot.ButtonCommand("bt:s"));
        Assert.Equal("!done Gamma | dust", TelegramBot.ButtonCommand("u:Gamma|dust"));
        Assert.Equal("/maintenance Gamma | an | 2", TelegramBot.ButtonCommand("mm:Gamma"));   // Telegram: keine Eingabemaske
        Assert.Null(MinerHub.ButtonToCommand("bt:x"));
    }

    [Fact]
    public void Telegram_operators_only_count_in_private_chats()
    {
        using var dir = new TempDir();
        using var bot = new TelegramBot("1:a", "4711", null, _ => Task.FromResult(new ChatReply("")), () => Task.FromResult(""), null, Path.Combine(dir.Path, "tg.json"))
        {
            Operators = new HashSet<string> { "42" },
        };
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.True(bot.ActorOf(J("""{"id":42,"username":"paul"}"""), J("""{"id":42,"type":"private"}""")).CanOperate);
        Assert.False(bot.ActorOf(J("""{"id":42}"""), J("""{"id":-100,"type":"group"}""")).CanOperate);
        Assert.False(bot.ActorOf(J("""{"id":7}"""), J("""{"id":7,"type":"private"}""")).CanOperate);
    }

    [Fact]
    public async Task Discord_checks_the_admin_role_and_hides_control_commands()
    {
        var roles = """[{"id":"900","name":"BitaxeTuner-Admin"},{"id":"901","name":"Gäste"}]""";
        using var http = new HttpClient();
        var fake = new FakeHandler(roles);
        using var dir = new TempDir();
        using var bot = new DiscordBot("tok", "555", fake, _ => Task.FromResult(new ChatReply("")), () => Task.FromResult(""), null, Path.Combine(dir.Path, "dc.txt"));
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        Assert.True((await bot.ActorOfAsync(J("""{"guild_id":"1","member":{"roles":["900"],"user":{"id":"5","username":"alice"}}}"""), default)).CanOperate);
        Assert.False((await bot.ActorOfAsync(J("""{"guild_id":"1","member":{"roles":["901"],"user":{"id":"6","username":"bob"}}}"""), default)).CanOperate);
        Assert.False((await bot.ActorOfAsync(J("""{"user":{"id":"7"}}"""), default)).CanOperate);   // Direktnachricht ohne Mitglied

        var setup = new FakeHandler("""{"id":"112233"}""");
        await DiscordBot.SetupAsync("tok", "445566", null, setup);
        using var doc = JsonDocument.Parse(setup.LastPut!);
        var restart = doc.RootElement.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "restart");
        Assert.Equal("0", restart.GetProperty("default_member_permissions").GetString());
        Assert.Equal("neustart", restart.GetProperty("name_localizations").GetProperty("de").GetString());
    }

    private sealed class FakeHandler(string answer) : HttpMessageHandler
    {
        public string? LastPut;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put && request.Content is not null) LastPut = await request.Content.ReadAsStringAsync(ct);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/commands") ? "[]" : answer;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
