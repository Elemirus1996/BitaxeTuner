using BitaxeTuner.Core.Profiles;
using Xunit;

namespace BitaxeTuner.Tests;

public class ProfileEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bt-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private DeviceProfile Gamma() => ProfileEditor.List(_dir).Single(e => e.Profile.Id == "bitaxe-gamma").Profile.Clone();

    [Fact]
    public void Editing_a_built_in_profile_stores_only_the_difference_and_reset_restores_it()
    {
        var p = Gamma();
        p.MaxChipTempC -= 2;
        var check = ProfileEditor.Save(_dir, p, "bitaxe-gamma", confirmed: false);
        Assert.Empty(check.Warnings);
        Assert.Contains(check.Changes, c => c.Contains("→"));

        var entry = ProfileEditor.List(_dir).Single(e => e.Profile.Id == "bitaxe-gamma");
        Assert.True(entry.IsCustomized);
        Assert.Equal(p.MaxChipTempC, ProfileRegistry.Load(_dir).Profiles.Single(x => x.Id == "bitaxe-gamma").MaxChipTempC);

        // Zurücksetzen: Eintrag weg, eingebauter Stand gilt; vorige Datei als .bak erhalten
        ProfileEditor.Remove(_dir, "bitaxe-gamma");
        Assert.False(ProfileEditor.List(_dir).Single(e => e.Profile.Id == "bitaxe-gamma").IsCustomized);
        Assert.True(File.Exists(Path.Combine(_dir, "profiles.json.bak")));
    }

    [Fact]
    public void Limits_above_the_built_in_profile_need_confirmation()
    {
        var p = Gamma();
        p.MaxVoltageMv += 50;
        var check = ProfileEditor.Save(_dir, p, "bitaxe-gamma", confirmed: false);
        Assert.NotEmpty(check.Warnings);
        Assert.False(File.Exists(Path.Combine(_dir, "profiles.json")));                   // nichts gespeichert
        ProfileEditor.Save(_dir, p, "bitaxe-gamma", confirmed: true);
        Assert.Equal(p.MaxVoltageMv, ProfileRegistry.Load(_dir).Profiles.Single(x => x.Id == "bitaxe-gamma").MaxVoltageMv);
    }

    [Fact]
    public void Invalid_profiles_are_rejected()
    {
        var p = Gamma();
        p.DefaultFrequencyMhz = p.MaxFrequencyMhz + 10;                                     // Standard über Maximum
        Assert.Throws<ArgumentException>(() => ProfileEditor.Save(_dir, p, "bitaxe-gamma", true));
        var q = Gamma();
        q.MaxVoltageMv = 2500;                                                              // außerhalb jeder Plausibilität
        Assert.Throws<ArgumentException>(() => ProfileEditor.Save(_dir, q, "bitaxe-gamma", true));
        var r = Gamma();
        r.Id = "Neu mit Leerzeichen";
        Assert.Throws<ArgumentException>(() => ProfileEditor.Save(_dir, r, null, true));
    }

    [Fact]
    public void A_copy_gets_a_free_id_no_detection_and_can_be_saved_and_deleted()
    {
        var all = ProfileEditor.List(_dir);
        var copy = ProfileEditor.CopyOf(Gamma(), all.Select(e => e.Profile.Id));
        Assert.Equal("bitaxe-gamma-eigen", copy.Id);
        Assert.Empty(copy.DeviceModelMatches);
        ProfileEditor.Save(_dir, copy, null, confirmed: false);
        Assert.Throws<InvalidOperationException>(() => ProfileEditor.Save(_dir, copy, null, true)); // gleiche Kennung
        Assert.Equal("bitaxe-gamma-eigen2", ProfileEditor.CopyOf(Gamma(), ProfileEditor.List(_dir).Select(e => e.Profile.Id)).Id);

        var entry = ProfileEditor.List(_dir).Single(e => e.Profile.Id == copy.Id);
        Assert.False(entry.IsBuiltIn);
        ProfileEditor.Remove(_dir, copy.Id);
        Assert.DoesNotContain(ProfileEditor.List(_dir), e => e.Profile.Id == copy.Id);
    }

    [Fact]
    public void Copies_are_checked_against_their_source_and_hard_ceilings()
    {
        // Audit N-S2: früher erlaubte eine Kopie ohne Rückfrage bis 1500 MHz / 1800 mV / 90 °C
        var copy = ProfileEditor.CopyOf(Gamma(), ProfileEditor.List(_dir).Select(e => e.Profile.Id));
        Assert.Equal("bitaxe-gamma", copy.BasedOn);
        copy.MaxVoltageMv += 50;
        var check = ProfileEditor.Save(_dir, copy, null, confirmed: false);
        Assert.Contains(check.Warnings, w => w.Contains("Bitaxe Gamma"));
        Assert.DoesNotContain(ProfileEditor.List(_dir), e => e.Profile.Id == copy.Id);     // nicht ohne Bestätigung

        // feste Obergrenze je Chip: auch bestätigt nicht
        var wild = ProfileEditor.CopyOf(Gamma(), ProfileEditor.List(_dir).Select(e => e.Profile.Id));
        wild.MaxFrequencyMhz = 1400;                                                        // BM1370: höchstens 1000 × 1,25
        Assert.Throws<ArgumentException>(() => ProfileEditor.Save(_dir, wild, null, confirmed: true));
        wild.MaxFrequencyMhz = 1000;
        wild.MaxChipTempC = 85;                                                             // höchstens 80 °C
        Assert.Throws<ArgumentException>(() => ProfileEditor.Save(_dir, wild, null, confirmed: true));

        // Erkennung in einer Kopie übernimmt Miner automatisch → Rückfrage
        var detect = ProfileEditor.CopyOf(Gamma(), ProfileEditor.List(_dir).Select(e => e.Profile.Id));
        detect.DeviceModelMatches = ["Gamma"];
        Assert.Contains(ProfileEditor.Save(_dir, detect, null, confirmed: false).Warnings, w => w.Contains("Gamma"));
    }

    [Fact]
    public void Hand_edited_profiles_are_checked_when_loading()
    {
        // Audit N-S3: profiles.json von Hand oder per Import – gleiche Grenzen wie im Editor
        Directory.CreateDirectory(_dir);
        var wild = Gamma();
        wild.Id = "umbau";
        wild.Name = "Umbau";
        wild.MaxVoltageMv = 1800;                                                           // über der festen Obergrenze
        var old = Gamma();
        old.MaxFrequencyMhz = 1100;                                                         // über eingebaut, unter Obergrenze
        var odd = Gamma();
        odd.Id = "Mein_Altes";                                                              // alte Schreibweise: bleibt erhalten
        odd.Name = "Mein Altes";
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), System.Text.Json.JsonSerializer.Serialize(new[] { wild, old, odd }));

        var reg = ProfileRegistry.Load(_dir);
        Assert.DoesNotContain(reg.Profiles, p => p.Id == "umbau");
        Assert.Contains(reg.Problems, p => p.Contains("Umbau") && p.Contains("nicht übernommen"));
        Assert.Equal(1100, reg.Profiles.Single(p => p.Id == "bitaxe-gamma").MaxFrequencyMhz);
        Assert.Contains(reg.Problems, p => p.Contains("höhere Grenzen"));
        Assert.Contains(reg.Profiles, p => p.Id == "Mein_Altes");
        Assert.True(File.Exists(Path.Combine(_dir, "profiles.json")));                     // Datei bleibt unangetastet
    }

    [Fact]
    public void Every_built_in_profile_passes_the_editor_check()
    {
        var builtIns = ProfileRegistry.LoadBuiltIn();
        Assert.All(builtIns, b => Assert.Empty(ProfileEditor.Check(b, b, builtIns).Errors));
        Assert.All(builtIns, b => Assert.Empty(ProfileEditor.Check(b, b, builtIns).Warnings));
    }

    [Fact]
    public void Existing_user_entries_are_kept_when_another_profile_is_saved()
    {
        Directory.CreateDirectory(_dir);
        var own = Gamma();
        own.Id = "mein-umbau";
        own.Name = "Mein Umbau";
        own.DeviceModelMatches = [];
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), System.Text.Json.JsonSerializer.Serialize(new[] { own }));

        var p = Gamma();
        p.MaxPowerW -= 1;
        ProfileEditor.Save(_dir, p, "bitaxe-gamma", false);
        var ids = ProfileRegistry.Load(_dir).Profiles.Select(x => x.Id).ToList();
        Assert.Contains("mein-umbau", ids);
    }
}
