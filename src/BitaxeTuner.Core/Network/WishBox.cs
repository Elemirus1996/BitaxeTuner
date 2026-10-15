using System.Net.Http;
using System.Text;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

/// <summary>
/// 0.9.16 „Wunsch einreichen“ ohne GitHub-Konto: Der Wunsch geht als kleine JSON-Nachricht an ein festes ntfy.sh-Thema
/// (öffentlicher Briefkasten, rund 12 Stunden zwischengespeichert). Die GitHub-Action „Wunsch-Briefkasten“ holt stündlich
/// ab und legt eine private Entwurfskarte auf dem Projekt-Board an. Mitgeschickt werden nur Text, App-Version und
/// Sprache – keine Kennung, keine Konfiguration. Spam-Bremse: höchstens 3 Wünsche je 10 Minuten.
/// </summary>
public static class WishBox
{
    public const string Topic = "bitaxetuner-wish-jjrd5a2dk5";
    public const string Url = "https://ntfy.sh/" + Topic;
    public const int MaxIdea = 1000;
    public const int MaxWhy = 1000;

    private static readonly Queue<DateTime> Sent = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Für Tests: statt ntfy.sh (Rückgabe = Nachricht).</summary>
    internal static Func<string, Task>? TransportForTest { get; set; }

    /// <summary>Inhalt der Nachricht (so liest ihn die Action).</summary>
    public static string Payload(string idea, string? why, string version, string language) =>
        JsonSerializer.Serialize(new { app = "bitaxetuner", v = 1, idea = idea.Trim(), why = (why ?? "").Trim(), version, lang = language });

    public static async Task SendAsync(string idea, string? why, string version, string language, CancellationToken ct = default)
    {
        idea = (idea ?? "").Trim();
        why = (why ?? "").Trim();
        if (idea.Length < 3) throw new LocalizedException("Bitte beschreibe deinen Wunsch in ein paar Worten.");
        if (idea.Length > MaxIdea || why.Length > MaxWhy) throw new LocalizedException("Bitte kürzer fassen (höchstens {0} Zeichen je Feld).", MaxIdea);
        lock (Sent)
        {
            while (Sent.Count > 0 && DateTime.UtcNow - Sent.Peek() > TimeSpan.FromMinutes(10)) Sent.Dequeue();
            if (Sent.Count >= 3) throw new LocalizedException("Gerade wurden schon mehrere Wünsche gesendet – bitte in ein paar Minuten noch einmal.");
            Sent.Enqueue(DateTime.UtcNow);
        }
        var body = Payload(idea, why, version, language);
        if (TransportForTest is { } t) { await t(body); return; }
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        req.Headers.Add("Title", "BitaxeTuner-Wunsch");
        req.Headers.UserAgent.ParseAdd("BitaxeTuner-wish/1.0");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new LocalizedException("Wunsch konnte nicht gesendet werden ({0}) – bitte später noch einmal oder über GitHub.", (int)resp.StatusCode);
    }

    internal static void ResetForTest()
    {
        lock (Sent) Sent.Clear();
    }
}
