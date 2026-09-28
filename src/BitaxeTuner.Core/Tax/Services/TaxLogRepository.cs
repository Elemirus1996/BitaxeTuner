using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>
/// Speichert Wallet-Adressen und dokumentierte Zuflüsse als JSON unter
/// &lt;Datenordner&gt;\tax\ (Standard %AppData%\BitaxeMonitor\tax\) und exportiert CSV für die Steuererklärung.
/// Das Dateiformat ist unverändert gegenüber BitaxeMonitor.
///
/// Beim ersten Start werden Daten aus dem eigenständigen Prototyp
/// (%AppData%\BitaxeTaxMonitor\) übernommen, falls vorhanden.
/// </summary>
public sealed class TaxLogRepository
{
    private readonly string _walletsPath;
    private readonly string _rewardsPath;
    private readonly string _ignoredPath;
    private readonly string _disposalsPath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public TaxLogRepository(string? baseDirectory = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var baseDir = baseDirectory ?? DataPaths.TaxDirectory;
        Directory.CreateDirectory(baseDir);

        _walletsPath = Path.Combine(baseDir, "wallets.json");
        _rewardsPath = Path.Combine(baseDir, "rewards.json");
        _ignoredPath = Path.Combine(baseDir, "ignored-txids.json");
        _disposalsPath = Path.Combine(baseDir, "disposals.json");

        if (baseDirectory is null)
            MigrateFromPrototype(Path.Combine(appData, "BitaxeTaxMonitor"));
    }

    public string RewardsPath => _rewardsPath;

    /// <summary>Hinweis für die Statuszeile, falls beim Start migriert wurde.</summary>
    public string? MigrationNote { get; private set; }

    // ---------- Wallets ----------

    public List<WalletAddress> LoadWallets() => Load<WalletAddress>(_walletsPath);

    public void SaveWallets(IEnumerable<WalletAddress> wallets) => Save(_walletsPath, wallets.ToList());

    // ---------- Rewards ----------

    public List<MinedReward> LoadRewards() => Load<MinedReward>(_rewardsPath);

    public void SaveRewards(IEnumerable<MinedReward> rewards) => Save(_rewardsPath, rewards.ToList());

    // ---------- Verkäufe ----------

    public List<Disposal> LoadDisposals() => Load<Disposal>(_disposalsPath);

    public void SaveDisposals(IEnumerable<Disposal> disposals) => Save(_disposalsPath, disposals.ToList());

    /// <summary>
    /// Bereits dokumentierte und bewusst ignorierte TXIDs, jeweils mit Coin
    /// (Transaktionen vor dem BCH-Fork haben auf beiden Chains dieselbe TXID).
    /// </summary>
    public HashSet<string> LoadKnownTxKeys()
    {
        var keys = LoadRewards().Select(r => TxKey(r.Coin, r.TxId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(Load<string>(_ignoredPath));
        return keys;
    }

    /// <summary>TXID dauerhaft ausschließen, z. B. für einen Eingang, der kein Mining-Ertrag ist.</summary>
    public void AddIgnored(CoinType coin, string txId)
    {
        lock (_lock)
        {
            var list = Load<string>(_ignoredPath);
            var key = TxKey(coin, txId);
            if (!list.Contains(key, StringComparer.OrdinalIgnoreCase)) list.Add(key);
            Save(_ignoredPath, list);
        }
    }

    public static string TxKey(CoinType coin, string txId) => $"{coin.Symbol()}:{txId}";

    // ---------- CSV ----------

    /// <summary>
    /// Semikolon-getrennt, deutsches Zahlenformat, UTF-8 mit BOM (Excel-tauglich).
    /// Datum und Uhrzeit in Ortszeit, zusätzlich UTC zur eindeutigen Zuordnung.
    /// </summary>
    public void ExportCsv(string filePath, IEnumerable<MinedReward> rewards)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var sb = new StringBuilder();
        sb.AppendLine("Datum;Uhrzeit;Zeitpunkt UTC;Coin;Wallet;Blockhöhe;TXID;Menge;EUR-Kurs;EUR-Wert;Kursquelle;Haltefrist endet;Restbestand;Notiz");

        foreach (var r in rewards.OrderBy(r => r.ReceivedAtUtc))
        {
            var local = r.ReceivedAtUtc.ToLocalTime();
            sb.AppendLine(string.Join(';',
                local.ToString("dd.MM.yyyy", de),
                local.ToString("HH:mm:ss", de),
                r.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Coin.Symbol(),
                Escape(r.WalletLabel),
                r.BlockHeight?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.TxId,
                r.Amount.ToString("0.00000000", de),
                r.EurPriceAtReceipt?.ToString("0.00", de) ?? "",
                r.EurValue?.ToString("0.00", de) ?? "",
                Escape(r.PriceSource),
                r.TaxFreeFrom.AddDays(-1).ToString("dd.MM.yyyy", de),
                r.Remaining.ToString("0.00000000", de),
                Escape(r.Note)));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Verkäufe mit FIFO-Ergebnis als CSV.</summary>
    public void ExportDisposalsCsv(string filePath, IEnumerable<DisposalResult> results)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var sb = new StringBuilder();
        sb.AppendLine("Datum;Coin;Menge;Erlös EUR;Anschaffungskosten EUR;steuerpfl. Menge;haltefristfreie Menge;steuerpfl. Gewinn EUR;Hinweis;Notiz");

        foreach (var r in results.OrderBy(r => r.Disposal.SoldAtUtc))
        {
            var hint = new List<string>();
            if (r.MissingPrice) hint.Add(L.T("Kurs fehlt bei mind. einem Zufluss"));
            if (r.UnmatchedAmount > 0) hint.Add(L.T("{0} ohne Zufluss", r.UnmatchedAmount.ToString("0.00000000", de)));

            sb.AppendLine(string.Join(';',
                r.Disposal.SoldAtLocal.ToString("dd.MM.yyyy", de),
                r.Disposal.Coin.Symbol(),
                r.Disposal.Amount.ToString("0.00000000", de),
                r.Disposal.ProceedsEur.ToString("0.00", de),
                r.CostBasisEur.ToString("0.00", de),
                r.TaxableAmount.ToString("0.00000000", de),
                r.TaxFreeAmount.ToString("0.00000000", de),
                r.TaxableGainEur.ToString("0.00", de),
                Escape(string.Join(", ", hint)),
                Escape(r.Disposal.Note)));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
        => value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    // ---------- Migration ----------

    private void MigrateFromPrototype(string oldDir)
    {
        try
        {
            if (!Directory.Exists(oldDir)) return;

            var moved = new List<string>();
            foreach (var (name, target) in new[] { ("wallets.json", _walletsPath), ("rewards.json", _rewardsPath) })
            {
                var source = Path.Combine(oldDir, name);
                if (File.Exists(source) && !File.Exists(target))
                {
                    File.Copy(source, target);
                    moved.Add(name);
                }
            }

            if (moved.Count > 0)
                MigrationNote = L.T("Daten aus dem Prototyp übernommen ({0}). Der alte Ordner bleibt unverändert.", string.Join(", ", moved));
        }
        catch (Exception ex)
        {
            MigrationNote = L.T("Übernahme aus dem Prototyp fehlgeschlagen: ") + ex.Message;
        }
    }

    // ---------- intern ----------

    private List<T> Load<T>(string path)
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return new List<T>();
            try
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? new List<T>();
            }
            catch
            {
                // Defekte Datei nicht überschreiben, sondern sichern
                File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
                return new List<T>();
            }
        }
    }

    private void Save<T>(string path, List<T> items)
    {
        lock (_lock)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
