using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Tests;

/// <summary>0.9.25: Kiosk-Nachtmodus.</summary>
public class KioskNightTests
{
    [Theory]
    [InlineData("22:00", "06:30", "23:15", true)]
    [InlineData("22:00", "06:30", "03:00", true)]
    [InlineData("22:00", "06:30", "06:30", false)]
    [InlineData("22:00", "06:30", "12:00", false)]
    [InlineData("01:00", "05:00", "04:59", true)]
    [InlineData("01:00", "05:00", "05:00", false)]
    [InlineData("07:00", "07:00", "07:00", false)]
    public void Night_window_works_across_midnight(string from, string to, string now, bool expected)
    {
        var n = new KioskNight { Enabled = true, From = from, To = to };
        Assert.Equal(expected, n.IsNight(TimeOnly.Parse(now)));
        Assert.False(Disabled(n).IsNight(TimeOnly.Parse(now)));
    }

    private static KioskNight Disabled(KioskNight n) => new() { Enabled = false, From = n.From, To = n.To };

    [Fact]
    public void Validation_normalizes_and_rejects_bad_times()
    {
        var d = KioskDesigns.Create("Flur", "bitcoin");
        d.Night = new KioskNight { Enabled = true, From = "7:5", To = "23:00", Mode = "irgendwas", Brightness = 3 };
        KioskDesigns.Validate(d);
        Assert.Equal(("07:05", "23:00", "dim", 10), (d.Night.From, d.Night.To, d.Night.Mode, d.Night.Brightness));

        d.Night = null!;   // ältere Designs ohne Eintrag
        KioskDesigns.Validate(d);
        Assert.False(d.Night.Enabled);

        d.Night = new KioskNight { From = "25:00" };
        Assert.Throws<LocalizedException>(() => KioskDesigns.Validate(d));
    }
}
