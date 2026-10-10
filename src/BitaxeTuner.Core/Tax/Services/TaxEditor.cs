using System.Globalization;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Tax.Services;

/// <summary>Zeile der Verkaufsübersicht: Verkauf mit FIFO-Ergebnis.</summary>
/// Beträge (Namen mit „Eur“) in der gerechneten Währung (0.9.11); ProceedsCurrency ist die Währung, in der der Erlös erfasst wurde.
public sealed record DisposalView(string Id, DateTime SoldAtUtc, CoinType Coin, decimal Amount, decimal ProceedsEur,
    decimal CostBasisEur, decimal TaxableGainEur, decimal TaxFreeAmount, decimal UnmatchedAmount, bool MissingPrice, string Note,
    string ProceedsCurrency = "EUR", decimal? EnteredProceeds = null, bool OtherCurrency = false);

/// <summary>Jahreswerte wie in der Desktop-App (Zuflüsse; Verkäufe nach § 23 EStG mit Freigrenze), Beträge in Currency.</summary>
public sealed record TaxYearSummary(int Year, int RewardCount, decimal RewardEur, int RewardsWithoutPrice,
    int SaleCount, decimal TaxableGainEur, decimal FreeLimitEur, bool SaleMissingPrice, bool SaleUnmatched, decimal UncertainGainEur = 0,
    string Currency = "EUR", bool SaleOtherCurrency = false);

/// <summary>
/// Steuer-Bereich bearbeiten (0.9.6, Browser im Server-Betrieb): Wallets, Kurs/Notiz eines Zuflusses, Einträge entfernen,
/// Verkäufe erfassen. Gleiche Dateien und Regeln wie die Desktop-App – nur die Bedienung ist eine andere.
/// Aufruf auf dem Hub-Thread; Fehler als <see cref="LocalizedException"/> (Text in der Sprache der Oberfläche).
/// </summary>
public sealed class TaxEditor(WalletMonitorService monitor, TaxLogRepository repository)
{
    /// <summary>Freigrenze für private Veräußerungsgeschäfte (§ 23 Abs. 3 EStG, ab 2024).</summary>
    public const decimal FreeLimitEur = 1000m;

    // ---------- Wallets ----------

    public WalletAddress AddWallet(string? address, CoinType? coin, string? label)
    {
        var a = (address ?? "").Trim();
        if (a.Length < 20 || a.Length > 120 || a.Any(char.IsWhiteSpace))
            throw new LocalizedException("Bitte eine gültige Wallet-Adresse eingeben.");
        if (monitor.Contains(a)) throw new LocalizedException("Adresse ist bereits eingetragen.");
        var c = coin ?? CoinTypeExtensions.GuessFromAddress(a);
        if (!c.HasWalletSupport()) throw new LocalizedException("Wallet-Abfrage gibt es nur für BTC und BCH – andere Coins kommen über das Pool-Konto.");
        var wallet = new WalletAddress
        {
            Address = a,
            Coin = c,
            Label = string.IsNullOrWhiteSpace(label) ? L.T("{0}-Wallet", c.Symbol()) : label.Trim(),
        };
        if (!monitor.AddWallet(wallet)) throw new LocalizedException("Adresse ist bereits eingetragen.");
        return wallet;
    }

    public WalletAddress UpdateWallet(string id, string? label, CoinType? coin)
    {
        var wallet = Wallet(id);
        if (!string.IsNullOrWhiteSpace(label)) wallet.Label = label.Trim();
        if (coin is { } c)
        {
            if (!c.HasWalletSupport()) throw new LocalizedException("Wallet-Abfrage gibt es nur für BTC und BCH – andere Coins kommen über das Pool-Konto.");
            wallet.Coin = c;
        }
        monitor.UpdateWallet(wallet);
        return wallet;
    }

    /// <summary>Aus der Überwachung nehmen; dokumentierte Zuflüsse bleiben erhalten.</summary>
    public WalletAddress RemoveWallet(string id)
    {
        var wallet = Wallet(id);
        monitor.RemoveWallet(wallet.Id);
        return wallet;
    }

