using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;

namespace BitaxeTuner.Core.Api;

public static class MinerClientFactory
{
    private static int _simSeed = 1;

    /// <summary>Erzeugt einen echten API-Client oder – für <c>sim</c> / <c>sim:&lt;profil-id&gt;</c> – einen Simulator.</summary>
    public static IMinerClient Create(string address, ProfileRegistry profiles)
    {
        address = address.Trim();
        if (!SimulatedMinerClient.IsSimAddress(address))
            return new AxeOsClient(address);

        var id = address.Contains(':') ? address[(address.IndexOf(':') + 1)..] : "bitaxe-gamma";
        var profile = profiles.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? profiles.Profiles.First(p => p.Id != "generic");
        return new SimulatedMinerClient(profile, Interlocked.Increment(ref _simSeed), $"{SimulatedMinerClient.AddressPrefix}:{profile.Id}");
    }
}
