using BitaxeTuner.Core.Api;
using BitaxeTuner.Core.Profiles;

namespace BitaxeTuner.Core.Simulation;

/// <summary>
/// Simuliert einen Miner auf Basis eines Geräteprofils – für Tests und den Demo-Modus
/// (Adresse <c>sim</c> bzw. <c>sim:&lt;profil-id&gt;</c>).
/// Modell: Oberhalb einer frequenzabhängigen Mindestspannung läuft der Chip stabil, darunter bricht die Hashrate ein.
/// Leistung ∝ f·V², Temperatur steigt mit der Leistung.
/// </summary>
public sealed class SimulatedMinerClient : IMinerClient
{
    public const string AddressPrefix = "sim";

    private readonly DeviceProfile _profile;
    private readonly Random _random;
    private readonly double _chipQualityMv;
    private readonly double _powerFactor;
    private readonly object _lock = new();

    private int _frequency;
    private int _voltage;
    private int _autoFan = 1;
    private int _fanPercent = 60;
    private long _shares;
    private DateTime _bootTime = DateTime.Now;

    public SimulatedMinerClient(DeviceProfile profile, int seed = 42, string? address = null)
    {
        _profile = profile;
        _random = new Random(seed);
        _chipQualityMv = _random.Next(-25, 26);
        _frequency = profile.DefaultFrequencyMhz;
        _voltage = profile.DefaultVoltageMv;
        var typicalPower = profile.MaxPowerW * 0.5;
        _powerFactor = typicalPower / (profile.DefaultFrequencyMhz * Math.Pow(profile.DefaultVoltageMv / 1000.0, 2));
        Address = address ?? $"{AddressPrefix}:{profile.Id}";
    }

    public string Address { get; }

    /// <summary>Zusätzliche Temperatur (z. B. um Überhitzung in Tests zu erzwingen).</summary>
    public double ExtraTempC { get; set; }
    public bool Offline { get; set; }
    public int ApplyCount { get; private set; }
    public int RestartCount { get; private set; }
    public (int Frequency, int Voltage) CurrentSettings { get { lock (_lock) return (_frequency, _voltage); } }

    public static bool IsSimAddress(string address) =>
        address.Equals(AddressPrefix, StringComparison.OrdinalIgnoreCase) ||
        address.StartsWith(AddressPrefix + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mindestspannung, ab der die Frequenz stabil läuft.</summary>
    public double RequiredVoltage(int freq) =>
        _profile.DefaultVoltageMv - 30 + (freq - _profile.DefaultFrequencyMhz) * 0.55 + _chipQualityMv;

    public Task<MinerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Offline) throw new MinerApiException($"{Address}: nicht erreichbar (simuliert)");
        lock (_lock)
        {
            var count = Math.Max(1, _profile.AsicCount);
            var cores = _profile.SmallCoresPerAsic > 0 ? _profile.SmallCoresPerAsic : 1000;
            var expected = _frequency * cores * (double)count / 1000.0;

            var deficit = RequiredVoltage(_frequency) - _voltage;
            var factor = deficit <= 0 ? 0.985 : Math.Max(0.15, 0.985 - deficit * 0.006);
            var hash = expected * factor * (1 + (_random.NextDouble() - 0.5) * 0.03);
            var error = deficit <= 0 ? 0.2 + _random.NextDouble() * 0.3 : 0.5 + deficit * 0.12;

            var power = _powerFactor * _frequency * Math.Pow(_voltage / 1000.0, 2) * (1 + (_random.NextDouble() - 0.5) * 0.02);
            var fanFull = _autoFan == 0 && _fanPercent >= 95;
            // Bei typischer Last (50 % der Maximalleistung) ≈ 57 °C, volle Lüfterleistung kühlt ca. 20 % besser.
            var load = power / Math.Max(1, _profile.MaxPowerW);
            var chipTemp = 35 + load * 45 * (fanFull ? 0.8 : 1.0) + ExtraTempC;
            var vrTemp = chipTemp + 8 + load * 10;
            var inputMv = (_profile.MaxInputVoltageMv ?? 5500) > 6000 ? 12000 : 5100;

            _shares += _random.Next(0, 3);
            return Task.FromResult(new MinerInfo
            {
                Firmware = FirmwareKind.Simulated,
                Hostname = $"sim-{_profile.Id}",
                DeviceModel = _profile.Name,
                AsicModel = _profile.AsicModel,
                AsicCount = count,
                SmallCoreCount = cores,
                FirmwareVersion = "(Demo)",
                HashRateGh = hash,
                ExpectedHashRateGh = expected,
                ErrorPercent = error,
                PowerW = power,
                InputVoltageMv = inputMv - power * 5,
                ChipTempC = chipTemp,
                VrTempC = vrTemp,
                FrequencyMhz = _frequency,
                CoreVoltageMv = _voltage,
                CoreVoltageActualMv = _voltage - 8,
                DefaultFrequencyMhz = _profile.DefaultFrequencyMhz,
                DefaultCoreVoltageMv = _profile.DefaultVoltageMv,
                AutoFanMode = _autoFan,
                FanPercent = _autoFan == 0 ? _fanPercent : (int)Math.Clamp(chipTemp, 30, 100),
                FanRpm = 3000 + _fanPercent * 30,
                SharesAccepted = _shares,
                SharesRejected = _shares / 200,
                UptimeSeconds = (long)(DateTime.Now - _bootTime).TotalSeconds,
            });
        }
    }

    public Task<AsicInfo?> GetAsicInfoAsync(CancellationToken ct = default) => Task.FromResult<AsicInfo?>(new AsicInfo
    {
        AsicModel = _profile.AsicModel,
        DeviceModel = _profile.Name,
        AsicCount = _profile.AsicCount,
        DefaultFrequencyMhz = _profile.DefaultFrequencyMhz,
        DefaultVoltageMv = _profile.DefaultVoltageMv,
    });

    public Task ApplySettingsAsync(int frequencyMhz, int coreVoltageMv, CancellationToken ct = default)
    {
        if (Offline) throw new MinerApiException($"{Address}: nicht erreichbar (simuliert)");
        lock (_lock)
        {
            _frequency = frequencyMhz;
            _voltage = coreVoltageMv;
            ApplyCount++;
        }
        return Task.CompletedTask;
    }

    public Task SetFanAsync(int autoFanMode, int manualPercent, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _autoFan = autoFanMode;
            _fanPercent = manualPercent;
        }
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            RestartCount++;
            _bootTime = DateTime.Now;
        }
        return Task.CompletedTask;
    }
}
