using BitaxeTuner.Core.Fans;

namespace BitaxeTuner.Tests;

/// <summary>
/// Pico-Suche unter Linux: /sys/class/tty/ttyACM0 ist ein relativer Link in den Gerätebaum (Fehler vom 02.10.2026:
/// Pi fand den Pico nicht, obwohl /dev/ttyACM0 mit Kennung 2e8a vorhanden war).
/// </summary>
public class PicoPortTests
{
    [Fact]
    public void Vendor_is_found_through_the_relative_sysfs_link()
    {
        using var dir = new TempDir();
        // /sys/devices/platform/usb1/1-1 (idVendor) / 1-1:1.0 (hier 1-1.0, Windows erlaubt keinen Doppelpunkt) (Schnittstelle) / tty / ttyACM0
        var usbDevice = Path.Combine(dir.Path, "devices", "platform", "usb1", "1-1");
        var tty = Path.Combine(usbDevice, "1-1.0", "tty", "ttyACM0");
        Directory.CreateDirectory(tty);
        File.WriteAllText(Path.Combine(usbDevice, "idVendor"), "2e8a\n");
        var ttyClass = Path.Combine(dir.Path, "class", "tty");
        Directory.CreateDirectory(ttyClass);
        var link = Path.Combine(ttyClass, "ttyACM0");
        try
        {
            // wie im Kernel: relativer Link ../../devices/…
            Directory.CreateSymbolicLink(link, Path.Combine("..", "..", "devices", "platform", "usb1", "1-1", "1-1.0", "tty", "ttyACM0"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Windows ohne Recht für symbolische Links – die CI-/Linux-Läufe prüfen es
        }

        Assert.Equal("2e8a", PicoFanDevice.UsbVendorOf(link));
        if (OperatingSystem.IsLinux()) Assert.Equal(["/dev/ttyACM0"], PicoFanDevice.FindPorts(ttyClass));
    }

    [Fact]
    public void Other_usb_serial_devices_are_ignored()
    {
        using var dir = new TempDir();
        var tty = Path.Combine(dir.Path, "1-2", "1-2.0", "tty", "ttyACM1");
        Directory.CreateDirectory(tty);
        File.WriteAllText(Path.Combine(dir.Path, "1-2", "idVendor"), "2341\n");   // z. B. Arduino/3D-Drucker
        Assert.Equal("2341", PicoFanDevice.UsbVendorOf(tty));
        Assert.Null(PicoFanDevice.UsbVendorOf(Path.Combine(dir.Path, "ohne-usb")));
    }
}
