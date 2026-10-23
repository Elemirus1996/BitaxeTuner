using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Transfer;

/// <summary>0.9.24: Stand eines Servers für die gemeinsame Übersicht der Desktop-App.</summary>
public sealed record ServerTile(int Index, string Name, string Url, bool Reachable, string? Error, int Online, int Count,
    double HashGh, double PowerW, double? CostPerDay, string Currency, bool Paused);

/// <summary>0.9.24: Summen über alle erreichbaren Server. Kosten nur, wenn alle dieselbe Währung haben.</summary>
public sealed record ServerTotals(int Servers, int Reachable, int Online, int Count, double HashGh, double PowerW, double? CostPerDay, string? Currency);

/// <summary>
/// 0.9.24 Mehrere Server in einer Desktop-App: <see cref="AppConfig.Server"/> bleibt die aktive Verbindung (alles Bisherige –
/// Sicherung holen, SSH, HTTPS – gilt für sie), weitere stehen in <see cref="AppConfig.ExtraServers"/>. Umschalten tauscht
/// die Einträge, damit nichts anderes umgebaut werden muss. Rein additiv: ohne weitere Server ändert sich nichts.
/// </summary>
public static class MultiServer
{
    public const int MaxServers = 10;

    /// <summary>Aktiver Server zuerst, dann die weiteren.</summary>
    public static List<ServerConnectionSettings> All(AppConfig c) => [c.Server, .. c.ExtraServers];

    /// <summary>Anzeigename: eigener Name, sonst Rechner aus der Adresse.</summary>
    public static string NameOf(ServerConnectionSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name.Trim();
        return Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Host : (s.Url.Length > 0 ? s.Url : L.T("Server"));
    }

    /// <summary>Server Nr. <paramref name="index"/> (0 = aktiv) zum aktiven machen; der bisherige rückt an seine Stelle.</summary>
    public static void Activate(AppConfig c, int index)
    {
        if (index <= 0 || index > c.ExtraServers.Count) return;
        var next = c.ExtraServers[index - 1];
        c.ExtraServers[index - 1] = c.Server;
        c.Server = next;
        c.Server.Enabled = true;
        c.ExtraServers[index - 1].Enabled = false;
    }

    /// <summary>Weiteren Server anhängen; gleiche Adresse ersetzt den alten Eintrag (neues Token, neuer Name).</summary>
    public static void Add(AppConfig c, ServerConnectionSettings s)
    {
        var same = c.ExtraServers.FindIndex(x => SameUrl(x.Url, s.Url));
        if (SameUrl(c.Server.Url, s.Url)) throw new LocalizedException("Dieser Server ist schon der aktive.");
        if (same >= 0) c.ExtraServers[same] = s;
        else if (c.ExtraServers.Count + 1 >= MaxServers) throw new LocalizedException("Höchstens {0} Server.", MaxServers);
        else c.ExtraServers.Add(s);
        s.Enabled = false;
    }

    public static void Remove(AppConfig c, int index)
    {
        if (index >= 1 && index <= c.ExtraServers.Count) c.ExtraServers.RemoveAt(index - 1);
    }

    private static bool SameUrl(string a, string b) =>
        a.Length > 0 && b.Length > 0 && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Kachel aus der Antwort von <c>/api/v1/status</c> („totals“).</summary>
    public static ServerTile Tile(int index, ServerConnectionSettings s, JsonElement status)
    {
        var t = status.TryGetProperty("totals", out var x) ? x : default;
        double D(string name) => t.ValueKind == JsonValueKind.Object && t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        double? Dn(string name) => t.ValueKind == JsonValueKind.Object && t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var currency = status.TryGetProperty("money", out var m) && m.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
        var paused = status.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.False;
        return new ServerTile(index, NameOf(s), s.Url, true, null, (int)D("online"), (int)D("count"), D("hashrate"), D("power"), Dn("costPerDay"), currency, paused);
    }

    public static ServerTile Unreachable(int index, ServerConnectionSettings s, string error) =>
        new(index, NameOf(s), s.Url, false, error, 0, 0, 0, 0, null, "", false);

    public static ServerTotals Sum(IReadOnlyCollection<ServerTile> tiles)
    {
        var ok = tiles.Where(x => x.Reachable).ToList();
        var currencies = ok.Select(x => x.Currency).Distinct().ToList();
        double? cost = ok.Count > 0 && currencies.Count == 1 && ok.All(x => x.CostPerDay is not null) ? ok.Sum(x => x.CostPerDay!.Value) : null;
        return new ServerTotals(tiles.Count, ok.Count, ok.Sum(x => x.Online), ok.Sum(x => x.Count), ok.Sum(x => x.HashGh), ok.Sum(x => x.PowerW),
            cost, currencies.Count == 1 ? currencies[0] : null);
    }

    /// <summary>Alle Server gleichzeitig abfragen (je 8 s); ein nicht erreichbarer bremst die anderen nicht.</summary>
    public static async Task<List<ServerTile>> FetchAllAsync(AppConfig c, Func<ServerConnectionSettings, ServerClient>? factory = null, CancellationToken ct = default)
    {
        factory ??= s => new ServerClient(s.Url, s.Token, s.CertificateFingerprint, TimeSpan.FromSeconds(8));
        var all = All(c);
        var tasks = all.Select(async (s, i) =>
        {
            if (s.Url.Length == 0 || s.Token.Length == 0) return Unreachable(i, s, L.T("Adresse oder Token fehlt."));
            try
            {
                using var client = factory(s);
                return Tile(i, s, await client.StatusAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Unreachable(i, s, ex.Message);
            }
        });
        return [.. await Task.WhenAll(tasks)];
    }
}
