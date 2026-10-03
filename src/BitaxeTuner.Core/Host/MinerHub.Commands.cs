using System.Globalization;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>Vorher/Nachher-Vergleich einer Tuning-Änderung aus history.db.</summary>
public sealed record TuningComparisonRow(TuningEvent Event, WindowAverage? Before, WindowAverage? After)
{
    public DateTime Time => Event.Time;
    public string Source => Event.SourceText;
    public string Change => Event.ChangeText;
    public string BeforeText => Format(Before);
    public string AfterText => Format(After);
    public string DeltaText => Before is null || After is null ? "–"
        : L.T("{0:+0;-0;0} GH/s · {1:+0.0;-0.0;0.0} °C · ", After.HashRateGh - Before.HashRateGh, After.Temp - Before.Temp) +
          (Before.EfficiencyJth is { } b && After.EfficiencyJth is { } a ? L.T("{0:+0.00;-0.00;0.00} J/TH", a - b) : "–");

    private static string Format(WindowAverage? w) => w is null ? L.T("keine Daten")
        : L.T("{0:0} GH/s · {1:0.0} °C · {2} ({3} min)", w.HashRateGh, w.Temp, (w.EfficiencyJth is { } e ? L.T("{0:0.00} J/TH", e) : "–"), w.Minutes);
}

/// <summary>Bestätigungstext für eine Frequenz-/Spannungsänderung (alt → neu), erst danach <see cref="MinerHub.ApplyChangeAsync"/>.</summary>
public sealed record ChangePreview(int FrequencyMhz, int CoreVoltageMv, string ConfirmText);

/// <summary>Einstellungen übertragen: Änderungen je Ziel-Miner oder Grund, warum es dort nicht geht.</summary>
public sealed record CopyPreview(HubDevice Device, IReadOnlyList<SettingChange> Changes, string? Error);

/// <summary>
/// Befehle, die Desktop und Browser gleichermaßen auslösen. Grenzprüfung und Ausführung liegen hier;
/// die Bestätigung (alter und neuer Wert) zeigt die jeweilige Oberfläche vorher mit dem Text aus der Vorschau.
/// Fehlerfälle → <see cref="InvalidOperationException"/> bzw. <see cref="MinerApiException"/> mit Klartext.
/// </summary>
public sealed partial class MinerHub
{
    // ---------- Frequenz / Spannung ----------

    /// <summary>Werte außerhalb der Profilgrenzen des ASIC-Modells → Fehlermeldung, sonst null.</summary>
    public static string? CheckLimits(HubDevice device, int frequencyMhz, int coreVoltageMv)
    {
        var p = device.Profile;
        return frequencyMhz < p.MinFrequencyMhz || frequencyMhz > p.MaxFrequencyMhz ||
               coreVoltageMv < p.MinVoltageMv || coreVoltageMv > p.MaxVoltageMv
            ? L.T("{0} MHz / {1} mV liegt außerhalb der Grenzen für {2}:\n", frequencyMhz, coreVoltageMv, p.Name) +
              L.T("Frequenz {0}–{1} MHz, Spannung {2}–{3} mV.", p.MinFrequencyMhz, p.MaxFrequencyMhz, p.MinVoltageMv, p.MaxVoltageMv)
            : null;
    }

