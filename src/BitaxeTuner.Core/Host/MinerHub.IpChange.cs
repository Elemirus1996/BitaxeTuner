using System.Collections.Concurrent;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Discovery;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>0.9.24: Miner unter neuer Adresse gefunden (gleiche MAC). Übernommen wird erst nach Bestätigung.</summary>
public sealed record HostMove(string OldHost, string NewHost, DateTime FoundAt);

/// <summary>
/// 0.9.24 IP-Wechsel erkennen: BitaxeTuner merkt sich die MAC-Adresse jedes Miners (aus /api/system/info). Ist ein Miner
/// länger als 10 Minuten offline, sucht er höchstens alle 30 Minuten im Heimnetz nach derselben MAC. Gefunden → Meldung und
/// Eintrag in „Was ist los?“ mit „Neue Adresse übernehmen“. Erst nach Bestätigung ziehen Adresse, Verlauf und alle Bezüge
/// (Push-Ziele, Steckdosen, Lüfterregeln) um – nichts wird gelöscht, die alte Adresse bleibt in <see cref="DeviceConfig.PreviousHosts"/>.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly TimeSpan IpWatchOfflineAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IpWatchScanInterval = TimeSpan.FromMinutes(30);
    private DateTime _ipScanAt = DateTime.MinValue;
    private int _ipScanBusy;
    private readonly ConcurrentDictionary<string, HostMove> _hostMoves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Für Tests: Netzwerksuche ersetzen.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<DiscoveredMiner>>>? MinerScan { get; set; }

    /// <summary>Wird nach einem Adresswechsel ausgelöst (alt, neu) – die Desktop-App baut ihre Liste neu auf.</summary>
    public event Action<string, string>? HostChanged;

    /// <summary>MAC einheitlich „AA:BB:CC:DD:EE:FF“; null, wenn keine gültige MAC.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || hex == "000000000000" || hex == "FFFFFFFFFFFF") return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>Gefundener neuer Platz eines Miners, solange er unter der alten Adresse offline ist.</summary>
    public HostMove? HostMoveOf(HubDevice d) =>
        _hostMoves.TryGetValue(d.Host, out var m) && !d.State.Online ? m : null;

    /// <summary>Aus der Abfrage: MAC eines erreichbaren Miners merken (einmalig bzw. bei Änderung speichern).</summary>
    internal void LearnMac(HubDevice d, string? mac)
    {
        if (NormalizeMac(mac) is not { } m || string.Equals(d.Config.Mac, m, StringComparison.OrdinalIgnoreCase)) return;
        d.Config.Mac = m;
        _hostMoves.TryRemove(d.Host, out _);
        Config.Save();
    }

    /// <summary>Aus dem Abfragetakt: bei länger offline Minern mit bekannter MAC das Heimnetz durchsuchen (im Hintergrund).</summary>
    internal void TickIpWatch(DateTime now)
    {
        foreach (var d in Devices.Where(x => x.State.Online)) _hostMoves.TryRemove(d.Host, out _);
        var missing = Devices.Where(d => !d.State.Online && d.Config.Mac is { Length: > 0 } && !Maintenance.IsActive(d.Host)
                                         && d.State.LastOk is { } ok && now - ok >= IpWatchOfflineAfter).ToList();
        if (missing.Count == 0 || now - _ipScanAt < IpWatchScanInterval) return;
        if (!Options.OnlineChecks && MinerScan is null) return;
        if (Interlocked.Exchange(ref _ipScanBusy, 1) == 1) return;
        _ipScanAt = now;
        var scan = MinerScan ?? (ct => NetworkScanner.ScanAsync(ct: ct, include: Web.WebViewServer.IsPrivate));
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var found = await scan(cts.Token);
                ApplyScan(missing, found, now);
            }
            catch { /* nicht kritisch – nächster Versuch nach 30 min */ }
            finally { Interlocked.Exchange(ref _ipScanBusy, 0); }
        });
    }

    /// <summary>Suchergebnis auswerten: gleiche MAC unter einer Adresse, die noch keinem anderen Miner gehört.</summary>
    internal void ApplyScan(IReadOnlyList<HubDevice> missing, IReadOnlyList<DiscoveredMiner> found, DateTime now)
    {
        var known = Config.Devices.Select(c => c.Host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in missing)
        {
            var hit = found.FirstOrDefault(f => string.Equals(NormalizeMac(f.Info.MacAddress), d.Config.Mac, StringComparison.OrdinalIgnoreCase));
            if (hit is null || known.Contains(hit.Address) || string.Equals(hit.Address, d.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (_hostMoves.TryGetValue(d.Host, out var old) && string.Equals(old.NewHost, hit.Address, StringComparison.OrdinalIgnoreCase)) continue;
            _hostMoves[d.Host] = new HostMove(d.Host, hit.Address, now);
            LogEvent(d.Host, EventCategories.Connection, L.T("{0} unter neuer Adresse {1} gefunden (gleiche MAC) – wartet auf Bestätigung.", d.Title, hit.Address));
            SendAlert(new Alert($"ipchange:{d.Host}", L.T("{0} hat jetzt die Adresse {1}", d.Title, hit.Address),
                L.T("Der Miner antwortet unter {0} nicht mehr, aber unter {1} mit derselben MAC-Adresse. In BitaxeTuner „Neue Adresse übernehmen“ wählen – Verlauf und Einstellungen bleiben erhalten. Tipp: im Router eine feste Adresse (DHCP-Reservierung) vergeben.", d.Host, hit.Address),
                NotifyPriority.High, TimeSpan.FromHours(12), NotifyCategory.Offline, d.Host));
        }
    }

    /// <summary>
    /// Adresse eines Miners ändern (vorgeschlagener Umzug oder von Hand): Konfiguration und alle Bezüge, Verlauf in history.db.
    /// Danach <see cref="ApplySettingsChangedAsync"/> aufrufen.
    /// </summary>
    public void ChangeHost(HubDevice d, string newHost)
    {
        var host = (newHost ?? "").Trim();
        if (host.Length == 0 || host.Contains('/') || host.Contains(' ')) throw new LocalizedException("Ungültige Adresse: {0}", newHost ?? "");
        var old = d.Host;
        if (string.Equals(old, host, StringComparison.OrdinalIgnoreCase)) return;
        if (Config.Devices.Any(c => string.Equals(c.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)))
            throw new LocalizedException("Unter {0} ist schon ein anderer Miner eingetragen.", host);
        if (d.IsBenchmarkRunning) throw new LocalizedException("Bitte zuerst den laufenden Benchmark stoppen.");

        var c = d.Config;
        c.PreviousHosts = [.. c.PreviousHosts.Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase)), old];
        if (c.PreviousHosts.Count > 5) c.PreviousHosts = c.PreviousHosts.TakeLast(5).ToList();
        c.Host = host;
        static void Swap(List<string> list, string from, string to)
        {
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Trim(), from, StringComparison.OrdinalIgnoreCase)) list[i] = to;
        }
        foreach (var t in Config.Notifications.Targets) Swap(t.Miners, old, host);
        foreach (var p in Config.Plugs.Items) Swap(p.Miners, old, host);
        Swap(Config.PiFan.Miners, old, host);
        Swap(Config.Fans.Case.Miners, old, host);
        foreach (var ch in Config.Fans.Channels)
            if (string.Equals(ch.MinerHost?.Trim(), old, StringComparison.OrdinalIgnoreCase)) ch.MinerHost = host;
        History?.RenameHost(old, host);
        _hostMoves.TryRemove(old, out _);
        Config.Save();
        LogEvent(host, EventCategories.Settings, L.T("Adresse von {0} geändert: {1} → {2} (Verlauf und Einstellungen übernommen).", c.Name, old, host));
        HostChanged?.Invoke(old, host);
    }
}
