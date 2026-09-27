using System.Globalization;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Automation;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Storage;

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
        : $"{After.HashRateGh - Before.HashRateGh:+0;-0;0} GH/s · {After.Temp - Before.Temp:+0.0;-0.0;0.0} °C · " +
          (Before.EfficiencyJth is { } b && After.EfficiencyJth is { } a ? $"{a - b:+0.00;-0.00;0.00} J/TH" : "–");

    private static string Format(WindowAverage? w) => w is null ? "keine Daten"
        : $"{w.HashRateGh:0} GH/s · {w.Temp:0.0} °C · {(w.EfficiencyJth is { } e ? $"{e:0.00} J/TH" : "–")} ({w.Minutes} min)";
}

/// <summary>Bestätigungstext für eine Frequenz-/Spannungsänderung (alt → neu), erst danach <see cref="MinerHub.ApplyChangeAsync"/>.</summary>
public sealed record ChangePreview(int FrequencyMhz, int CoreVoltageMv, string ConfirmText);

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
            ? $"{frequencyMhz} MHz / {coreVoltageMv} mV liegt außerhalb der Grenzen für {p.Name}:\n" +
              $"Frequenz {p.MinFrequencyMhz}–{p.MaxFrequencyMhz} MHz, Spannung {p.MinVoltageMv}–{p.MaxVoltageMv} mV."
            : null;
    }

    /// <summary>Grenzen prüfen, aktuellen Wert holen, Bestätigungstext bauen.</summary>
    public async Task<ChangePreview> PreviewChangeAsync(HubDevice device, int frequencyMhz, int coreVoltageMv, string? intro = null)
    {
        if (CheckLimits(device, frequencyMhz, coreVoltageMv) is { } error) throw new InvalidOperationException(error);
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Während eines Benchmarks nicht möglich.");

        MinerInfo? now = null;
        try { now = await device.Connection.GetInfoAsync(); } catch (MinerApiException) { }
        var overclock = false;
        try { overclock = await device.Connection.WillEnableOverclockAsync(frequencyMhz, coreVoltageMv); } catch (MinerApiException) { }
        var restart = Config.RestartAfterApply;
        var p = device.Profile;

        var text = (intro is null ? "" : intro + "\n\n") + $"Einstellung für {device.Title} ändern?\n\n" +
                   $"Frequenz:      {(now is null ? "?" : now.FrequencyMhz.ToString())} MHz  →  {frequencyMhz} MHz\n" +
                   $"Kernspannung:  {(now is null ? "?" : now.CoreVoltageMv.ToString())} mV  →  {coreVoltageMv} mV\n\n" +
                   $"Grenzen {p.Name}: {p.MinFrequencyMhz}–{p.MaxFrequencyMhz} MHz, {p.MinVoltageMv}–{p.MaxVoltageMv} mV\n" +
                   (overclock ? "Der Wert liegt außerhalb der AxeOS-Auswahlliste – „overclockEnabled“ wird eingeschaltet.\n" : "") +
                   (restart ? "Das Gerät wird danach neu gestartet (Watchdog und Offline-Meldung pausieren).\n" : "") +
                   "\nDie Änderung wird mit Zeitstempel in history.db protokolliert.";
        return new ChangePreview(frequencyMhz, coreVoltageMv, text);
    }

    /// <summary>Bestätigte Änderung ausführen: Sicherung, setzen, ggf. Neustart, protokollieren.</summary>
    public async Task ApplyChangeAsync(HubDevice device, int frequencyMhz, int coreVoltageMv, TuningSource source = TuningSource.Manual)
    {
        if (CheckLimits(device, frequencyMhz, coreVoltageMv) is { } error) throw new InvalidOperationException(error);
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Während eines Benchmarks nicht möglich.");

        await TryAutoBackupAsync(device, "vor manueller Änderung");
        var before = device.Connection.Last;
        await device.Connection.ApplySettingsAsync(frequencyMhz, coreVoltageMv, source);
        if (Config.RestartAfterApply) await device.Connection.RestartAsync();
        device.PendingSuggestion = null;
        device.AddLog($"Angewendet: {(before is null ? "?" : $"{before.FrequencyMhz} MHz / {before.CoreVoltageMv} mV")} → {frequencyMhz} MHz / {coreVoltageMv} mV");
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
            : $"{preset} liegt außerhalb der Grenzen für {device.Profile.Name}.";

    public static string PresetText(HubDevice device, string name) =>
        device.Config.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
            ? p.ToString() : $"„{name}“ (fehlt!)";

    /// <summary>Text für die Freigabe des Temperaturschutzes.</summary>
    public static string ThermalGuardApprovalText(HubDevice device)
    {
        var g = device.Config.ThermalGuard;
        return $"Temperaturschutz für {device.Title} freigeben?\n\n" +
               $"Wenn die Chiptemperatur über {g.MaxChipTempC:0.#} °C oder die VR-Temperatur über {g.MaxVrTempC:0.#} °C liegt " +
               $"(durchgehend {g.Minutes} min), senkt die App die Frequenz um {g.StepMhz} MHz, nie unter {g.MinFrequencyMhz} MHz. " +
               "Die Kernspannung bleibt unverändert.\n" +
               (g.Recover ? $"Ist der Miner {g.RecoverMinutes} min mindestens 5 °C unter den Grenzen, geht sie schrittweise zurück bis zur ursprünglichen Frequenz.\n" : "") +
               "\nJede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet. Ändert sich die Regel, ist eine neue Freigabe nötig.";
    }

    /// <summary>Text für die Freigabe von Zeitplan bzw. Strompreis-Regel.</summary>
    public string ScheduleApprovalText(HubDevice device)
    {
        var s = device.Config.Schedule;
        string body;
        if (s.Mode == "price")
        {
            body = $"Strompreis ({Prices.SourceName}) ≤ {s.ThresholdCt:0.##} ct/kWh → {PresetText(device, s.CheapPreset)}\n" +
                   $"sonst → {PresetText(device, s.ExpensivePreset)}";
        }
        else
        {
            body = string.Join("\n", s.Entries.Select(e => $"{e.DaysText} {e.FromHour:00}–{e.ToHour:00} Uhr → {PresetText(device, e.Preset)}")) +
                   $"\nsonst → {(string.IsNullOrWhiteSpace(s.DefaultPreset) ? "keine Änderung" : PresetText(device, s.DefaultPreset))}";
        }
        return $"{(s.Mode == "price" ? "Strompreis-Regel" : "Zeitplan")} für {device.Title} freigeben?\n\n{body}\n\n" +
               $"Zwischen zwei automatischen Änderungen liegen mindestens {AutomationEngine.MinGap.TotalMinutes:0} min. " +
               "Während eines Benchmarks, Dauertests oder abgesenkten Temperaturschutzes pausiert die Regel. " +
               "Jede Änderung wird protokolliert, im Verlauf markiert und per Push gemeldet.";
    }

    /// <summary>Regel nach bestätigter Freigabe mit Prüfsumme freischalten.</summary>
    public void ApproveRule(HubDevice device, AutomationRule rule, string label)
    {
        if (!rule.Enabled) throw new InvalidOperationException($"{label} ist nicht eingeschaltet.");
        rule.Approve(device.Host);
        Config.Save();
        device.AddLog($"{label} freigegeben.");
    }

    // ---------- Dauertest ----------

    public string SoakConfirmText(HubDevice device, int hours)
    {
        var info = device.Info ?? throw new InvalidOperationException("Dauertest: Miner nicht erreichbar.");
        return $"Dauertest für {device.Title} starten?\n\n" +
               $"Beobachtet wird die aktuelle Einstellung {info.FrequencyMhz} MHz / {info.CoreVoltageMv} mV für {hours} h: " +
               $"Hashrate (Ø 15 min, mind. {SoakMonitor.RatioThreshold:P0} der Soll-Hashrate), Fehlerrate, Temperaturen, Erreichbarkeit.\n\n" +
               "Am Miner wird dabei nichts geändert. Zeitplan/Strompreis-Regel pausieren so lange. " +
               "Bei einem Fehler meldet die App das und schlägt die nächstniedrigere stabile Einstellung vor (nur nach Bestätigung).";
    }

    public void StartSoak(HubDevice device, int hours)
    {
        var info = device.Info ?? throw new InvalidOperationException("Dauertest: Miner nicht erreichbar.");
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Dauertest: zuerst den Benchmark beenden.");
        if (hours is < 1 or > 168) throw new InvalidOperationException("Dauertest: 1 bis 168 Stunden.");
        var now = DateTime.Now;
        device.Config.Soak = new SoakTestState(now, now.AddHours(hours), info.FrequencyMhz, info.CoreVoltageMv);
        Config.Save();
        device.SoakMonitor = null;
        device.SoakStatus = "Dauertest gestartet – Anlaufphase";
        device.PendingSuggestion = null;
        device.AddLog($"Dauertest gestartet: {info.FrequencyMhz} MHz / {info.CoreVoltageMv} mV für {hours} h");
        RaiseDeviceChanged(device);
    }

    public void StopSoak(HubDevice device)
    {
        if (device.Config.Soak is null) return;
        device.Config.Soak = null;
        Config.Save();
        device.SoakMonitor = null;
        device.SoakStatus = "Dauertest abgebrochen";
        device.AddLog("Dauertest abgebrochen.");
        RaiseDeviceChanged(device);
    }

    // ---------- Dauertest für mehrere Miner ----------

    /// <summary>Ein Miner in der Auswahl für den gemeinsamen Dauertest.</summary>
    public sealed record SoakBatchEntry(HubDevice Device, bool Eligible, string? Reason, int? FrequencyMhz, int? CoreVoltageMv, bool Running);

    /// <summary>Alle Miner mit aktueller Einstellung und ob ein Dauertest jetzt möglich ist.</summary>
    public IReadOnlyList<SoakBatchEntry> SoakBatchPreview() => Devices.Select(d =>
    {
        var info = d.Info;
        var reason = d.Config.Soak is not null ? "Dauertest läuft bereits"
            : d.IsBenchmarkRunning ? "Benchmark läuft"
            : info is null ? "nicht erreichbar"
            : null;
        return new SoakBatchEntry(d, reason is null, reason, info?.FrequencyMhz, info?.CoreVoltageMv, d.Config.Soak is not null);
    }).ToList();

    /// <summary>
    /// Dauertest für die gewählten Miner starten (je Miner wie <see cref="StartSoak"/>). Nicht geeignete werden
    /// übersprungen; liefert je Miner „gestartet“ oder den Grund.
    /// </summary>
    public IReadOnlyList<(HubDevice Device, bool Started, string Message)> StartSoakBatch(IEnumerable<string> hosts, int hours)
    {
        if (hours is < 1 or > 168) throw new InvalidOperationException("Dauertest: 1 bis 168 Stunden.");
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
                result.Add((e.Device, true, $"gestartet: {e.FrequencyMhz} MHz / {e.CoreVoltageMv} mV für {hours} h"));
            }
            catch (InvalidOperationException ex)
            {
                result.Add((e.Device, false, ex.Message));
            }
        }
        if (result.Count == 0) throw new InvalidOperationException("Dauertest: keinen Miner ausgewählt.");
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
        device.AddLog(reason == "manuell" ? $"Einstellungen gesichert: {snap.DisplayText}" : $"Automatische Sicherung ({reason})");
        return snap;
    }

    internal async Task TryAutoBackupAsync(HubDevice device, string reason)
    {
        if (device.IsSimulated) return;
        try { await BackupSettingsAsync(device, reason); }
        catch (Exception ex) { device.AddLog($"Automatische Sicherung ({reason}) fehlgeschlagen: {ex.Message}"); }
    }

    /// <summary>Unterschiede zwischen Sicherung und aktuellem Stand des Miners.</summary>
    public async Task<(IReadOnlyList<SettingsSnapshot> Snapshots, string CurrentRaw)> RestoreCandidatesAsync(HubDevice device)
    {
        var snapshots = Snapshots.List(device.Host);
        if (snapshots.Count == 0) throw new InvalidOperationException($"Für {device.Title} gibt es noch keine Sicherung.");
        var current = await device.Connection.GetRawInfoAsync();
        return (snapshots, current);
    }

    /// <summary>Bestätigte, ausgewählte Felder zurückspielen. Frequenz/Spannung laufen über den protokollierten Weg.</summary>
    public async Task RestoreAsync(HubDevice device, SettingsSnapshot snapshot, IReadOnlyList<SettingChange> changes)
    {
        if (device.IsBenchmarkRunning) throw new InvalidOperationException("Während eines Benchmarks nicht möglich.");
        if (changes.Count == 0) return;

        var freq = changes.FirstOrDefault(c => c.Field == "frequency");
        var volt = changes.FirstOrDefault(c => c.Field == "coreVoltage");
        if (freq is not null || volt is not null)
        {
            var info = await device.Connection.GetInfoAsync();
            var f = freq is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(freq.Value), CultureInfo.InvariantCulture) : info.FrequencyMhz;
            var v = volt is not null ? Convert.ToInt32(SettingsSnapshots.ToPatchValue(volt.Value), CultureInfo.InvariantCulture) : info.CoreVoltageMv;
            if (CheckLimits(device, f, v) is { } error) throw new InvalidOperationException(error);
        }

        await TryAutoBackupAsync(device, "vor Wiederherstellung");
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

        device.AddLog($"Wiederhergestellt ({snapshot.DisplayText}): " +
                      string.Join(", ", changes.Select(c => $"{c.Label} {c.Current} → {c.Saved}")));
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
}
