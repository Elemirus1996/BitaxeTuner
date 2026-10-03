using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Core.Host;

// Geräteprofile bearbeiten (0.9.9): gemeinsam für Browser und Desktop
public sealed partial class MinerHub
{
    /// <summary>Ordner mit <c>profiles.json</c>.</summary>
    public string ProfilesDirectory => _tuningDirectory;

    public List<ProfileEntry> ListProfiles() => ProfileEditor.List(_tuningDirectory);

    /// <summary>
    /// Profil speichern. Liegen Grenzen über dem eingebauten Profil und ist <paramref name="confirmed"/> false, wird nichts
    /// gespeichert – die Rückgabe enthält dann die Hinweise für die Bestätigung.
    /// </summary>
    public ProfileCheck SaveProfile(DeviceProfile profile, string? originalId, bool confirmed)
    {
        if (Devices.Any(d => d.IsBenchmarkRunning && Same(d.Profile.Id, originalId ?? profile.Id)))
            throw new InvalidOperationException(L.T("Während eines Benchmarks mit diesem Profil nicht möglich."));
        var check = ProfileEditor.Save(_tuningDirectory, profile, originalId, confirmed);
        if (check.Warnings.Count > 0 && !confirmed) return check;

        var text = originalId is null
            ? L.T("Geräteprofil angelegt: „{0}“ ({1}), Frequenz {2}–{3} MHz, Spannung {4}–{5} mV", profile.Name, profile.Id,
                profile.MinFrequencyMhz, profile.MaxFrequencyMhz, profile.MinVoltageMv, profile.MaxVoltageMv)
            : L.T("Geräteprofil geändert: „{0}“ – {1}", profile.Name, check.Changes.Count > 0 ? string.Join("; ", check.Changes) : L.T("Erkennung/Notiz"));
        if (check.Warnings.Count > 0) text += " – " + L.T("bestätigt: {0}", string.Join(" ", check.Warnings));
        LogEvent(null, EventCategories.Settings, text);
        ApplyProfiles(profile.Id);
        return check;
    }

    /// <summary>Eigenes Profil löschen bzw. angepasstes eingebautes zurücksetzen.</summary>
    public void RemoveProfile(string id)
    {
        if (Devices.Any(d => d.IsBenchmarkRunning && Same(d.Profile.Id, id)))
            throw new InvalidOperationException(L.T("Während eines Benchmarks mit diesem Profil nicht möglich."));
        var builtIn = ProfileRegistry.LoadBuiltIn().Any(p => Same(p.Id, id));
        var name = ProfileEditor.Remove(_tuningDirectory, id);
        LogEvent(null, EventCategories.Settings, builtIn
            ? L.T("Geräteprofil „{0}“ auf den eingebauten Stand zurückgesetzt", name)
            : L.T("Geräteprofil „{0}“ gelöscht", name));
        if (!builtIn)
        {
            // Miner, denen das gelöschte Profil fest zugewiesen war, werden wieder automatisch erkannt
            foreach (var d in Devices.Where(d => Same(d.Config.ProfileId, id)))
                d.Config.ProfileId = null;
            Config.Save();
        }
        ApplyProfiles(id);
    }

    /// <summary>Neu laden und betroffene Miner sofort umstellen (fest gewählt: neuer Stand; erkannt: beim nächsten Abruf neu erkennen).</summary>
    private void ApplyProfiles(string id)
    {
        ReloadProfiles();
        foreach (var d in Devices)
        {
            if (!Same(d.Profile.Id, id) && !Same(d.Config.ProfileId, id) && !Same(d.MatchedProfile?.Id, id)) continue;
            if (d.Config.ProfileId is { } chosen && Profiles.Profiles.FirstOrDefault(p => Same(p.Id, chosen)) is { } p)
            {
                d.Profile = p.Clone();
                d.ProfileResolved = true;
            }
            else
            {
                d.ProfileResolved = false; // nächster Datenpunkt ordnet neu zu
            }
            RaiseDeviceChanged(d);
        }
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
