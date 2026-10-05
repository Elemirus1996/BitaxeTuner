using System.Text.Json;
using System.Text.RegularExpressions;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Profiles;

/// <summary>Eintrag der Profilliste für Browser und Desktop: aktueller Stand, eingebauter Stand (falls vorhanden) und Herkunft.</summary>
public sealed record ProfileEntry(DeviceProfile Profile, DeviceProfile? BuiltIn, bool IsBuiltIn, bool IsCustomized);

/// <summary>Ergebnis der Prüfung: Fehler verhindern das Speichern, Hinweise (Grenzen über dem eingebauten Profil) brauchen eine Bestätigung.</summary>
public sealed record ProfileCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Changes);

/// <summary>
/// Geräteprofile bearbeiten (0.9.9, Browser und Desktop): eigene Profile anlegen, eingebaute anpassen oder auf den
/// eingebauten Stand zurücksetzen. Gespeichert wird wie bisher in <c>profiles.json</c> (Liste von <see cref="DeviceProfile"/>,
/// nur Abweichungen und eigene Profile); vor jedem Schreiben bleibt der vorige Stand als <c>profiles.json.bak</c> erhalten.
/// </summary>
public static class ProfileEditor
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,39}$");

    /// <summary>Alle Profile (eingebaute zuerst, dann eigene) mit Herkunft.</summary>
    public static List<ProfileEntry> List(string dataDirectory)
    {
        var builtIn = ProfileRegistry.LoadBuiltIn();
        var current = ProfileRegistry.Load(dataDirectory).Profiles;
        return current.Select(p =>
        {
            var b = builtIn.FirstOrDefault(x => Same(x.Id, p.Id));
            return new ProfileEntry(p, b, b is not null, b is not null && !SameContent(b, p));
        }).OrderBy(e => e.IsBuiltIn ? 0 : 1).ToList();
    }

    /// <summary>
    /// Vergleichsprofil für die Bestätigung: das eingebaute Profil gleicher Kennung, sonst das Quellprofil einer Kopie
    /// (<see cref="DeviceProfile.BasedOn"/>), sonst das stärkste eingebaute Profil desselben ASIC-Chips, sonst „generic“.
    /// </summary>
    public static DeviceProfile Reference(DeviceProfile p, IReadOnlyList<DeviceProfile> builtIns)
    {
        if (builtIns.FirstOrDefault(x => Same(x.Id, p.Id)) is { } same) return same;
        if (p.BasedOn is { } src && builtIns.FirstOrDefault(x => Same(x.Id, src)) is { } based) return based;
        var family = Family(p, builtIns);
        if (family.Count == 0) return builtIns.First(x => x.Id == "generic");
        var strongest = family.OrderByDescending(x => x.MaxFrequencyMhz).First().Clone();
        strongest.Name = L.T("stärkstes eingebautes {0}-Profil", p.AsicModel);
        strongest.MaxFrequencyMhz = family.Max(x => x.MaxFrequencyMhz);
        strongest.MaxVoltageMv = family.Max(x => x.MaxVoltageMv);
        strongest.MaxChipTempC = family.Max(x => x.MaxChipTempC);
        strongest.MaxVrTempC = family.Max(x => x.MaxVrTempC);
        strongest.MaxPowerW = Math.Round(family.Max(x => x.MaxPowerW / Math.Max(1, x.AsicCount)) * Math.Max(1, p.AsicCount), 1);
        return strongest;
    }

    /// <summary>
    /// Feste Obergrenzen je ASIC-Chip (Audit N-S2), auch mit Bestätigung nicht überschreitbar: höchste eingebaute Frequenz
    /// + 25 %, höchste eingebaute Spannung + 100 mV, Chip 80 °C, VR 105 °C (wie die absoluten Benchmark-Grenzen),
    /// Leistung das 1,5-Fache des stärksten eingebauten Profils je ASIC. Unbekannter Chip: vom allgemeinen Profil aus.
    /// </summary>
    public static (int FrequencyMhz, int VoltageMv, double ChipC, double VrC, double PowerW) Ceiling(DeviceProfile p, IReadOnlyList<DeviceProfile> builtIns)
    {
        var family = Family(p, builtIns);
        if (family.Count == 0) family = [builtIns.First(x => x.Id == "generic")];
        var perAsic = family.Max(x => x.MaxPowerW / Math.Max(1, x.AsicCount));
        return ((int)Math.Round(family.Max(x => x.MaxFrequencyMhz) * 1.25), family.Max(x => x.MaxVoltageMv) + 100, 80, 105,
            Math.Round(perAsic * Math.Max(1, p.AsicCount) * 1.5, 1));
    }

    private static List<DeviceProfile> Family(DeviceProfile p, IReadOnlyList<DeviceProfile> builtIns) =>
        builtIns.Where(x => x.Id != "generic" && x.AsicModel.Length > 0 && Same(x.AsicModel, p.AsicModel ?? "")).ToList();

    /// <summary>Prüft ein Profil. <paramref name="previous"/> ist der bisherige Stand (für die Liste der Änderungen).</summary>
    /// <param name="valuesOnly">Beim Laden von profiles.json: nur Grenzwerte prüfen, nicht die Schreibweise von Kennung, Name
    /// und Erkennung (ältere, von Hand angelegte Profile sollen nicht deswegen wegfallen).</param>
    public static ProfileCheck Check(DeviceProfile p, DeviceProfile? previous, IReadOnlyList<DeviceProfile> builtIns, bool valuesOnly = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (!valuesOnly && !IdPattern.IsMatch(p.Id ?? ""))
            errors.Add(L.T("Kennung: 2–40 Zeichen, nur Kleinbuchstaben, Ziffern und Bindestrich."));
        if (!valuesOnly && (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60))
            errors.Add(L.T("Name: 1–60 Zeichen."));
        if (p.Id == "generic" && previous is null)
            errors.Add(L.T("Die Kennung „generic“ ist für das allgemeine Profil reserviert."));

        Range(errors, L.T("Frequenz"), p.MinFrequencyMhz, p.DefaultFrequencyMhz, p.MaxFrequencyMhz, 50, 1500, "MHz");
        Range(errors, L.T("Spannung"), p.MinVoltageMv, p.DefaultVoltageMv, p.MaxVoltageMv, 700, 1800, "mV");
        Between(errors, L.T("Max. Chiptemperatur"), p.MaxChipTempC, 40, 90, "°C");
        Between(errors, L.T("Max. VR-Temperatur"), p.MaxVrTempC, 50, 120, "°C");
        Between(errors, L.T("Max. Leistung"), p.MaxPowerW, 1, 2000, "W");
        Between(errors, L.T("ASIC-Anzahl"), p.AsicCount, 1, 64, "");
        Between(errors, L.T("Small-Cores je ASIC"), p.SmallCoresPerAsic, 0, 20000, "");
        if (p.MinInputVoltageMv is { } lo) Between(errors, L.T("Min. Eingangsspannung"), lo, 3000, 15000, "mV");
        if (p.MaxInputVoltageMv is { } hi) Between(errors, L.T("Max. Eingangsspannung"), hi, 3000, 15000, "mV");
        if (p.MinInputVoltageMv is { } a && p.MaxInputVoltageMv is { } b && a >= b)
            errors.Add(L.T("Eingangsspannung: Minimum muss unter dem Maximum liegen."));
        if (!valuesOnly && (p.DeviceModelMatches.Count > 20 || p.DeviceModelMatches.Any(x => x.Length is 0 or > 40)))
            errors.Add(L.T("Erkennung: höchstens 20 Einträge mit je 1–40 Zeichen."));
        if (!valuesOnly && (p.BoardVersions.Count > 20 || p.BoardVersions.Any(x => x.Length is 0 or > 10)))
            errors.Add(L.T("Board-Versionen: höchstens 20 Einträge mit je 1–10 Zeichen."));
        if (!valuesOnly && p.Notes is { Length: > 500 })
            errors.Add(L.T("Notiz: höchstens 500 Zeichen."));

        // Feste Obergrenzen je ASIC-Chip: auch mit Bestätigung nicht
        var cap = Ceiling(p, builtIns);
        void Cap(string label, double now, double limit, string unit)
        {
            if (now > limit) errors.Add(L.T("{0} {1} {2} liegt über der festen Obergrenze für {3} ({4} {2}).", label, now, unit,
                string.IsNullOrEmpty(p.AsicModel) ? L.T("unbekannte Chips") : p.AsicModel, limit));
        }
        Cap(L.T("Max. Frequenz"), p.MaxFrequencyMhz, cap.FrequencyMhz, "MHz");
        Cap(L.T("Max. Spannung"), p.MaxVoltageMv, cap.VoltageMv, "mV");
        Cap(L.T("Max. Chiptemperatur"), p.MaxChipTempC, cap.ChipC, "°C");
        Cap(L.T("Max. VR-Temperatur"), p.MaxVrTempC, cap.VrC, "°C");
        Cap(L.T("Max. Leistung"), p.MaxPowerW, cap.PowerW, "W");

        // Grenzen über dem Vergleichsprofil: erlaubt, aber nur mit ausdrücklicher Bestätigung – auch für Kopien (Audit N-S2)
        var reference = Reference(p, builtIns);
        void Over(string label, double now, double limit, string unit)
        {
            if (now > limit) warnings.Add(L.T("{0} {1} {2} liegt über dem Profil „{3}“ ({4} {2}).", label, now, unit, reference.Name, limit));
        }
        Over(L.T("Max. Frequenz"), p.MaxFrequencyMhz, reference.MaxFrequencyMhz, "MHz");
        Over(L.T("Max. Spannung"), p.MaxVoltageMv, reference.MaxVoltageMv, "mV");
        Over(L.T("Max. Chiptemperatur"), p.MaxChipTempC, reference.MaxChipTempC, "°C");
        Over(L.T("Max. VR-Temperatur"), p.MaxVrTempC, reference.MaxVrTempC, "°C");
        Over(L.T("Max. Leistung"), p.MaxPowerW, reference.MaxPowerW, "W");

        // Automatische Erkennung: ein eigenes oder geändertes Erkennungsmuster übernimmt Miner ohne Rückfrage – bestätigen lassen
        var builtInSame = builtIns.FirstOrDefault(x => Same(x.Id, p.Id ?? ""));
        bool SameList(List<string> a, List<string> b) => a.Count == b.Count && a.Zip(b).All(t => string.Equals(t.First, t.Second, StringComparison.OrdinalIgnoreCase));
        var detectionBefore = previous ?? builtInSame;
        if ((p.DeviceModelMatches.Count > 0 || p.BoardVersions.Count > 0) && (detectionBefore is null
                || !SameList(p.DeviceModelMatches, detectionBefore.DeviceModelMatches) || !SameList(p.BoardVersions, detectionBefore.BoardVersions)))
            warnings.Add(L.T("Erkennung: Miner mit Gerätemodell „{0}“{1} bekommen dieses Profil automatisch – auch die bereits eingetragenen.",
                p.DeviceModelMatches.Count > 0 ? string.Join("“, „", p.DeviceModelMatches) : "–",
                p.BoardVersions.Count > 0 ? L.T(" und Board „{0}“", string.Join("“, „", p.BoardVersions)) : ""));
        return new ProfileCheck(errors, warnings, Changes(previous, p));
    }

    /// <summary>Speichert ein Profil (neu oder geändert). Wirft bei Fehlern; Hinweise nur mit <paramref name="confirmed"/>.</summary>
    public static ProfileCheck Save(string dataDirectory, DeviceProfile profile, string? originalId, bool confirmed)
    {
        profile = Normalize(profile);
        var entries = List(dataDirectory);
        var previous = originalId is null ? null : entries.FirstOrDefault(e => Same(e.Profile.Id, originalId))?.Profile;
        if (originalId is not null && previous is null)
            throw new KeyNotFoundException(L.T("Profil unbekannt."));
        if (originalId is not null && !Same(originalId, profile.Id))
            throw new InvalidOperationException(L.T("Die Kennung eines Profils lässt sich nicht ändern – bitte als Kopie neu anlegen."));
        if (originalId is null && entries.Any(e => Same(e.Profile.Id, profile.Id)))
            throw new InvalidOperationException(L.T("Ein Profil mit der Kennung „{0}“ gibt es schon.", profile.Id));

        var builtIns = ProfileRegistry.LoadBuiltIn();
        var builtIn = builtIns.FirstOrDefault(x => Same(x.Id, profile.Id));
        if (builtIn is not null) profile.BasedOn = null;   // eingebautes Profil angepasst: Bezug ist es selbst
        var check = Check(profile, previous, builtIns);
        if (check.Errors.Count > 0)
            throw new ArgumentException(string.Join(" ", check.Errors));
        if (check.Warnings.Count > 0 && !confirmed)
            return check;

        var user = ReadUser(dataDirectory);
        user.RemoveAll(x => Same(x.Id, profile.Id));
        // gleicher Stand wie eingebaut → kein Eintrag nötig (eingebaute Korrekturen greifen dann weiter)
        if (builtIn is null || !SameContent(builtIn, profile))
            user.Add(profile);
        WriteUser(dataDirectory, user);
        return check;
    }

    /// <summary>Eigenes Profil löschen bzw. eingebautes auf den eingebauten Stand zurücksetzen. Gibt den Namen zurück.</summary>
    public static string Remove(string dataDirectory, string id)
    {
        var user = ReadUser(dataDirectory);
        var entry = user.FirstOrDefault(x => Same(x.Id, id)) ?? throw new KeyNotFoundException(L.T("Profil unbekannt oder unverändert."));
        user.Remove(entry);
        WriteUser(dataDirectory, user);
        return entry.Name;
    }

    /// <summary>Neues Profil als Kopie eines vorhandenen (Kennung und Name eindeutig gemacht).</summary>
    public static DeviceProfile CopyOf(DeviceProfile source, IEnumerable<string> takenIds)
    {
        var taken = takenIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = source.Clone();
        copy.IsFallback = false;
        var baseId = (source.Id.Length > 34 ? source.Id[..34] : source.Id) + "-eigen";
        copy.Id = baseId;
        for (var i = 2; taken.Contains(copy.Id); i++) copy.Id = $"{baseId}{i}";
        copy.Name = L.T("{0} (eigen)", source.Name);
        copy.BasedOn = source.BasedOn ?? source.Id;
        // Eine Kopie soll Geräte nicht automatisch übernehmen – Erkennung leer, Auswahl von Hand
        copy.DeviceModelMatches = [];
        copy.BoardVersions = [];
        return copy;
    }

    private static DeviceProfile Normalize(DeviceProfile p)
    {
        var c = p.Clone();
        c.Id = (c.Id ?? "").Trim().ToLowerInvariant();
        c.Name = (c.Name ?? "").Trim();
        c.Family = (c.Family ?? "").Trim();
        c.AsicModel = (c.AsicModel ?? "").Trim();
        c.Notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim();
        c.DeviceModelMatches = c.DeviceModelMatches.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.BoardVersions = c.BoardVersions.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        c.IsFallback = false;
        return c;
    }

    private static List<DeviceProfile> ReadUser(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, ProfileRegistry.UserFileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<DeviceProfile>>(File.ReadAllText(path), ProfileRegistry.JsonOptions) ?? [] : [];
    }

    private static void WriteUser(string dataDirectory, List<DeviceProfile> user)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, ProfileRegistry.UserFileName);
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(user, ProfileRegistry.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static List<string> Changes(DeviceProfile? before, DeviceProfile after)
    {
        var list = new List<string>();
        if (before is null) return list;
        void C<T>(string label, T a, T b, string unit)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b)) list.Add($"{label}: {a}{unit} → {b}{unit}");
        }
        C(L.T("Name"), before.Name, after.Name, "");
        C(L.T("Min. Frequenz"), before.MinFrequencyMhz, after.MinFrequencyMhz, " MHz");
        C(L.T("Standard-Frequenz"), before.DefaultFrequencyMhz, after.DefaultFrequencyMhz, " MHz");
        C(L.T("Max. Frequenz"), before.MaxFrequencyMhz, after.MaxFrequencyMhz, " MHz");
        C(L.T("Min. Spannung"), before.MinVoltageMv, after.MinVoltageMv, " mV");
        C(L.T("Standard-Spannung"), before.DefaultVoltageMv, after.DefaultVoltageMv, " mV");
        C(L.T("Max. Spannung"), before.MaxVoltageMv, after.MaxVoltageMv, " mV");
        C(L.T("Max. Chiptemperatur"), before.MaxChipTempC, after.MaxChipTempC, " °C");
        C(L.T("Max. VR-Temperatur"), before.MaxVrTempC, after.MaxVrTempC, " °C");
        C(L.T("Max. Leistung"), before.MaxPowerW, after.MaxPowerW, " W");
        C(L.T("ASIC-Anzahl"), before.AsicCount, after.AsicCount, "");
        return list;
    }

    private static void Range(List<string> errors, string label, int min, int def, int max, int lo, int hi, string unit)
    {
        if (min < lo || max > hi) errors.Add(L.T("{0}: erlaubt sind {1}–{2}{3}.", label, lo, hi, Unit(unit)));
        if (!(min <= def && def <= max)) errors.Add(L.T("{0}: Minimum ≤ Standard ≤ Maximum.", label));
    }

    private static void Between(List<string> errors, string label, double v, double lo, double hi, string unit)
    {
        if (v < lo || v > hi) errors.Add(L.T("{0}: erlaubt sind {1}–{2}{3}.", label, lo, hi, Unit(unit)));
    }

    private static string Unit(string unit) => unit.Length > 0 ? " " + unit : "";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameContent(DeviceProfile a, DeviceProfile b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
