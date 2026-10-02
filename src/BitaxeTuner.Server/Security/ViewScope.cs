using BitaxeTuner.Core.Host;

namespace BitaxeTuner.Server.Security;

/// <summary>
/// Welche Miner eine Ansicht sehen darf: <see cref="Groups"/> null = alle, sonst nur Miner in mindestens einer der
/// Gruppen. Miner ohne Gruppe sind dann nicht sichtbar, ebenso Summen und Verläufe über alle Miner.
/// </summary>
public sealed record ViewScope(IReadOnlyList<string>? Groups)
{
    public static readonly ViewScope All = new((IReadOnlyList<string>?)null);

    /// <summary>Für einen Ansicht-Zugang: ohne Gruppen = alle Miner.</summary>
    public static ViewScope For(ViewerAccess? access) =>
        access is { Groups.Count: > 0 } a ? new ViewScope(a.Groups.ToList()) : All;

    public bool Restricted => Groups is not null;

    public bool Allows(HubDevice d) => Allows(d.Config.Groups);

    /// <summary>Eine einzelne Gruppe sichtbar? (Gruppennamen anderer Zugänge nicht verraten.)</summary>
    public bool Allows1(string group) => Groups is null || Groups.Contains(group, StringComparer.OrdinalIgnoreCase);

    public bool Allows(IEnumerable<string> groups) =>
        Groups is null || groups.Any(g => Groups.Contains(g, StringComparer.OrdinalIgnoreCase));

    public bool AllowsHost(Core.Host.MinerHub hub, string host) =>
        Groups is null || (hub.Device(host) is { } d && Allows(d));

    /// <summary>Schlüssel, um den Live-Stand je Freigabe nur einmal zu bauen.</summary>
    public string Key => Groups is null ? "" : string.Join("\n", Groups.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
}
