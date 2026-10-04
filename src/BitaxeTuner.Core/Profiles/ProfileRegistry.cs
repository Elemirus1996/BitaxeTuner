using System.Text.Json;
using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Profiles;

/// <summary>
/// Hält alle bekannten Geräteprofile (eingebettet + optionale Nutzerdatei) und ordnet einem Miner das passende zu.
/// </summary>
public sealed class ProfileRegistry
{
    public const string UserFileName = "profiles.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public ProfileRegistry(IEnumerable<DeviceProfile> profiles)
    {
        Profiles = profiles.ToList();
        if (Profiles.All(p => p.Id != "generic"))
            Profiles.Add(new DeviceProfile());
    }

    public List<DeviceProfile> Profiles { get; }

    /// <summary>
    /// Beim Laden aufgefallene eigene Profile (Audit N-S3): verworfen (ungültig oder über der festen Obergrenze) oder
    /// übernommen, aber über dem eingebauten Profil. Der Hub schreibt sie ins Protokoll.
    /// </summary>
    public List<string> Problems { get; } = [];

    public DeviceProfile Generic => Profiles.First(p => p.Id == "generic");

    /// <summary>Lädt die eingebetteten Profile und überschreibt/ergänzt sie mit <c>profiles.json</c> aus dem Datenordner.</summary>
    public static ProfileRegistry Load(string? dataDirectory)
    {
        var profiles = LoadBuiltIn();
        var builtIns = LoadBuiltIn();
        var problems = new List<string>();
        if (dataDirectory is not null)
        {
            var userFile = Path.Combine(dataDirectory, UserFileName);
            if (File.Exists(userFile))
            {
                var user = JsonSerializer.Deserialize<List<DeviceProfile>>(File.ReadAllText(userFile), JsonOptions) ?? [];
                var legacy = LoadLegacyBuiltIns();
                foreach (var up in user)
                {
                    // Unveränderte Kopie eines früher eingebauten Profils (z. B. aus "Profile bearbeiten" in v0.1.0):
                    // nicht übernehmen, sonst würden korrigierte eingebaute Werte wieder überdeckt.
                    if (legacy.Any(l => SameContent(l, up))) continue;
                    // Gleiche Prüfung wie im Editor (Audit N-S3): Handbearbeitung oder Datenimport umgehen sie sonst
                    // (previous = das Profil selbst: die Erkennung gilt hier als unverändert, nur Grenzen zählen)
                    up.DeviceModelMatches ??= [];
                    up.BoardVersions ??= [];
                    var check = ProfileEditor.Check(up, up, builtIns, valuesOnly: true);
                    if (check.Errors.Count > 0)
                    {
                        problems.Add(L.T("Geräteprofil „{0}“ aus profiles.json nicht übernommen: {1}", up.Name ?? up.Id ?? "?", string.Join(" ", check.Errors)));
                        continue;
                    }
                    if (check.Warnings.Count > 0)
                        problems.Add(L.T("Geräteprofil „{0}“ aus profiles.json hat höhere Grenzen als vorgesehen: {1}", up.Name, string.Join(" ", check.Warnings)));
                    var idx = profiles.FindIndex(p => string.Equals(p.Id, up.Id, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) profiles[idx] = up; else profiles.Add(up);
                }
            }
        }
        var registry = new ProfileRegistry(profiles);
        registry.Problems.AddRange(problems);
        return registry;
    }

    // Frühere Stände der eingebauten Profile (0.1.0; 0.2.0 bis 0.9.2). Eine unveränderte Kopie davon in profiles.json
    // (entsteht durch „Profile bearbeiten“) darf korrigierte eingebaute Werte nicht überdecken – z. B. GammaDuo 0.9.3.
    private static readonly string[] LegacyResources =
        ["BitaxeTuner.Core.Profiles.DeviceProfiles.v0.1.0.json", "BitaxeTuner.Core.Profiles.DeviceProfiles.legacy.json"];

    private static List<DeviceProfile> LoadLegacyBuiltIns()
    {
        var list = new List<DeviceProfile>();
        foreach (var name in LegacyResources)
        {
            using var stream = typeof(ProfileRegistry).Assembly.GetManifestResourceStream(name);
            if (stream is not null) list.AddRange(JsonSerializer.Deserialize<List<DeviceProfile>>(stream, JsonOptions) ?? []);
        }
        return list;
    }

    private static bool SameContent(DeviceProfile a, DeviceProfile b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public static List<DeviceProfile> LoadBuiltIn()
    {
        using var stream = typeof(ProfileRegistry).Assembly.GetManifestResourceStream("BitaxeTuner.Core.Profiles.DeviceProfiles.json")
            ?? throw new InvalidOperationException(L.T("Eingebettete Geräteprofile fehlen."));
        return JsonSerializer.Deserialize<List<DeviceProfile>>(stream, JsonOptions) ?? [];
    }

    /// <summary>Schreibt die eingebauten Profile als Vorlage in den Datenordner, falls dort noch keine Datei liegt.</summary>
    public static string WriteUserTemplate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, UserFileName);
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(LoadBuiltIn(), JsonOptions));
        return path;
    }

    public DeviceProfile Match(MinerInfo info, AsicInfo? asic = null)
    {
        var profile = FindProfile(info, asic)?.Clone();
        if (profile is null)
        {
            profile = Generic.Clone();
            profile.AsicModel = asic?.AsicModel ?? info.AsicModel ?? "";
            profile.AsicCount = asic?.AsicCount ?? info.AsicCount;
            profile.Name = L.T("Generisch ({0})", info.DeviceModel ?? profile.AsicModel);
            var df = (asic?.DefaultFrequencyMhz ?? info.DefaultFrequencyMhz) is { } f and > 0 ? f : (int?)null;
            var dv = (asic?.DefaultVoltageMv ?? info.DefaultCoreVoltageMv) is { } v and > 0 ? v : (int?)null;
            if (df is { } dff) profile.DefaultFrequencyMhz = dff;
            if (dv is { } dvv) profile.DefaultVoltageMv = dvv;
            Narrow(profile, df, dv);
        }

        // Angaben des Geräts haben Vorrang vor den Tabellenwerten (z. B. BM1373: ESP-Miner 6725, Gaia meldet 6860 Small-Cores)
        if (info.SmallCoreCount is > 0)
            profile.SmallCoresPerAsic = info.SmallCoreCount.Value;
        if (asic?.AsicCount is > 0 && asic.AsicCount != profile.AsicCount)
            profile.AsicCount = asic.AsicCount.Value;
        else if (asic?.AsicCount is null && info.AsicCount > 1 && info.AsicCount != profile.AsicCount)
            profile.AsicCount = info.AsicCount; // NerdQAxe-Firmware: kein /api/system/asic, aber asicCount in info
        return profile;
    }

    /// <summary>
    /// Kein passendes Profil: Die allgemeinen Grenzen (bis 800 MHz / 1300 mV) wären für manche Chips zu hoch –
    /// z. B. BM1373 verträgt laut seinen Profilen höchstens 1040–1100 mV. Daher:
    /// <list type="bullet">
    /// <item>bekannter ASIC (anderes Gerät/andere ASIC-Anzahl): jeweils die engste Grenze aller Profile dieses ASICs,
    /// Leistung je ASIC hochgerechnet</item>
    /// <item>unbekannter ASIC: nur wenig über den Standardwerten des Geräts (+50 MHz / +50 mV), ohne Standardwerte
    /// höchstens 600 MHz / 1200 mV</item>
    /// </list>
    /// </summary>
    private void Narrow(DeviceProfile p, int? defaultFrequency, int? defaultVoltage)
    {
        p.IsFallback = true;
        var family = Profiles.Where(x => x.Id != "generic" && x.AsicModel.Length > 0 &&
                                         string.Equals(x.AsicModel, p.AsicModel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (family.Count > 0)
        {
            p.MinFrequencyMhz = family.Max(x => x.MinFrequencyMhz);
            p.MaxFrequencyMhz = family.Min(x => x.MaxFrequencyMhz);
            p.MinVoltageMv = family.Max(x => x.MinVoltageMv);
            p.MaxVoltageMv = family.Min(x => x.MaxVoltageMv);
            p.MaxChipTempC = family.Min(x => x.MaxChipTempC);
            p.MaxVrTempC = family.Min(x => x.MaxVrTempC);
            p.MaxPowerW = Math.Round(family.Min(x => x.MaxPowerW / Math.Max(1, x.AsicCount)) * Math.Max(1, p.AsicCount), 1);
            if (defaultFrequency is null) p.DefaultFrequencyMhz = family.Min(x => x.DefaultFrequencyMhz);
            if (defaultVoltage is null) p.DefaultVoltageMv = family.Min(x => x.DefaultVoltageMv);
        }
        else
        {
            p.MaxFrequencyMhz = Math.Min(p.MaxFrequencyMhz, defaultFrequency is { } f ? f + 50 : 600);
            p.MaxVoltageMv = Math.Min(p.MaxVoltageMv, defaultVoltage is { } v ? v + 50 : 1200);
        }
        // Standardwerte müssen erreichbar bleiben (Zurücksetzen, Wiederherstellen); Untergrenzen nie über den Obergrenzen
        p.MinFrequencyMhz = Math.Min(p.MinFrequencyMhz, Math.Min(p.DefaultFrequencyMhz, p.MaxFrequencyMhz));
        p.MinVoltageMv = Math.Min(p.MinVoltageMv, Math.Min(p.DefaultVoltageMv, p.MaxVoltageMv));
        p.MaxFrequencyMhz = Math.Max(p.MaxFrequencyMhz, p.DefaultFrequencyMhz);
        p.MaxVoltageMv = Math.Max(p.MaxVoltageMv, p.DefaultVoltageMv);
    }

    private DeviceProfile? FindProfile(MinerInfo info, AsicInfo? asic)
    {
        var deviceModel = asic?.DeviceModel ?? info.DeviceModel;
        if (!string.IsNullOrWhiteSpace(deviceModel))
        {
            var byModel = Profiles
                .SelectMany(p => p.DeviceModelMatches.Select(m => (p, m)))
                .Where(x => deviceModel.Contains(x.m, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.m.Length)
                .Select(x => x.p)
                .FirstOrDefault();
            if (byModel is not null) return byModel;
        }

        var asicModel = asic?.AsicModel ?? info.AsicModel;
        var asicCount = asic?.AsicCount ?? info.AsicCount;

        if (!string.IsNullOrWhiteSpace(info.BoardVersion))
        {
            var byBoard = Profiles
                .SelectMany(p => p.BoardVersions.Select(b => (p, b)))
                .Where(x => info.BoardVersion.StartsWith(x.b, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.b.Length)
                .ThenByDescending(x => string.Equals(x.p.AsicModel, asicModel, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.p)
                .FirstOrDefault();
            if (byBoard is not null) return byBoard;
        }

        if (!string.IsNullOrWhiteSpace(asicModel))
        {
            var preferredFamily = info.Firmware == FirmwareKind.NerdQAxe ? "NerdAxe" : "Bitaxe";
            return Profiles
                .Where(p => string.Equals(p.AsicModel, asicModel, StringComparison.OrdinalIgnoreCase) && p.AsicCount == asicCount)
                .OrderByDescending(p => p.Family == preferredFamily)
                .FirstOrDefault();
        }
        return null;
    }
}
