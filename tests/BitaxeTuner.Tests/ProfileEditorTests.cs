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
