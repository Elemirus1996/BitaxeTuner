namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.20: weiteres Pool-Konto neben Mining-Dutch (<see cref="PoolAccountSettings"/>) – nur lesend. Gespeichert in
/// config.json („ExtraPoolAccounts“, additiv); Zugangs-Token/API-Schlüssel nur in secrets.json (<see cref="SecretKey"/>).
/// <list type="bullet">
/// <item>„public-pool“: public-pool.io (oder eigene Instanz, z. B. Umbrel) – Statistik je Wallet-Adresse, ohne Schlüssel.</item>
/// <item>„ckpool“: solo.ckpool.org (oder kompatibel) – Statistik je Wallet-Adresse, ohne Schlüssel.</item>
/// <item>„ocean“: OCEAN (TIDES) – je Wallet-Adresse ohne Schlüssel; Erträge je Block und Auszahlungen.</item>
/// <item>„braiins“: Braiins Pool – Zugangs-Token (Lesezugriff); Guthaben, Tageserträge, Auszahlungen.</item>
/// <item>„viabtc“: ViaBTC – API-Schlüssel (nur lesen, IP-Freigabe beim Pool); Tageserträge, Auszahlungen.</item>
/// </list>
/// </summary>
public sealed class ExtraPoolAccount
{
    public static readonly string[] Providers = ["public-pool", "ckpool", "ocean", "braiins", "viabtc"];

    /// <summary>Kurzkennung (a–f, 0–9), auch für den Schlüssel in secrets.json und die Steuer-Zuflüsse.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Provider { get; set; } = "public-pool";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Wallet-Adresse (public-pool, ckpool, OCEAN).</summary>
    public string Address { get; set; } = "";

    /// <summary>Eigene Adresse der Pool-API (z. B. eigene public-pool-Instanz); leer = Standard des Anbieters.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Coin bei ViaBTC („BTC“ oder „BCH“); sonst BTC.</summary>
    public string Coin { get; set; } = "BTC";

    /// <summary>Erträge als Zuflüsse in die Steuer übernehmen (OCEAN, Braiins, ViaBTC).</summary>
    public bool TaxImport { get; set; } = true;

    /// <summary>Push bei neuer Auszahlung (Kategorie Pool).</summary>
    public bool NotifyPayouts { get; set; } = true;

    public bool NeedsAddress => Provider is "public-pool" or "ckpool" or "ocean";
    public bool NeedsKey => Provider is "braiins" or "viabtc";
    /// <summary>Liefert der Anbieter Erträge/Auszahlungen (Solo-Pools nicht – dort zahlt der Block direkt).</summary>
    public bool HasEarnings => Provider is "ocean" or "braiins" or "viabtc";

    public string SecretKey => SecretKeyOf(Id);
    public static string SecretKeyOf(string id) => $"ExtraPool.{id}";

    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : Provider switch
    {
        "public-pool" => "public-pool",
        "ckpool" => "solo.ckpool",
        "ocean" => "OCEAN",
        "braiins" => "Braiins Pool",
        "viabtc" => "ViaBTC",
        _ => Provider,
    };

    public ExtraPoolAccount Clone() => (ExtraPoolAccount)MemberwiseClone();
}
