namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.24 Chat-Bots (nur lesend): Befehle wie /status in Telegram und Discord, angepinnte Live-Statusnachricht.
/// Der Bot läuft in BitaxeTuner selbst (nur ausgehende Verbindungen, kein zentraler Bot). Telegram nutzt Bot-Token und
/// Chat-ID eines vorhandenen Push-Ziels; das Discord-Bot-Token liegt nur in secrets.json („ChatBot.DiscordToken“).
/// Gespeichert in config.json („ChatBot“, additiv), standardmäßig aus.
/// </summary>
public sealed class ChatBotSettings
{
    public bool TelegramEnabled { get; set; }
    /// <summary>Id des Push-Ziels (Anbieter „telegram“), dessen Bot-Token und Chat-ID benutzt werden.</summary>
    public string TelegramTargetId { get; set; } = "";

    public bool DiscordEnabled { get; set; }
    /// <summary>Kanal, in dem der Bot antwortet und die Live-Nachricht pflegt.</summary>
    public string DiscordChannelId { get; set; } = "";
    /// <summary>Server (Guild) für die Slash-Befehle – damit sind sie sofort da (global dauert es bis zu einer Stunde).</summary>
    public string DiscordGuildId { get; set; } = "";
    /// <summary>Anwendungs-Id (vom Einrichten, für den Einladungslink).</summary>
    public string DiscordAppId { get; set; } = "";

    /// <summary>Angepinnte Nachricht mit dem aktuellen Stand, alle <see cref="LiveMinutes"/> Minuten aktualisiert.</summary>
    public bool LiveStatus { get; set; }
    public int LiveMinutes { get; set; } = 5;

    /// <summary>0.9.23: Discord – unter dem Bot-Namen live „3/3 online · 3,2 TH/s“ (Presence).</summary>
    public bool Presence { get; set; } = true;
    /// <summary>0.9.23: Warnungen (offline, Überhitzung, Lüfter steht) zusätzlich in den Chat – mit Knöpfen [Details] [Verlauf].</summary>
    public bool AlertButtons { get; set; }
    /// <summary>0.9.23: Discord – Antworten auf diese Befehle sieht nur, wer gefragt hat (ephemeral).</summary>
    public List<string> PrivateCommands { get; set; } = ["pool", "power"];

    public void Normalize()
    {
        TelegramTargetId = (TelegramTargetId ?? "").Trim();
        DiscordChannelId = Digits(DiscordChannelId);
        DiscordGuildId = Digits(DiscordGuildId);
        DiscordAppId = Digits(DiscordAppId);
        LiveMinutes = Math.Clamp(LiveMinutes, 2, 60);
        PrivateCommands = (PrivateCommands ?? []).Where(c => Host.MinerHub.ChatCommandNames.Any(x => x.Name == c)).Distinct().ToList();
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());
}
