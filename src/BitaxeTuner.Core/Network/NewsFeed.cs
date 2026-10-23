using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

/// <summary>Eine Neuigkeit; Titel und Text in der Sprache der Oberfläche (Rückfall: Deutsch, dann Englisch).</summary>
/// <param name="Kind">"firmware", "miner", "solo", "network", "bitaxetuner"; 0.9.16 lokal: "halving", "price"; 0.9.20 lokal: "milestone".</param>
public sealed record NewsItem(string Id, DateTime DateUtc, string Kind, string? Coin, string Title, string Text, string Url);

/// <summary>
/// 0.9.11 Neuigkeiten aus der Solo-Mining-Welt: Die GitHub Action „news.yml“ sammelt sie (Firmware-Releases, neue
/// Miner-Modelle, Solo-Blockfunde, Difficulty-Anpassungen, BitaxeTuner-Versionen) in news.json im Branch „news“.
/// Hier wird nur diese eine öffentliche Datei geholt – ohne Nutzerdaten. Die letzte Antwort liegt im Datenordner,
/// damit die Seite auch ohne Internet nach einem Neustart etwas zeigt.
/// </summary>
public sealed class NewsFeed : IDisposable
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/Elemirus1996/BitaxeTuner/news/news.json";
    public static readonly string[] Kinds = ["solo", "firmware", "miner", "network", "bitaxetuner", "halving", "price", "milestone"];
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(3);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private string? _raw;
    private DateTime _fetched = DateTime.MinValue;
    private bool _busy;

    public NewsFeed(string dataDirectory, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner-news/1.0");
        _cacheFile = Path.Combine(dataDirectory, "news-cache.json");
        _localFile = Path.Combine(dataDirectory, "news-local.json");
        try { if (File.Exists(_cacheFile)) _raw = File.ReadAllText(_cacheFile); } catch { /* ohne Zwischenspeicher weiter */ }
        try { if (File.Exists(_localFile)) _local = JsonSerializer.Deserialize<List<NewsItem>>(File.ReadAllText(_localFile)) ?? []; }
        catch { _local = []; }
    }

    // 0.9.16: Meldungen, die der Server selbst erzeugt (Halving-Countdown, große Kursbewegung) – im Datenordner gemerkt
    private readonly string _localFile;
    private List<NewsItem> _local = [];

    /// <summary>Lokale Meldung hinzufügen (gleiche Id = schon vorhanden, nichts passiert). Liefert true, wenn neu.</summary>
    public bool AddLocal(NewsItem item)
    {
        lock (_local)
        {
            if (_local.Any(x => x.Id == item.Id)) return false;
            _local.Add(item);
            _local = _local.OrderByDescending(x => x.DateUtc).Take(40).ToList();
            try { File.WriteAllText(_localFile, JsonSerializer.Serialize(_local)); } catch { /* nur Anzeige */ }
            return true;
        }
    }

    /// <summary>0.9.25: lokale Einträge einer Art (z. B. „milestone“ für den Jahresrückblick), neueste zuerst.</summary>
    public IReadOnlyList<NewsItem> LocalItems(string kind)
    {
        lock (_local) return _local.Where(x => x.Kind == kind).ToList();
    }

    public bool HasLocal(string id)
    {
        lock (_local) return _local.Any(x => x.Id == id);
    }

    public string Url { get; set; } = DefaultUrl;

    /// <summary>Stand laut Datei (UTC) bzw. null, wenn noch nichts geladen ist.</summary>
    public DateTime? UpdatedUtc => Parse(_raw ?? "", "de").Updated;

    public string? LastError { get; private set; }

    /// <summary>Höchstens alle 3 Stunden neu holen (force: sofort).</summary>
    public async Task RefreshAsync(DateTime nowUtc, bool force = false, CancellationToken ct = default)
    {
        if (_busy || (!force && nowUtc - _fetched < RefreshEvery)) return;
        _busy = true;
        _fetched = nowUtc;
        try
        {
            var text = await _http.GetStringAsync(Url, ct).ConfigureAwait(false);
            if (Parse(text, "de").Items.Count == 0 && text.Length > 0 && !text.Contains("\"items\"")) throw new InvalidDataException(L.T("Unerwartetes Format."));
            _raw = text;
            LastError = null;
            try { File.WriteAllText(_cacheFile, text); } catch { /* nicht kritisch */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Neueste Einträge der gewählten Arten (leer = alle), neueste zuerst.</summary>
    public List<NewsItem> Items(IEnumerable<string>? kinds, int max, string? language = null)
    {
        var wanted = kinds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<NewsItem> local;
        lock (_local) local = [.. _local];
        return Parse(_raw ?? "", language ?? Loc.Current.Language).Items.Concat(local)
            .Where(i => wanted is null || wanted.Count == 0 || wanted.Contains(i.Kind))
            .OrderByDescending(i => i.DateUtc).Take(max).ToList();
    }

    /// <summary>news.json lesen; unbekannte Felder und kaputte Einträge werden übersprungen.</summary>
    public static (DateTime? Updated, List<NewsItem> Items) Parse(string json, string language)
    {
        var list = new List<NewsItem>();
        if (string.IsNullOrWhiteSpace(json)) return (null, list);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            DateTime? updated = root.TryGetProperty("updated", out var u) && u.TryGetDateTime(out var ud) ? ud.ToUniversalTime() : null;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return (updated, list);
            foreach (var x in items.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object) continue;
                var id = Str(x, "id");
                var kind = Str(x, "kind");
                if (id.Length == 0 || !x.TryGetProperty("date", out var d) || !d.TryGetDateTime(out var date)) continue;
                var title = Lang(x, "title", language);
                if (title.Length == 0) continue;
                list.Add(new NewsItem(id, date.ToUniversalTime(), kind, x.TryGetProperty("coin", out var c) ? c.GetString() : null,
                    Clip(title, 120), Clip(Lang(x, "text", language), 200), SafeUrl(Str(x, "url"))));
            }
            return (updated, list);
        }
        catch (JsonException)
        {
            return (null, list);
        }
    }

    private static string Str(JsonElement x, string name) =>
        x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Lang(JsonElement x, string name, string language)
    {
        if (!x.TryGetProperty(name, out var v)) return "";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        if (v.ValueKind != JsonValueKind.Object) return "";
        foreach (var l in new[] { language, "en", "de" })
            if (v.TryGetProperty(l, out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s) return s;
        return "";
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Nur https-Links weitergeben (der Browser zeigt sie als Link).</summary>
    private static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.ToString() : "";

    public void Dispose() => _http.Dispose();
}
