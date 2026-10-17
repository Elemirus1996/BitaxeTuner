using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

public sealed record ChatBotRequest(ChatBotSettings Settings, string? DiscordToken);
public sealed record DiscordSetupRequest(string? Token, string? GuildId);
public sealed record ChatPreviewRequest(string Command);

/// <summary>0.9.24: Chat-Bots (Telegram, Discord) einstellen, Discord einrichten, Antworten vorab ansehen. Nur Admin.</summary>
public static class ChatBotEndpoints
{
    private static object Status(MinerHub h)
    {
        var s = h.Config.ChatBot;
        var (tg, dc) = h.ChatBotStatus;
        return new
        {
            settings = Dto.Copy(s),
            discordTokenSet = h.Secrets.Has(MinerHub.DiscordTokenKey),
            telegramTargets = h.Config.Notifications.EffectiveTargets().Where(t => t.Provider == "telegram")
                .Select(t => new { id = t.Id, name = t.Title, ready = t.TelegramBotToken.Length > 0 && t.TelegramChatId.Length > 0 }).ToList(),
            telegramStatus = tg,
            discordStatus = dc,
            inviteUrl = s.DiscordAppId.Length > 0
                ? $"https://discord.com/oauth2/authorize?client_id={s.DiscordAppId}&scope=bot%20applications.commands&permissions={Core.Chat.DiscordBot.Permissions}" +
                  (s.DiscordGuildId.Length > 0 ? $"&guild_id={s.DiscordGuildId}&disable_guild_select=true" : "")
                : null,
            commands = MinerHub.ChatCommandNames.Select(c => new { name = c.Name, de = c.De, description = MinerHub.ChatCommandDescription(c.Name) }).ToList(),
        };
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/chatbot", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        g.MapPut("/chatbot", async (ChatBotRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var s = req.Settings ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            s.Normalize();
            if (s.TelegramEnabled && h.Config.Notifications.EffectiveTargets().All(t => t.Id != s.TelegramTargetId || t.Provider != "telegram"))
                throw new LocalizedException("Bitte den Telegram-Push-Dienst wählen, dessen Bot antworten soll.");
            if (s.DiscordEnabled && s.DiscordChannelId.Length == 0)
                throw new LocalizedException("Bitte die Kanal-ID eintragen (in Discord: Rechtsklick auf den Kanal → „Kanal-ID kopieren“).");
            // Merker vom Einrichten bleiben
            if (s.DiscordAppId.Length == 0) s.DiscordAppId = h.Config.ChatBot.DiscordAppId;
            if (!string.IsNullOrWhiteSpace(req.DiscordToken)) h.Secrets.Set(MinerHub.DiscordTokenKey, req.DiscordToken.Trim());
            h.Config.ChatBot = s;
            h.Config.Save();
            h.LogEvent(null, Core.Monitoring.EventCategories.Settings, L.T("Chat-Bots gespeichert (Telegram {0}, Discord {1}).",
                s.TelegramEnabled ? L.T("an") : L.T("aus"), s.DiscordEnabled ? L.T("an") : L.T("aus")));
            return Status(h);
        })));

        g.MapPost("/chatbot/discord/setup", async (DiscordSetupRequest req, HubService hub, IWebHostEnvironment env) =>
        {
            byte[]? avatar = null;
            var icon = Path.Combine(env.WebRootPath ?? "", "icon-512.png");
            if (File.Exists(icon)) avatar = await File.ReadAllBytesAsync(icon);
            var r = await hub.RunAsync(h => h.SetupDiscordAsync(req.Token, req.GuildId, avatar));
            return Results.Json(new { ok = true, r.AppId, r.BotName, r.InviteUrl, r.Commands });
        });

        // Antwort vorab ansehen (ohne Telegram/Discord)
        g.MapPost("/chatbot/preview", async (ChatPreviewRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = h.AnswerChat(req.Command ?? "/help", DateTime.Now);
            return new { text = r.Text, image = r.Png is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : null };
        })));
    }
}
