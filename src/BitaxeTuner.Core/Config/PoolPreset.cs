namespace BitaxeTuner.Core.Config;

/// <summary>
/// 0.9.14 Pool-Steuerung: eine gespeicherte Pool-Vorlage, die auf Miner als Haupt- oder Ersatz-Pool übertragen wird.
/// Gespeichert in config.json („PoolPresets“, additiv); das Passwort liegt als Verweis in secrets.json (<see cref="ConfigSecrets"/>).
/// </summary>
public sealed class PoolPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>Stratum-Adresse ohne Port, z. B. „stratum.example.org“ (ein vorangestelltes „stratum+tcp://“ wird entfernt).</summary>
    public string Url { get; set; } = "";
    public int Port { get; set; } = 3333;
    /// <summary>Wallet-Adresse bzw. Pool-Konto, optional mit „.Worker“.</summary>
    public string User { get; set; } = "";
    /// <summary>„keep“ = Worker-Namen des Miners behalten, „name“ = Minername als Worker, „none“ = Benutzer genau so übernehmen.</summary>
    public string Worker { get; set; } = "keep";
    /// <summary>Pool-Passwort (meist „x“); leer = das auf dem Miner gespeicherte Passwort bleibt.</summary>
    public string Password { get; set; } = "";
    /// <summary>ESP-Miner „stratumTLS“: 0 = aus, 1 = TLS, 2 = TLS mit eigenem Zertifikat. Nur Firmware, die das Feld kennt.</summary>
    public int Tls { get; set; }
    public string Notes { get; set; } = "";

    public static readonly string[] WorkerModes = ["keep", "name", "none"];

    public string Text => $"{Url}:{Port}";

    /// <summary>Pool-Benutzer für einen Miner: Wallet aus der Vorlage, Worker je nach <see cref="Worker"/>.</summary>
    public string UserFor(string minerName, string currentUser)
    {
        var user = User.Trim();
        switch (Worker)
        {
            case "none":
                return user;
            case "name":
                var worker = new string(minerName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                return worker.Length == 0 ? user : $"{user.Split('.', 2)[0]}.{worker}";
            default:
                // Worker des Miners behalten; hat der Miner keinen, gilt der Worker aus der Vorlage
                var current = currentUser.Split('.', 2) is [_, var w] && w.Length > 0 ? w : null;
                return current is null ? user : $"{user.Split('.', 2)[0]}.{current}";
        }
    }

    public PoolPreset Clone() => (PoolPreset)MemberwiseClone();
}
