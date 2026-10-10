using System.Text.Json.Serialization;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.12: Pool-Konto (bisher Mining-Dutch): Coin je Miner aus dem Pool, Guthaben/Worker-Statistik und Pool-Buchungen als
/// Zuflüsse für die Steuer. Der API-Schlüssel steht beim Speichern nur als Verweis in config.json, der Wert in secrets.json
/// (<see cref="ConfigSecrets"/>). Additiv, standardmäßig aus.
/// </summary>
public sealed class PoolAccountSettings
{
    public bool Enabled { get; set; }

    /// <summary>Anbieter; bisher nur „mining-dutch“.</summary>
    public string Provider { get; set; } = "mining-dutch";

    /// <summary>Lese-Schlüssel der Pool-API (Mining-Dutch: „Edit Account“ → API-Key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Abfrage alle … Minuten (10–240). Jede Runde fragt mehrere Coins mit Pausen ab.</summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>Was als Zufluss zählt (Gutschrift je Tag oder Auszahlung).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PoolIncomeBasis TaxBasis { get; set; } = PoolIncomeBasis.Credit;

    /// <summary>Pool-Buchungen als Zuflüsse in die Steuer übernehmen.</summary>
    public bool TaxImport { get; set; } = true;

    public void Normalize()
    {
        ApiKey = (ApiKey ?? "").Trim();
        IntervalMinutes = Math.Clamp(IntervalMinutes, 10, 240);
        if (Provider != "mining-dutch") Provider = "mining-dutch";
        if (!Enum.IsDefined(TaxBasis)) TaxBasis = PoolIncomeBasis.Credit;
    }
}
