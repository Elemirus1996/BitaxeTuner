using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Server.Api;

/// <summary>0.9.20: Lüfter des Servers (Raspberry Pi 5) – Stand und Einstellung. Nur Admin.</summary>
public static class PiFanEndpoints
{
    public static object Status(MinerHub h)
    {
        var st = h.PiFan;
        return new
        {
            st.Present, st.Rpm, cpuTemp = st.CpuTemp, st.Level, st.MaxLevel, st.Firmware, st.Wanted, st.Reason, st.Error, st.CanControl,
            settings = Dto.Copy(h.Config.PiFan),
            miners = h.Devices.Select(d => new { host = d.Host, name = d.Title }).ToList(),
        };
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/pifan", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        g.MapPut("/pifan", async (PiFanSettings req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SavePiFan(req ?? throw new InvalidOperationException(L.N("Einstellungen fehlen.")), "Browser");
            return Status(h);
        })));
    }
}
