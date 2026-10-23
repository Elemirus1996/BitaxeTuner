using System.Globalization;
using System.Text.Json;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Display;

/// <summary>
/// 0.9.25 Mini-Anzeige (Infobereich und kleines Fenster der Desktop-App): Zustand aller Miner in einer Zeile – im Modus
/// „Lokal“ aus dem Hub, im Modus „Server“ aus der Status-Antwort des Servers. Gleiche Rechnung wie die Summen im Browser.
/// </summary>
public sealed record MiniStatus(int Online, int Total, double HashGh, double PowerW, double? MaxTemp, double? CostPerDay, string Currency,
    IReadOnlyList<string> Warnings, bool Running = true)
{
    /// <summary>„ok“, „warn“, „error“ oder „unknown“ (Farbe des Symbols).</summary>
    public string Level => Total == 0 ? "unknown" : Online == 0 ? "error" : Online < Total || Warnings.Count > 0 || !Running ? "warn" : "ok";

    public string HashText => StatusRenderer.FormatHash(HashGh);

    /// <summary>„3 °C · 45 W · 0,42 €/Tag“ – Zeile unter der Hashrate.</summary>
    public string DetailText
    {
        get
        {
            var c = L.Culture;
            var parts = new List<string>();
            if (MaxTemp is { } t) parts.Add(t.ToString("0", c) + " °C");
            parts.Add(PowerW.ToString("0", c) + " W");
            if (CostPerDay is { } cost) parts.Add(L.T("{0} {1}/Tag", cost.ToString("0.00", c), Currency));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Tooltip des Symbols im Infobereich (Windows: höchstens 127 Zeichen).</summary>
    public string Tooltip()
    {
        var lines = new List<string> { L.T("BitaxeTuner – {0}/{1} Miner online", Online, Total), HashText + " · " + DetailText };
        if (!Running) lines.Add(L.T("Motor pausiert"));
        if (Warnings.Count > 0) lines.Add(Warnings.Count == 1 ? Warnings[0] : L.T("{0} Warnungen: {1}", Warnings.Count, Warnings[0]));
        var text = string.Join("\n", lines);
        return text.Length > 127 ? text[..126] + "…" : text;
    }

    /// <summary>Aus dem eigenen Hub (Modus „Lokal“).</summary>
    public static MiniStatus FromHub(MinerHub hub, DateTime now)
    {
        var online = hub.Devices.Where(d => d.State.Online && d.State.Info is not null).Select(d => d.State.Info!).ToList();
        var power = online.Sum(i => i.power);
        var energy = hub.Config.Plugs.Items.Count > 0 ? hub.CurrentEnergy() : null;
        var costPower = energy is { FromPlugs: true } ? energy.TotalPowerW : power;
        var price = hub.Prices.PriceAt(now.ToUniversalTime());
        var warnings = hub.Devices.SelectMany(d => hub.ProblemsOf(d).Select(p => $"{d.Title}: {p.Title}")).ToList();
        return new MiniStatus(online.Count, hub.Devices.Count, online.Sum(i => i.hashRate), power,
            online.Count > 0 ? online.Max(i => i.temp) : null,
            costPower * 24 / 1000.0 * Plugs.EnergyCost.CurrentCt(hub.Config, price) / 100.0, hub.Config.Currency, warnings, hub.IsRunning);
    }

    /// <summary>Aus der Antwort von <c>/api/v1/status</c> (Modus „Server“).</summary>
    public static MiniStatus FromStatusJson(JsonElement status)
    {
        var t = status.TryGetProperty("totals", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
        double D(string n) => t.ValueKind == JsonValueKind.Object && t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        double? Dn(string n) => t.ValueKind == JsonValueKind.Object && t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var currency = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "€" : "€";
        var warnings = new List<string>();
        if (status.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            foreach (var d in devs.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (d.TryGetProperty("problems", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                        if (p.TryGetProperty("title", out var title)) warnings.Add($"{name}: {title.GetString()}");
            }
        var running = !status.TryGetProperty("running", out var r) || r.ValueKind != JsonValueKind.False;
        return new MiniStatus((int)D("online"), (int)D("count"), D("hashrate"), D("power"), Dn("maxTemp"), Dn("costPerDay"), currency, warnings, running);
    }
}