    /// <summary>Grenzen prüfen, aktuellen Wert holen, Bestätigungstext bauen.</summary>
    public async Task<ChangePreview> PreviewChangeAsync(HubDevice device, int frequencyMhz, int coreVoltageMv, string? intro = null)
    {
        if (CheckLimits(device, frequencyMhz, coreVoltageMv) is { } error) throw new InvalidOperationException(error);
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));

        MinerInfo? now = null;
        try { now = await device.Connection.GetInfoAsync(); } catch (MinerApiException) { }
        var overclock = false;
        try { overclock = await device.Connection.WillEnableOverclockAsync(frequencyMhz, coreVoltageMv); } catch (MinerApiException) { }
        var restart = Config.RestartAfterApply;
        var p = device.Profile;

        var text = (intro is null ? "" : intro + "\n\n") + L.T("Einstellung für {0} ändern?\n\n", device.Title) +
                   L.T("Frequenz:      {0} MHz  →  {1} MHz\n", (now is null ? "?" : now.FrequencyMhz.ToString()), frequencyMhz) +
                   L.T("Kernspannung:  {0} mV  →  {1} mV\n\n", (now is null ? "?" : now.CoreVoltageMv.ToString()), coreVoltageMv) +
                   L.T("Grenzen {0}: {1}–{2} MHz, {3}–{4} mV\n", p.Name, p.MinFrequencyMhz, p.MaxFrequencyMhz, p.MinVoltageMv, p.MaxVoltageMv) +
                   (p.IsFallback ? L.T("Achtung: Für dieses Gerät gibt es kein genaues Profil – es gelten vorsichtige Ersatzgrenzen. Im Zweifel ein passendes Profil wählen.\n") : "") +
                   (overclock ? L.T("Der Wert liegt außerhalb der AxeOS-Auswahlliste – „overclockEnabled“ wird eingeschaltet.\n") : "") +
                   (restart ? L.T("Das Gerät wird danach neu gestartet (Watchdog und Offline-Meldung pausieren).\n") : "") +
                   L.T("\nDie Änderung wird mit Zeitstempel in history.db protokolliert.");
        return new ChangePreview(frequencyMhz, coreVoltageMv, text);
    }

    /// <summary>Bestätigte Änderung ausführen: Sicherung, setzen, ggf. Neustart, protokollieren.</summary>
    public async Task ApplyChangeAsync(HubDevice device, int frequencyMhz, int coreVoltageMv, TuningSource source = TuningSource.Manual)
    {
        if (CheckLimits(device, frequencyMhz, coreVoltageMv) is { } error) throw new InvalidOperationException(error);
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));

        await TryAutoBackupAsync(device, L.T("vor manueller Änderung"));
        var before = device.Connection.Last;
        await device.Connection.ApplySettingsAsync(frequencyMhz, coreVoltageMv, source);
        if (Config.RestartAfterApply) await device.Connection.RestartAsync();
        device.PendingSuggestion = null;
        device.AddLog(L.T("Angewendet: {0} → {1} MHz / {2} mV", (before is null ? "?" : L.T("{0} MHz / {1} mV", before.FrequencyMhz, before.CoreVoltageMv)), frequencyMhz, coreVoltageMv), category: null);
        RaiseDeviceChanged(device);
    }

    /// <summary>Offenen Vorschlag nach fehlgeschlagenem Dauertest verwerfen.</summary>
    public void DismissSuggestion(HubDevice device)
    {
        device.PendingSuggestion = null;
        RaiseDeviceChanged(device);
    }

    // ---------- Voreinstellungen und Regeln ----------

    /// <summary>Voreinstellung außerhalb der Profilgrenzen → Fehlermeldung, sonst null.</summary>
    public static string? CheckPreset(HubDevice device, TuningPreset preset) =>
        CheckLimits(device, preset.FrequencyMhz, preset.CoreVoltageMv) is null ? null
            : L.T("{0} liegt außerhalb der Grenzen für {1}.", preset, device.Profile.Name);

    public static string PresetText(HubDevice device, string name) =>
        device.Config.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
            ? p.ToString() : L.T("„{0}“ (fehlt!)", name);

    /// <summary>Text für die Freigabe des Temperaturschutzes.</summary>
    public static string ThermalGuardApprovalText(HubDevice device)
    {
        var g = device.Config.ThermalGuard;
        return L.T("Temperaturschutz für {0} freigeben?\n\n", device.Title) +
               L.T("Wenn die Chiptemperatur über {0:0.#} °C oder die VR-Temperatur über {1:0.#} °C liegt ", g.MaxChipTempC, g.MaxVrTempC) +
               L.T("(durchgehend {0} min), senkt die App die Frequenz um {1} MHz, nie unter {2} MHz. ", g.Minutes, g.StepMhz, g.MinFrequencyMhz) +
               L.T("Die Kernspannung bleibt unverändert.\n") +
               (g.Recover ? L.T("Ist der Miner {0} min mindestens 5 °C unter den Grenzen, geht sie schrittweise zurück bis zur ursprünglichen Frequenz.\n", g.RecoverMinutes) : "") +
               L.T("\nJede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet. Ändert sich die Regel, ist eine neue Freigabe nötig.");
    }

    /// <summary>Text für die Freigabe von Zeitplan bzw. Strompreis-Regel.</summary>
    public string ScheduleApprovalText(HubDevice device)
    {
        var s = device.Config.Schedule;
        string body;
        if (s.Mode == "price")
        {
            body = L.T("Strompreis ({0}) ≤ {1:0.##} ct/kWh → {2}\n", Prices.SourceName, s.ThresholdCt, PresetText(device, s.CheapPreset)) +
                   L.T("sonst → {0}", PresetText(device, s.ExpensivePreset));
        }
        else
        {
            body = string.Join("\n", s.Entries.Select(e => L.T("{0} {1:00}–{2:00} Uhr → {3}", e.DaysText, e.FromHour, e.ToHour, PresetText(device, e.Preset)))) +
                   L.T("\nsonst → {0}", (string.IsNullOrWhiteSpace(s.DefaultPreset) ? L.T("keine Änderung") : PresetText(device, s.DefaultPreset)));
        }
        return L.T("{0} für {1} freigeben?\n\n{2}\n\n", (s.Mode == "price" ? L.T("Strompreis-Regel") : L.T("Zeitplan")), device.Title, body) +
               L.T("Zwischen zwei automatischen Änderungen liegen mindestens {0:0} min. ", AutomationEngine.MinGap.TotalMinutes) +
               L.T("Während eines Benchmarks, Dauertests oder abgesenkten Temperaturschutzes pausiert die Regel. ") +
               L.T("Jede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet.");
    }

    /// <summary>Regel nach bestätigter Freigabe mit Prüfsumme freischalten.</summary>
    public void ApproveRule(HubDevice device, AutomationRule rule, string label)
    {
        if (!rule.Enabled) throw new InvalidOperationException(L.T("{0} ist nicht eingeschaltet.", label));
        rule.Approve(device.Host);
        Config.Save();
        device.AddLog(L.T("{0} freigegeben.", label), EventCategories.Automation);
    }

    // ---------- Dauertest ----------

    public string SoakConfirmText(HubDevice device, int hours)
    {
        var info = device.Info ?? throw new InvalidOperationException(L.T("Dauertest: Miner nicht erreichbar."));
        return L.T("Dauertest für {0} starten?\n\n", device.Title) +
               L.T("Beobachtet wird die aktuelle Einstellung {0} MHz / {1} mV für {2} h: ", info.FrequencyMhz, info.CoreVoltageMv, hours) +
               L.T("Hashrate (Ø 15 min, mind. {0:P0} der Soll-Hashrate), Fehlerrate, Temperaturen, Erreichbarkeit.\n\n", SoakMonitor.RatioThreshold) +
               L.T("Am Miner wird dabei nichts geändert. Zeitplan/Strompreis-Regel pausieren so lange. ") +
               L.T("Bei einem Fehler meldet die App das und schlägt die nächstniedrigere stabile Einstellung vor (nur nach Bestätigung).");
    }

    public void StartSoak(HubDevice device, int hours)
    {
        var info = device.Info ?? throw new InvalidOperationException(L.T("Dauertest: Miner nicht erreichbar."));
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Dauertest: zuerst den Benchmark beenden."));
        if (hours is < 1 or > 168) throw new InvalidOperationException(L.T("Dauertest: 1 bis 168 Stunden."));
        var now = DateTime.Now;
        device.Config.Soak = new SoakTestState(now, now.AddHours(hours), info.FrequencyMhz, info.CoreVoltageMv);
        Config.Save();
        device.SoakMonitor = null;
        device.SoakStatus = L.T("Dauertest gestartet – Anlaufphase");
        device.PendingSuggestion = null;
        device.AddLog(L.T("Dauertest gestartet: {0} MHz / {1} mV für {2} h", info.FrequencyMhz, info.CoreVoltageMv, hours), EventCategories.Soak);
        RaiseDeviceChanged(device);
    }

    public void StopSoak(HubDevice device)
    {
        if (device.Config.Soak is null) return;
        device.Config.Soak = null;
        Config.Save();
        device.SoakMonitor = null;
        device.SoakStatus = L.T("Dauertest abgebrochen");
        device.AddLog(L.T("Dauertest abgebrochen."), EventCategories.Soak);
        RaiseDeviceChanged(device);
    }

    // ---------- Dauertest für mehrere Miner ----------

    /// <summary>Ein Miner in der Auswahl für den gemeinsamen Dauertest.</summary>
    public sealed record SoakBatchEntry(HubDevice Device, bool Eligible, string? Reason, int? FrequencyMhz, int? CoreVoltageMv, bool Running);

    /// <summary>Alle Miner mit aktueller Einstellung und ob ein Dauertest jetzt möglich ist.</summary>
    public IReadOnlyList<SoakBatchEntry> SoakBatchPreview() => Devices.Select(d =>
    {
        var info = d.Info;
        var reason = d.Config.Soak is not null ? L.T("Dauertest läuft bereits")
            : d.IsBenchmarkRunning ? L.T("Benchmark läuft")
            : info is null ? L.T("nicht erreichbar")
            : null;
        return new SoakBatchEntry(d, reason is null, reason, info?.FrequencyMhz, info?.CoreVoltageMv, d.Config.Soak is not null);
    }).ToList();

    /// <summary>
    /// Dauertest für die gewählten Miner starten (je Miner wie <see cref="StartSoak"/>). Nicht geeignete werden
    /// übersprungen; liefert je Miner „gestartet“ oder den Grund.
    /// </summary>
    public IReadOnlyList<(HubDevice Device, bool Started, string Message)> StartSoakBatch(IEnumerable<string> hosts, int hours)
    {
        if (hours is < 1 or > 168) throw new InvalidOperationException(L.T("Dauertest: 1 bis 168 Stunden."));
        var wanted = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<(HubDevice, bool, string)>();
        foreach (var e in SoakBatchPreview().Where(e => wanted.Contains(e.Device.Host)))
        {
            if (!e.Eligible)
            {
                result.Add((e.Device, false, e.Reason!));
                continue;
            }
            try
            {
                StartSoak(e.Device, hours);
                result.Add((e.Device, true, L.T("gestartet: {0} MHz / {1} mV für {2} h", e.FrequencyMhz, e.CoreVoltageMv, hours)));
            }
            catch (InvalidOperationException ex)
            {
                result.Add((e.Device, false, ex.Message));
            }
        }
        if (result.Count == 0) throw new InvalidOperationException(L.T("Dauertest: keinen Miner ausgewählt."));
        return result;
    }

    /// <summary>Alle laufenden Dauertests abbrechen; liefert die Anzahl.</summary>
    public int StopAllSoaks()
    {
        var running = Devices.Where(d => d.Config.Soak is not null).ToList();
        foreach (var d in running) StopSoak(d);
        return running.Count;
    }

    // ---------- Einstellungen sichern / wiederherstellen ----------

    /// <summary>Aktuelle Einstellungen des Miners sichern (&lt;Datenordner&gt;/snapshots).</summary>
    public async Task<SettingsSnapshot> BackupSettingsAsync(HubDevice device, string reason = "manuell")
    {
        var raw = await device.Connection.GetRawInfoAsync();
        var snap = Snapshots.Save(device.Host, device.Title, raw, reason);
        device.AddLog(reason == "manuell" ? L.T("Einstellungen gesichert: {0}", snap.DisplayText) : L.T("Automatische Sicherung ({0})", reason), EventCategories.Settings);
        return snap;
    }

    internal async Task TryAutoBackupAsync(HubDevice device, string reason)
    {
        if (device.IsSimulated) return;
        try { await BackupSettingsAsync(device, reason); }
        catch (Exception ex) { device.AddLog(L.T("Automatische Sicherung ({0}) fehlgeschlagen: {1}", reason, ex.Message), EventCategories.Settings); }
    }

    /// <summary>Unterschiede zwischen Sicherung und aktuellem Stand des Miners.</summary>
    public async Task<(IReadOnlyList<SettingsSnapshot> Snapshots, string CurrentRaw)> RestoreCandidatesAsync(HubDevice device)
    {
        var snapshots = Snapshots.List(device.Host);
        if (snapshots.Count == 0) throw new InvalidOperationException(L.T("Für {0} gibt es noch keine Sicherung.", device.Title));
        var current = await device.Connection.GetRawInfoAsync();
        return (snapshots, current);
    }

    /// <summary>Bestätigte, ausgewählte Felder zurückspielen. Frequenz/Spannung laufen über den protokollierten Weg.</summary>
    public async Task RestoreAsync(HubDevice device, SettingsSnapshot snapshot, IReadOnlyList<SettingChange> changes)
    {
        if (device.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));
        if (changes.Count == 0) return;
        if (CheckFanChanges(device, changes) is { } fanError) throw new InvalidOperationException(fanError);

        var freq = changes.FirstOrDefault(c => c.Field == "frequency");
        var volt = changes.FirstOrDefault(c => c.Field == "coreVoltage");
        if (freq is not null || volt is not null)
        {
            var info = await device.Connection.GetInfoAsync();
            var f = freq is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(freq.Value), CultureInfo.InvariantCulture) : info.FrequencyMhz;
            var v = volt is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(volt.Value), CultureInfo.InvariantCulture) : info.CoreVoltageMv;
            if (CheckLimits(device, f, v) is { } error) throw new InvalidOperationException(error);
        }

        await TryAutoBackupAsync(device, L.T("vor Wiederherstellung"));
        if (freq is not null || volt is not null)
        {
            var info = await device.Connection.GetInfoAsync();
            var f = freq is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(freq.Value), CultureInfo.InvariantCulture) : info.FrequencyMhz;
            var v = volt is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(volt.Value), CultureInfo.InvariantCulture) : info.CoreVoltageMv;
            await device.Connection.ApplySettingsAsync(f, v, TuningSource.Restore);
        }

        var rest = changes.Where(c => c.Field is not "frequency" and not "coreVoltage")
                          .ToDictionary(c => c.Field, c => SettingsSnapshots.ToPatchValue(c.Value));
        if (rest.Count > 0) await device.Connection.PatchSettingsAsync(rest);

        var restart = Config.RestartAfterApply || changes.Any(c => c.Group == SettingGroup.Pool);
        if (restart) await device.Connection.RestartAsync();

        device.AddLog(L.T("Wiederhergestellt ({0}): ", snapshot.DisplayText) +
                      string.Join(", ", changes.Select(c => $"{c.Label} {c.Current} → {c.Saved}")), EventCategories.Settings);
    }

    // ---------- Einstellungen auf mehrere Miner übertragen ----------

    /// <summary>Vorschau je Ziel: welche Felder sich ändern würden (oder warum es nicht geht).</summary>
    public async Task<List<CopyPreview>> CopySettingsPreviewAsync(HubDevice source, IReadOnlyList<HubDevice> targets, IReadOnlySet<SettingGroup> groups)
    {
        if (groups.Count == 0) throw new InvalidOperationException(L.T("Bitte mindestens einen Bereich wählen (Pool oder Lüfter)."));
        var sourceRaw = await source.Connection.GetRawInfoAsync();
        var list = new List<CopyPreview>();
        foreach (var t in targets.Where(t => t != source))
        {
            try
            {
                if (t.IsBenchmarkRunning) throw new InvalidOperationException(L.T("Während eines Benchmarks nicht möglich."));
                var changes = SettingsSnapshots.CopyDiff(sourceRaw, await t.Connection.GetRawInfoAsync(), groups);
                list.Add(new CopyPreview(t, changes, CheckFanChanges(t, changes)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException or System.Text.Json.JsonException)
            {
                list.Add(new CopyPreview(t, [], ex.Message));
            }
        }
        return list;
    }

    /// <summary>
    /// Bestätigte Übertragung: Vorschau neu berechnen (Stand kann sich geändert haben), je Ziel vorher sichern,
    /// Felder setzen, bei Pool-Änderung neu starten und protokollieren. Frequenz/Spannung sind nie dabei.
    /// </summary>
    public async Task<List<CopyPreview>> CopySettingsAsync(HubDevice source, IReadOnlyList<HubDevice> targets, IReadOnlySet<SettingGroup> groups)
    {
        var previews = await CopySettingsPreviewAsync(source, targets, groups);
        var results = new List<CopyPreview>();
        foreach (var p in previews)
        {
            if (p.Error is not null || p.Changes.Count == 0) { results.Add(p); continue; }
            try
            {
                await TryAutoBackupAsync(p.Device, L.T("vor Übernahme von {0}", source.Title));
                await p.Device.Connection.PatchSettingsAsync(p.Changes.ToDictionary(c => c.Field, c => SettingsSnapshots.ToPatchValue(c.Value)));
                if (Config.RestartAfterApply || p.Changes.Any(c => c.Group == SettingGroup.Pool)) await p.Device.Connection.RestartAsync();
                p.Device.AddLog(L.T("Einstellungen übernommen von {0}: ", source.Title) +
                                string.Join(", ", p.Changes.Select(c => $"{c.Label} {c.Current} → {c.Saved}")), EventCategories.Settings);
                results.Add(p);
            }
            catch (Exception ex) when (ex is InvalidOperationException or MinerApiException or NotSupportedException)
            {
                results.Add(p with { Error = ex.Message });
            }
        }
        return results;
    }

    // ---------- Vorher/Nachher ----------

    /// <summary>
    /// Je Tuning-Änderung: Ø Hashrate, Temperatur und J/TH in den 60 min davor und 60 min danach
    /// (die ersten 5 min nach der Änderung werden als Anlaufphase übersprungen).
    /// </summary>
    public List<TuningComparisonRow> Comparisons(HubDevice device)
    {
        var rows = new List<TuningComparisonRow>();
        if (History is not { } history || device.IsSimulated) return rows;
        var events = history.QueryTuningEvents(device.Host, DateTime.Now.AddDays(-30), DateTime.Now)
            .OrderByDescending(e => e.Time).Take(50);
        foreach (var e in events)
        {
            var before = history.Average(device.Host, e.Time.AddMinutes(-60), e.Time.AddMinutes(-1));
            var after = e.Time.AddMinutes(5) < DateTime.Now
                ? history.Average(device.Host, e.Time.AddMinutes(5), e.Time.AddMinutes(65))
                : null;
            rows.Add(new TuningComparisonRow(e, before, after));
        }
        return rows;
    }

    // ---------- Lüfter des Miners (AxeOS/NerdQAxe) ----------

    public const int MinManualFanPercent = 20;
    public const int MinFanTargetTempC = 45;

    /// <summary>Höchste erlaubte Zieltemperatur: Chip-Grenze des Geräteprofils, höchstens 75 °C.</summary>
    public static int MaxFanTargetTempC(HubDevice device) => (int)Math.Min(75, Math.Floor(device.Profile.MaxChipTempC));

    /// <summary>Prüfung vor dem Anwenden; null = in Ordnung, sonst Fehlertext.</summary>
    public static string? CheckMinerFan(HubDevice device, bool auto, int targetTempC, int manualPercent)
    {
        if (auto && (targetTempC < MinFanTargetTempC || targetTempC > MaxFanTargetTempC(device)))
            return L.T("Zieltemperatur {0}–{1} °C (Grenze des Profils {2}).", MinFanTargetTempC, MaxFanTargetTempC(device), device.Profile.Name);
        if (!auto && (manualPercent < MinManualFanPercent || manualPercent > 100))
            return L.T("Lüfter manuell: {0}–100 %.", MinManualFanPercent);
        return null;
    }

    /// <summary>
    /// Lüfterfelder einer Wiederherstellung bzw. Übertragung prüfen (Audit H5): Aus dem aktuellen Stand und den Änderungen
    /// ergibt sich der neue Lüfterzustand; er muss dieselben Grenzen einhalten wie beim direkten Einstellen.
    /// Ergebnis: Fehlermeldung oder null (auch, wenn keine Lüfterfelder dabei sind).
    /// </summary>
    public static string? CheckFanChanges(HubDevice device, IReadOnlyList<SettingChange> changes)
    {
        if (!changes.Any(c => c.Group == SettingGroup.Fan)) return null;
        int? Value(string field) => changes.FirstOrDefault(c => c.Field == field) is { } c
            ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(c.Value), CultureInfo.InvariantCulture) : null;
        var info = device.Info;
        var auto = Value("autofanspeed") is { } a ? a > 0 : info?.AutoFan ?? true;
        var target = Value("temptarget") ?? info?.FanTargetTempC;
        var manual = Value("manualFanSpeed") ?? info?.FanPercent;
        if (Value("minFanSpeed") is { } min && min is < 0 or > 99) return L.T("Lüfter mindestens: 0–99 %.");
        // Unbekannte, unveränderte Werte nicht prüfen (z. B. Firmware ohne Zieltemperatur)
        return CheckMinerFan(device, auto, target ?? MinFanTargetTempC, manual ?? 100);
    }

    /// <summary>Text „alt → neu“ für die Bestätigung (Desktop und Browser gleich).</summary>
    public static string MinerFanText(bool auto, int? targetTempC, int? percent, int? minPercent = null) =>
        auto ? (targetTempC is { } t ? L.T("Automatik, Ziel {0} °C", t) : L.T("Automatik")) + (minPercent is { } m ? L.T(", mindestens {0} %", m) : "")
             : L.T("Manuell {0} %", percent?.ToString() ?? "?");

    /// <summary>
    /// Lüfter des Miners stellen: Automatik mit Zieltemperatur oder fester Wert. Nur nach Bestätigung in der Oberfläche
    /// aufrufen; geprüft gegen die Profilgrenze, protokolliert (Kategorie Lüfter).
    /// </summary>
    /// <param name="minPercent">Mindestdrehzahl der Automatik (0–99 %, wie in AxeOS); null = unverändert.</param>
    public async Task SetMinerFanAsync(HubDevice device, bool auto, int targetTempC, int manualPercent, string source, int? minPercent = null)
    {
        if (CheckMinerFan(device, auto, targetTempC, manualPercent) is { } error) throw new InvalidOperationException(error);
        if (minPercent is < 0 or > 99) throw new InvalidOperationException(L.T("Lüfter mindestens: 0–99 %."));
        var info = device.Info ?? throw new InvalidOperationException(L.T("Miner ist nicht erreichbar."));
        // Firmware ohne „minFanSpeed“: Wert nicht senden und nicht anzeigen
        if (info.FanMinPercent is null) minPercent = null;
        var before = MinerFanText(info.AutoFan == true, info.FanTargetTempC, info.FanPercent, info.FanMinPercent);
        if (auto)
        {
            // Automatik: bisherigen Modus behalten (NerdQAxe 2 = PID), sonst 1; Zieltemperatur und Mindestdrehzahl zuerst
            await device.Connection.SetFanTargetAsync(targetTempC);
            if (minPercent is { } min) await device.Connection.SetFanMinAsync(min);
            await device.Connection.SetFanAsync(info.AutoFanMode is > 0 and var m ? m : 1, info.FanPercent ?? 100);
        }
        else
            await device.Connection.SetFanAsync(0, manualPercent);
        device.AddLog(L.T("AxeOS-Lüfter ({0}): {1} → {2}", source, before, MinerFanText(auto, targetTempC, manualPercent, auto ? minPercent ?? info.FanMinPercent : null)), EventCategories.Fans);
        RaiseDeviceChanged(device);
    }
}
