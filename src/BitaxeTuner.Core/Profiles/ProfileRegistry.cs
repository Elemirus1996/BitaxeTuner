using System.Text.Json;
using BitaxeTuner.Core.Api;

namespace BitaxeTuner.Core.Profiles;

/// <summary>
/// Hält alle bekannten Geräteprofile (eingebettet + optionale Nutzerdatei) und ordnet einem Miner das passende zu.
/// </summary>
public sealed class ProfileRegistry
{
    public const string UserFileName = "profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
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

    public DeviceProfile Generic => Profiles.First(p => p.Id == "generic");

    /// <summary>Lädt die eingebetteten Profile und überschreibt/ergänzt sie mit <c>profiles.json</c> aus dem Datenordner.</summary>
    public static ProfileRegistry Load(string? dataDirectory)
    {
        var profiles = LoadBuiltIn();
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
                    var idx = profiles.FindIndex(p => string.Equals(p.Id, up.Id, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) profiles[idx] = up; else profiles.Add(up);
                }
            }
        }
        return new ProfileRegistry(profiles);
    }

    private static readonly string[] LegacyResources = ["BitaxeTuner.Core.Profiles.DeviceProfiles.v0.1.0.json"];

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
            ?? throw new InvalidOperationException("Eingebettete Geräteprofile fehlen.");
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
            profile.Name = $"Generisch ({info.DeviceModel ?? profile.AsicModel})";
            if ((asic?.DefaultFrequencyMhz ?? info.DefaultFrequencyMhz) is { } df and > 0)
                profile.DefaultFrequencyMhz = df;
            if ((asic?.DefaultVoltageMv ?? info.DefaultCoreVoltageMv) is { } dv and > 0)
                profile.DefaultVoltageMv = dv;
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
