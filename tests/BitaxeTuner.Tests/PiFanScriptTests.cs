using System.Diagnostics;

namespace BitaxeTuner.Tests;

/// <summary>
/// 0.9.24: das echte Root-Skript pifan-set (deploy/linux/bitaxetuner-pifan.sh) gegen ein nachgebildetes /sys wie auf dem
/// Raspberry Pi 5 mit Raspberry Pi OS: nur Regler step_wise, Schaltpunkte beschreibbar. Ohne sh auf dem Rechner übersprungen.
/// </summary>
public sealed class PiFanScriptTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string Zone => _dir.File("sys/sys/class/thermal/thermal_zone0");
    private string Data => _dir.File("data");

    private static string? Shell()
    {
        foreach (var name in new[] { "sh", @"C:\Program Files\Git\bin\sh.exe" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "-c true") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
                if (p is null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return name;
            }
            catch { /* nicht vorhanden */ }
        }
        return null;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BitaxeTuner.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir ?? throw new InvalidOperationException("Repository nicht gefunden"), .. parts]);
    }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + "\n");
    }

    private string Read(string path) => File.ReadAllText(path).Trim();

    private string Trip(int i) => Read(Path.Combine(Zone, $"trip_point_{i}_temp"));

    public PiFanScriptTests()
    {
        var cooling = _dir.File("sys/sys/class/thermal/cooling_device0");
        Write(Path.Combine(cooling, "type"), "pwm-fan");
        Write(Path.Combine(cooling, "max_state"), "4");
        Write(Path.Combine(cooling, "cur_state"), "0");
        Write(Path.Combine(Zone, "type"), "cpu-thermal");
        Write(Path.Combine(Zone, "policy"), "step_wise");
        Write(Path.Combine(Zone, "available_policies"), "step_wise");   // wie Raspberry Pi OS: kein user_space
        var trips = new[] { ("critical", 110000), ("active", 50000), ("active", 60000), ("active", 67500), ("active", 75000) };
        for (var i = 0; i < trips.Length; i++)
        {
            Write(Path.Combine(Zone, $"trip_point_{i}_type"), trips[i].Item1);
            Write(Path.Combine(Zone, $"trip_point_{i}_temp"), trips[i].Item2.ToString());
        }
        Directory.CreateDirectory(Data);
    }

    public void Dispose() => _dir.Dispose();

    private bool Run(string shell, string request, string mode = "apply")
    {
        if (request.Length > 0) File.WriteAllText(Path.Combine(Data, "pifan-request"), request);
        var psi = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(RepoFile("deploy", "linux", "bitaxetuner-pifan.sh"));
        psi.ArgumentList.Add(mode);
        psi.Environment["BITAXETUNER_PIFAN_TEST_DATA"] = Data.Replace('\\', '/');
        psi.Environment["BITAXETUNER_PIFAN_TEST_SYS"] = _dir.File("sys").Replace('\\', '/');
        using var p = Process.Start(psi)!;
        // Unter voller Testparallelität kann sh deutlich länger brauchen – großzügig warten, dann sauber abbrechen
        if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0;
    }

    [Fact]
    public void Level_is_set_through_the_trip_points_and_factory_values_come_back()
    {
        if (Shell() is not { } sh) return;
        Assert.True(Run(sh, "level 2"));
        // Stufe 2: zwei Schaltpunkte immer überschritten, zwei unerreichbar hoch – der kritische bleibt
        Assert.Equal(["110000", "1000", "2000", "98000", "99000"], Enumerable.Range(0, 5).Select(Trip));
        Assert.Equal("step_wise", Read(Path.Combine(Zone, "policy")));
        Assert.True(File.Exists(Path.Combine(Data, "pifan-trips")));
        Assert.False(File.Exists(Path.Combine(Data, "pifan-error")));

        // nächste Stufe: Werkswerte bleiben gesichert (nicht mit den verschobenen überschrieben)
        Assert.True(Run(sh, "level 4"));
        Assert.Equal(["1000", "2000", "3000", "4000"], Enumerable.Range(1, 4).Select(Trip));
        Assert.True(Run(sh, "level 0"));
        Assert.Equal(["96000", "97000", "98000", "99000"], Enumerable.Range(1, 4).Select(Trip));

        // zurück an die Firmware: Werkswerte wieder da
        Assert.True(Run(sh, "auto"));
        Assert.Equal(["110000", "50000", "60000", "67500", "75000"], Enumerable.Range(0, 5).Select(Trip));
        Assert.False(File.Exists(Path.Combine(Data, "pifan-trips")));
    }

    [Fact]
    public void Guard_restores_the_firmware_when_bitaxetuner_is_silent()
    {
        if (Shell() is not { } sh) return;
        Assert.True(Run(sh, "level 1"));
        var hb = Path.Combine(Data, "pifan-heartbeat");
        File.WriteAllText(hb, "180");
        File.SetLastWriteTimeUtc(hb, DateTime.UtcNow.AddMinutes(-10));
        Assert.True(Run(sh, "", "guard"));
        Assert.Equal("50000", Trip(1));
        Assert.Equal("guard", Read(Path.Combine(Data, "pifan-error")));
        Assert.False(File.Exists(Path.Combine(Data, "pifan-trips")));
    }
}