    /// <summary>
    /// Adressen der Miner übernehmen (Gerätename als Bezeichnung). Liefert übernommen, vorhanden und die Namen der Miner
    /// mit Legacy-Adresse, bei denen der Coin nur angenommen ist.
    /// </summary>
    public (int Added, int Skipped, List<string> Ambiguous) ImportFromMiners(IEnumerable<(string Name, string Host, string Address)> miners)
    {
        int added = 0, skipped = 0;
        var ambiguous = new List<string>();
        foreach (var (name, host, address) in miners)
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            if (monitor.Contains(address)) { skipped++; continue; }
            if (!CoinTypeExtensions.IsUnambiguous(address)) ambiguous.Add(name);
            var wallet = new WalletAddress
            {
                Address = address.Trim(),
                Coin = CoinTypeExtensions.GuessFromAddress(address),
                Label = name,
                SourceDeviceHost = host,
            };
            if (monitor.AddWallet(wallet)) added++; else skipped++;
        }
        return (added, skipped, ambiguous);
    }

    private WalletAddress Wallet(string id) =>
        monitor.Wallets.FirstOrDefault(w => w.Id == id) ?? throw new LocalizedException("Wallet nicht gefunden.") { Status = 404 };

    // ---------- Zuflüsse ----------

    /// <summary>
    /// Kurs und/oder Notiz ändern. <paramref name="price"/>: null = unverändert, leer = Kurs entfernen, sonst Kurs je Coin
    /// in <paramref name="currency"/> (0.9.11). Ein geänderter Kurs gilt als manuell und wird nie automatisch überschrieben.
    /// </summary>
    public MinedReward UpdateReward(string id, string? price, string? note, string currency = "EUR")
    {
        var cur = Config.Currencies.Get(currency);
        var reward = monitor.LoadRewards().FirstOrDefault(r => r.Id == id)
                     ?? throw new LocalizedException("Eintrag nicht gefunden.") { Status = 404 };
        if (price is not null)
        {
            decimal? value = null;
            if (price.Trim().Length > 0)
            {
                if (!TryParseEur(price, out var v) || v <= 0 || v > 100_000_000m) throw new LocalizedException("Kurs ungültig.");
                // Audit N-F1: BTC und BCH lagen nie unter 100 € je Coin, seit es Mining-Erträge zu dokumentieren gibt
                if (v < MinPlausiblePriceEur)
                    throw new LocalizedException("Kurs {0} {1} je Coin ist unplausibel niedrig – Tausender bitte mit Punkt und Dezimalstellen mit Komma (z. B. 65.000,00).",
                        v.ToString("0.##", L.Culture), cur.Symbol);
                value = v;
            }
            if (value != reward.PriceIn(cur.Code))
                reward.SetPrice(cur.Code, value, null, value is null ? "" : L.T("manuell eingetragen am {0:d}", DateTime.Now));
        }
        if (note is not null) reward.Note = note.Trim().Length > 500 ? note.Trim()[..500] : note.Trim();
        monitor.SaveReward(reward);
        return reward;
    }

    /// <summary>Eintrag entfernen, der kein Mining-Ertrag ist; die Transaktion wird danach dauerhaft ignoriert.</summary>
    public MinedReward RemoveReward(string id)
    {
        var reward = monitor.LoadRewards().FirstOrDefault(r => r.Id == id)
                     ?? throw new LocalizedException("Eintrag nicht gefunden.") { Status = 404 };
        monitor.RemoveReward(reward);
        return reward;
    }

    // ---------- Verkäufe ----------

    /// <summary>
    /// Verkauf erfassen (Uhrzeit 12:00 lokal, damit ein Verkauf am Zuflusstag nicht vor dem Zufluss einsortiert wird).
    /// Ist mehr verkauft als dokumentiert, nur mit <paramref name="allowOversell"/> – sonst Status 409 mit Hinweis.
    /// </summary>
    public Disposal AddDisposal(CoinType coin, string? date, string? amount, string? proceeds, string? note, bool allowOversell, string currency = "EUR")
    {
        var cur = Config.Currencies.Get(currency);
        if (!DateOnly.TryParseExact(date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day > DateOnly.FromDateTime(DateTime.Now) || day.Year < 2009)
            throw new LocalizedException("Datum ungültig.");
        if (!TryParseDecimal(amount ?? "", out var a) || a <= 0 || a > 21_000_000m) throw new LocalizedException("Menge ungültig.");
        if (!TryParseEur(proceeds ?? "", out var p) || p < 0 || p > 1_000_000_000m) throw new LocalizedException("Erlös ungültig.");

        var rewards = monitor.LoadRewards();
        var disposals = repository.LoadDisposals();
        HoldingCalculator.Apply(rewards, disposals);
        var available = rewards.Where(r => r.Coin == coin).Sum(r => r.Remaining);
        if (a > available && !allowOversell)
            throw new LocalizedException("Verkauft werden sollen {0} {1}, dokumentiert sind nur {2} im Bestand. Trotzdem erfassen? Der Überhang wird als „ohne dokumentierten Zufluss“ markiert.",
                a.ToString("0.00000000", L.Culture), coin.Symbol(), available.ToString("0.00000000", L.Culture)) { Status = 409 };

        var disposal = new Disposal
        {
            Coin = coin,
            SoldAtUtc = TaxTime.FromTax(day.ToDateTime(new TimeOnly(12, 0))),
            Amount = a,
            ProceedsEur = cur.IsEuro ? p : 0,
            Currency = cur.IsEuro ? null : cur.Code,
            Proceeds = cur.IsEuro ? null : p,
            Note = (note ?? "").Trim(),
        };
        disposals.Add(disposal);
        repository.SaveDisposals(disposals);
        return disposal;
    }

    public Disposal RemoveDisposal(string id)
    {
        var disposals = repository.LoadDisposals();
        var disposal = disposals.FirstOrDefault(d => d.Id == id) ?? throw new LocalizedException("Verkauf nicht gefunden.") { Status = 404 };
        disposals.Remove(disposal);
        repository.SaveDisposals(disposals);
        return disposal;
    }

    // ---------- Übersicht ----------

    /// <summary>Zuflüsse mit Restbestand (FIFO), Verkäufe mit Ergebnis, Jahreswerte und Bestand je Coin – Beträge in <paramref name="currency"/>.</summary>
    public (List<MinedReward> Rewards, List<DisposalView> Disposals, TaxYearSummary Summary, Dictionary<string, decimal> Available)
        Overview(DateTime now, string currency = "EUR")
    {
        var cur = Config.Currencies.Get(currency).Code;
        var rewards = monitor.LoadRewards();
        var results = HoldingCalculator.Apply(rewards, repository.LoadDisposals(), cur);
        var year = TaxTime.ToTax(now.ToUniversalTime()).Year;
        var inYear = rewards.Where(r => r.ReceivedAtLocal.Year == year).ToList();
        var sales = results.Where(r => r.Disposal.SoldAtLocal.Year == year).ToList();
        var summary = new TaxYearSummary(year, inYear.Count, inYear.Sum(r => r.ValueIn(cur) ?? 0), inYear.Count(r => r.ValueIn(cur) is null),
            sales.Count, sales.Sum(r => r.TaxableGainEur), FreeLimitEur, sales.Any(r => r.MissingPrice), sales.Any(r => r.UnmatchedAmount > 0),
            sales.Sum(r => r.UncertainGainEur), cur, sales.Any(r => r.OtherCurrency));
        var views = results.OrderByDescending(r => r.Disposal.SoldAtUtc).Select(r => new DisposalView(r.Disposal.Id, r.Disposal.SoldAtUtc,
            r.Disposal.Coin, r.Disposal.Amount, r.Disposal.ProceedsIn(cur) ?? 0, r.CostBasisEur, r.TaxableGainEur, r.TaxFreeAmount,
            r.UnmatchedAmount, r.MissingPrice, r.Disposal.Note, r.Disposal.EnteredCurrency,
            r.Disposal.EnteredCurrency == "EUR" ? r.Disposal.ProceedsEur : r.Disposal.Proceeds, r.OtherCurrency)).ToList();
        var available = Enum.GetValues<CoinType>().ToDictionary(c => c.Symbol(), c => rewards.Where(r => r.Coin == c).Sum(r => r.Remaining));
        return (rewards.OrderByDescending(r => r.ReceivedAtUtc).ToList(), views, summary, available);
    }

    /// <summary>Untergrenze für einen von Hand eingetragenen Kurs (je Coin; gilt für alle wählbaren Währungen).</summary>
    public const decimal MinPlausiblePriceEur = 100m;

    /// <summary>
    /// Euro-Betrag (Kurs, Erlös), Audit N-F1: wie <see cref="TryParseDecimal"/>, aber Punkte vor genau drei Ziffern sind
    /// Tausendertrenner – „65.000“ ist 65 000 €, nicht 65 €; „1.234.567“ ist 1 234 567 €. „12.50“ bleibt 12,50 €.
    /// </summary>
    public static bool TryParseEur(string text, out decimal value)
    {
        var t = StripCurrency(text);
        if (!t.Contains(',') && System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,3}(\.\d{3})+$"))
            t = t.Replace(".", "");
        return TryParseDecimal(t, out value);
    }

    /// <summary>Leerzeichen und Währungszeichen/-codes (€, $, CHF, kr …) entfernen.</summary>
    private static string StripCurrency(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"[\s\p{Sc}]|[A-Za-zÀ-ž]+\.?", "");

    /// <summary>Mit Komma deutsch, ohne Komma mit Punkt als Dezimaltrenner (wie in der Desktop-App); € und Leerzeichen erlaubt.</summary>
    public static bool TryParseDecimal(string text, out decimal value)
    {
        text = StripCurrency(text);
        return text.Contains(',')
            ? decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out value)
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
