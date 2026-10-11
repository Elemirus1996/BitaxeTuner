using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Plugs;

/// <summary>0.9.14: passenden Client je Gerätetyp bauen (Shelly, Tasmota, FRITZ!DECT) – alle nur lesend.</summary>
public static class PlugClients
{
    public static IPlugClient Create(string? type, string host, string? user, string? password, string? ain) => (type ?? "shelly") switch
    {
        "tasmota" => new TasmotaClient(host, user, password),
        "fritz" => new FritzDectClient(host, ain ?? "", user, password),
        "shelly" => new ShellyClient(host, user, password),
        _ => throw new LocalizedException("Unbekannter Plug-Typ: {0}", type ?? ""),
    };
}
