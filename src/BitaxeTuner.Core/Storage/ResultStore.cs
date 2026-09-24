using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.Benchmark;

namespace BitaxeTuner.Core.Storage;

/// <summary>Speichert Benchmark-Läufe als JSON unter <c>&lt;data&gt;\results</c> und exportiert CSV.</summary>
public sealed class ResultStore(string dataDirectory)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public string ResultsDirectory => Path.Combine(dataDirectory, "results");

    public string PathFor(BenchmarkSession session)
    {
        var name = Sanitize(session.Hostname ?? session.DeviceAddress);
        return Path.Combine(ResultsDirectory, $"{name}_{session.StartedAt:yyyyMMdd-HHmmss}.json");
    }

    public async Task SaveAsync(BenchmarkSession session)
    {
        Directory.CreateDirectory(ResultsDirectory);
        var path = PathFor(session);
        var tmp = path + ".tmp";
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(session, JsonOptions)).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public IReadOnlyList<BenchmarkSession> LoadAll()
    {
        if (!Directory.Exists(ResultsDirectory)) return [];
        var list = new List<BenchmarkSession>();
        foreach (var file in Directory.EnumerateFiles(ResultsDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BenchmarkSession>(File.ReadAllText(file), JsonOptions) is { } s)
                    list.Add(s);
            }
            catch (JsonException) { /* defekte Datei überspringen */ }
            catch (IOException) { }
        }
        return list.OrderByDescending(s => s.StartedAt).ToList();
    }

    /// <summary>Letzter Lauf für ein Gerät (per Adresse oder Hostname).</summary>
    public BenchmarkSession? LoadLatest(string address, string? hostname) =>
        LoadAll().FirstOrDefault(s =>
            string.Equals(s.DeviceAddress, address, StringComparison.OrdinalIgnoreCase) ||
            (hostname is not null && string.Equals(s.Hostname, hostname, StringComparison.OrdinalIgnoreCase)));

    public static void ExportCsv(BenchmarkSession session, string path)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("timestamp;frequency_mhz;core_voltage_mv;outcome;hashrate_ghs;expected_ghs;ratio;power_w;efficiency_jth;avg_chip_temp_c;max_chip_temp_c;avg_vr_temp_c;max_vr_temp_c;error_percent;input_voltage_mv;samples;message");
        foreach (var r in session.Results)
        {
            sb.AppendLine(string.Join(';',
                r.Timestamp.ToString("s", c), r.FrequencyMhz, r.CoreVoltageMv, r.Outcome,
                F(r.AvgHashRateGh), F(r.ExpectedHashRateGh), F(r.HashRateRatio), F(r.AvgPowerW), F(r.EfficiencyJth),
                F(r.AvgChipTempC), F(r.MaxChipTempC), F(r.AvgVrTempC), F(r.MaxVrTempC), F(r.AvgErrorPercent),
                F(r.AvgInputVoltageMv), r.SampleCount, (r.Message ?? "").Replace(';', ',')));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

        static string F(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) || ch == ':' ? '_' : ch).ToArray());
    }
}
