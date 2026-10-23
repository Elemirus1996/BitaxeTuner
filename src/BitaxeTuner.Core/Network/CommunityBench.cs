using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

/// <summary>0.9.26: anonymer Messwert für den Community-Vergleich – keine IP, kein Name, keine Wallet, keine Geräte-ID.</summary>
public sealed record BenchSample(string Model, string? Asic, string? Firmware, int FreqMhz, int CoreMv, double Gh, double Jth, double Temp);

/// <summary>0.9.26: Statistik eines Modells aus der Community (nur zusammengefasst, keine Einzelwerte).</summary>
public sealed record ModelStats(string Model, int N, double[] JthQ, double MedianGh, double MedianTemp, double MedianFreq, double MedianMv)
{
    /// <summary>Anteil der Einsendungen mit schlechterer Effizienz (höherem J/TH) – 0 … 1, aus den Quantilen interpoliert.</summary>
    public double? BetterThan(double jth)
    {
        if (JthQ.Length != 5 || N < CommunityBench.MinSamples) return null;
        double[] p = [0.10, 0.25, 0.50, 0.75, 0.90];
        double share;   // Anteil mit kleinerem oder gleichem J/TH (besser oder gleich)
        if (jth <= JthQ[0]) share = 0.10 * Math.Max(0, jth / JthQ[0]);
        else if (jth >= JthQ[4]) share = 0.90 + 0.10 * Math.Min(1, (jth - JthQ[4]) / Math.Max(0.01, JthQ[4] - JthQ[3]));
        else
        {
            var i = 0;
            while (i < 3 && jth > JthQ[i + 1]) i++;
            var span = Math.Max(1e-9, JthQ[i + 1] - JthQ[i]);
            share = p[i] + (p[i + 1] - p[i]) * (jth - JthQ[i]) / span;
        }
        return Math.Round(Math.Clamp(1 - share, 0, 1), 2);
    }
}

/// <summary>
/// 0.9.26 Benchmark-Vergleich mit der Community (freiwillig je Miner, ohne eigenen Server): Die App schickt höchstens einmal
/// pro Woche je eingeschaltetem Miner die 24-Stunden-Mittelwerte an ein festes ntfy.sh-Thema. Die GitHub-Action
/// „Benchmark-Vergleich“ prüft sie gegen die Grenzen der Geräteprofile, verwirft Ausreißer und veröffentlicht nur Statistiken je
/// Modell (stats.json im Branch „bench“). Einsendungen sind nicht prüfbar echt – ein grober Vergleich.
/// </summary>
public static class CommunityBench
{
    public const string Topic = "bitaxetuner-bench-hgpswdkool";
    public const string Url = "https://ntfy.sh/" + Topic;
    public const string StatsUrl = "https://raw.githubusercontent.com/Elemirus1996/BitaxeTuner/bench/stats.json";
    public const int MinSamples = 5;
    public static readonly TimeSpan ShareInterval = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string Payload(BenchSample s) => JsonSerializer.Serialize(new
    {
        app = "bitaxetuner", v = 1, kind = "bench",
        model = s.Model, asic = s.Asic, fw = s.Firmware, freq = s.FreqMhz, mv = s.CoreMv,
        gh = Math.Round(s.Gh, 1), jth = Math.Round(s.Jth, 2), temp = Math.Round(s.Temp, 1),
    });

    public static async Task SendAsync(BenchSample s, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(Payload(s), Encoding.UTF8, "text/plain") };
        req.Headers.Add("Title", "BitaxeTuner-Benchmark");
        req.Headers.UserAgent.ParseAdd("BitaxeTuner-bench/1.0");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new LocalizedException("Einsenden fehlgeschlagen ({0}).", (int)resp.StatusCode);
    }

    public static async Task<string> FetchStatsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, StatsUrl);
        req.Headers.UserAgent.ParseAdd("BitaxeTuner-bench/1.0");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private sealed record StatsFile([property: JsonPropertyName("models")] Dictionary<string, StatsEntry>? Models);
    private sealed record StatsEntry(
        [property: JsonPropertyName("n")] int N,
        [property: JsonPropertyName("jth")] double[]? Jth,
        [property: JsonPropertyName("gh")] double Gh,
        [property: JsonPropertyName("temp")] double Temp,
        [property: JsonPropertyName("freq")] double Freq,
        [property: JsonPropertyName("mv")] double Mv);

    /// <summary>stats.json der Action lesen (unbekannte oder kaputte Einträge werden übersprungen).</summary>
    public static Dictionary<string, ModelStats> ParseStats(string json)
    {
        var result = new Dictionary<string, ModelStats>(StringComparer.OrdinalIgnoreCase);
        StatsFile? file;
        try { file = JsonSerializer.Deserialize<StatsFile>(json); } catch (JsonException) { return result; }
        foreach (var (model, e) in file?.Models ?? [])
            if (e is { Jth.Length: 5 } && e.N > 0 && e.Jth.All(v => v > 0) && e.Jth.Zip(e.Jth.Skip(1)).All(x => x.First <= x.Second))
                result[model] = new ModelStats(model, e.N, e.Jth, e.Gh, e.Temp, e.Freq, e.Mv);
        return result;
    }

    /// <summary>„oberes Drittel“ usw. für <see cref="ModelStats.BetterThan"/>.</summary>
    public static string RankText(double betterThan) => betterThan switch
    {
        >= 0.9 => L.T("unter den effizientesten 10 %"),
        >= 0.667 => L.T("im oberen Drittel"),
        >= 0.333 => L.T("im Mittelfeld"),
        _ => L.T("im unteren Drittel"),
    };
}
