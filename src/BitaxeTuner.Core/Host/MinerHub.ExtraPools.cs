using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Pools;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>Stand eines weiteren Pool-Kontos für Browser und Desktop.</summary>
public sealed record ExtraPoolView(ExtraPoolAccount Account, bool KeySet, ExtraPoolStatus? Status, DateTime? LastPollUtc, string? Error);

/// <summary>
/// 0.9.20: weitere Pool-Konten neben Mining-Dutch (<see cref="ExtraPoolAccount"/>) – nur lesend, alle 10 Minuten.
/// Erträge (OCEAN je Block, Braiins/ViaBTC je abgeschlossenem Tag) gehen auf Wunsch als Zuflüsse in die Steuer – nur
/// ergänzend (<see cref="Tax.Services.WalletMonitorService.ImportExternalRewardsAsync"/>). Neue Auszahlungen melden sich per
/// Push (Kategorie Pool); bei der ersten Abfrage eines Kontos gelten alle bisherigen als bekannt (extra-pools.json).
/// </summary>
/// <summary>0.9.20: Kurzstand eines weiteren Pool-Kontos für Übersicht und E-Paper (ohne Adresse/Schlüssel).</summary>
/// <param name="Value">Guthaben in der eingestellten Währung (Kurs der geschürften Coins), null = kein Kurs.</param>
public sealed record ExtraPoolSummary(string Id, string Name, string Provider, string Coin, double? HashrateGh, double? Hashrate24hGh,
    int Workers, decimal? Balance, decimal? Value, DateTime? LastPayoutUtc, decimal? LastPayout, string? Error, DateTime? LastPollUtc);

public sealed partial class MinerHub
{
    /// <summary>0.9.20: eingeschaltete weitere Pool-Konten mit ihrem letzten Stand (Übersicht-Panel „Pool-Konten“, E-Paper „Pool“).</summary>
    public IReadOnlyList<ExtraPoolSummary> ExtraPoolSummaries()
    {
        var quotes = AllCoinQuotes();   // Gegenwert auch bei ausgeblendetem Kurs
        return ExtraPools.Where(v => v.Account.Enabled).Select(v =>
        {
            var st = v.Status;
            var coin = st?.Coin ?? v.Account.Coin;
            var price = quotes.FirstOrDefault(q => string.Equals(q.Symbol, coin, StringComparison.OrdinalIgnoreCase))?.Price;
            var paid = st?.Payouts.MaxBy(p => p.TimeUtc);
            return new ExtraPoolSummary(v.Account.Id, v.Account.DisplayName, v.Account.Provider, coin, st?.HashrateGh, st?.Hashrate24hGh,
                st?.ActiveWorkers ?? 0, st?.Balance, st?.Balance is { } b && price is { } p ? Math.Round(b * p, 2) : null,
                paid?.TimeUtc, paid?.Amount, v.Error, v.LastPollUtc);
        }).ToList();
    }

    public static readonly TimeSpan ExtraPoolInterval = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (ExtraPoolStatus? Status, DateTime? At, string? Error)> _extraPools = [];
    private HttpClient? _extraPoolHttp;

    /// <summary>Für Tests: eigener HTTP-Handler für die Pool-Abfragen.</summary>
    internal HttpMessageHandler? ExtraPoolHandler { get; set; }

    public event Action? ExtraPoolsUpdated;

    private ExtraPoolClient ExtraPoolClient()
    {
        _extraPoolHttp ??= ExtraPoolHandler is { } h ? new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) } : new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (_extraPoolHttp.DefaultRequestHeaders.UserAgent.Count == 0) _extraPoolHttp.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeTuner");
        return ExtraPoolHandler is null ? new ExtraPoolClient(_extraPoolHttp) : new ExtraPoolClient(_extraPoolHttp) { Pause = (_, _) => Task.CompletedTask };
    }

    public IReadOnlyList<ExtraPoolView> ExtraPools => Config.ExtraPoolAccounts.Select(a =>
    {
        var st = _extraPools.TryGetValue(a.Id, out var s) ? s : default;
        return new ExtraPoolView(a, Secrets.Has(a.SecretKey), st.Status, st.At, st.Error);
    }).ToList();

    private Task? _extraPoolsRun;

    /// <summary>Alle eingeschalteten Konten abfragen (Schleife alle 10 min, nach dem Speichern sofort). Läuft schon eine
    /// Abfrage, wartet der Aufruf auf diese.</summary>
    public Task ExtraPoolsTickAsync()
    {
        if (_extraPoolsRun is { IsCompleted: false } running) return running;
        if (_paused || (!Options.OnlineChecks && ExtraPoolHandler is null)) return Task.CompletedTask;
        return _extraPoolsRun = RunExtraPoolsAsync();
    }

    private async Task RunExtraPoolsAsync()
    {
        try
        {
            foreach (var a in Config.ExtraPoolAccounts.Where(a => a.Enabled).ToList())
                await PollExtraPoolAsync(a);
        }
        finally
        {
            ExtraPoolsUpdated?.Invoke();
        }
    }

    /// <summary>Ein Konto abfragen (auch „Testen“ vor dem Speichern, dann mit <paramref name="secretOverride"/>).</summary>
    public async Task<ExtraPoolStatus> FetchExtraPoolAsync(ExtraPoolAccount a, string? secretOverride = null) =>
        await ExtraPoolClient().FetchAsync(a, string.IsNullOrWhiteSpace(secretOverride) ? Secrets.Get(a.SecretKey) : secretOverride, DateTime.UtcNow);

    private async Task PollExtraPoolAsync(ExtraPoolAccount a)
    {
        var had = _extraPools.TryGetValue(a.Id, out var prev) ? prev.Error : null;
        try
        {
            var status = await FetchExtraPoolAsync(a);
            _extraPools[a.Id] = (status, DateTime.UtcNow, null);
            if (had is not null) LogEvent(null, EventCategories.Connection, L.T("Pool-Konto „{0}“ wieder erreichbar.", a.DisplayName));
            await ExtraPoolPayoutsAsync(a, status);
            if (a.TaxImport && a.HasEarnings && status.Earnings.Count > 0)
            {
                var added = await TaxMonitor.ImportExternalRewardsAsync(status.Earnings.Select(e => ExtraPoolReward(a, status.Coin, e)).ToList(),
                    fetchPrices: Options.OnlineChecks);
                if (added > 0) LogEvent(null, EventCategories.Other, L.T("Pool-Konto „{0}“: {1} neue(r) Zufluss/Zuflüsse für die Steuer.", a.DisplayName, added));
            }
        }
        catch (Exception ex) when (ex is LocalizedException or HttpRequestException or TaskCanceledException or JsonException)
        {
            var msg = ex is LocalizedException le ? L.T(le.Text, le.Args) : MinerPollingService.Shorten(ex);
            _extraPools[a.Id] = (prev.Status, prev.At, msg);
            if (msg != had) LogEvent(null, EventCategories.Connection, L.T("Pool-Konto „{0}“: {1}", a.DisplayName, msg));
        }
    }

    /// <summary>Zufluss für die Steuer (Wallet-Kennung je Konto, TxId = stabile Kennung des Ertrags).</summary>
    internal static MinedReward ExtraPoolReward(ExtraPoolAccount a, string coin, ExtraPoolEarning e) => new()
    {
        WalletAddressId = "pool:" + a.Provider + ":" + a.Id,
        WalletLabel = a.DisplayName,
        Coin = coin == "BCH" ? CoinType.BitcoinCash : CoinType.Bitcoin,
        TxId = e.Key,
        ReceivedAtUtc = e.TimeUtc,
        Amount = e.Amount,
        Note = e.Note,
    };

    private string ExtraPoolStateFile => Path.Combine(DataDirectory, "extra-pools.json");

    /// <summary>Neue Auszahlungen melden; beim ersten Abruf eines Kontos nur merken (alte nicht als neu melden).</summary>
    private async Task ExtraPoolPayoutsAsync(ExtraPoolAccount a, ExtraPoolStatus status)
    {
        Dictionary<string, List<string>> seen;
        try { seen = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(await File.ReadAllTextAsync(ExtraPoolStateFile)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { seen = []; }
        var first = !seen.TryGetValue(a.Id, out var known);
        known ??= [];
        var fresh = status.Payouts.Where(p => !known.Contains(p.Key)).ToList();
        if (fresh.Count == 0 && !first) return;
        if (!first && a.NotifyPayouts && Config.Notifications.Wants(NotifyCategory.Pool))
            foreach (var p in fresh)
                _ = Notify.SendAsync($"extra-pool-payout:{a.Id}:{p.Key}", L.T("Auszahlung vom Pool"),
                    L.T("{0}: {1} {2} ausgezahlt.", a.DisplayName, p.Amount.ToString("0.########", L.Culture), status.Coin),
                    NotifyPriority.Normal, TimeSpan.FromDays(365), category: NotifyCategory.Pool);
        foreach (var p in fresh) LogEvent(null, EventCategories.Other, L.T("{0}: Auszahlung {1} {2}.", a.DisplayName, p.Amount.ToString("0.########", L.Culture), status.Coin));
        known.AddRange(fresh.Select(p => p.Key));
        seen[a.Id] = known.TakeLast(500).ToList();
        var tmp = ExtraPoolStateFile + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(seen));
        File.Move(tmp, ExtraPoolStateFile, overwrite: true);
    }

    /// <summary>Konten prüfen und speichern; Schlüssel nur, wenn neu eingegeben (leer = unverändert); entfernte verlieren ihren Schlüssel.</summary>
    public void SaveExtraPools(IReadOnlyList<ExtraPoolAccount> accounts, IReadOnlyDictionary<string, string>? newSecrets, string by)
    {
        var ids = new HashSet<string>();
        var list = new List<ExtraPoolAccount>();
        foreach (var src in accounts)
        {
            var a = src.Clone();
            if (string.IsNullOrWhiteSpace(a.Id) || !System.Text.RegularExpressions.Regex.IsMatch(a.Id, "^[a-f0-9]{4,32}$") || !ids.Add(a.Id))
                a.Id = Guid.NewGuid().ToString("N")[..8];
            if (!ExtraPoolAccount.Providers.Contains(a.Provider)) throw new LocalizedException("Unbekannter Pool-Anbieter: {0}", a.Provider);
            a.Name = (a.Name ?? "").Trim();
            a.Address = (a.Address ?? "").Trim();
            a.BaseUrl = (a.BaseUrl ?? "").Trim().TrimEnd('/');
            a.Coin = a.Coin == "BCH" && a.Provider == "viabtc" ? "BCH" : "BTC";
            if (a.NeedsAddress && !Pools.ExtraPoolClient.IsAddress(a.Address))
                throw new LocalizedException("{0}: bitte eine gültige Wallet-Adresse eintragen.", a.DisplayName);
            if (a.BaseUrl.Length > 0 && (!Uri.TryCreate(a.BaseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
                throw new LocalizedException("Adresse der Pool-API muss mit http:// oder https:// beginnen.");
            list.Add(a);
        }
        foreach (var removed in Config.ExtraPoolAccounts.Where(o => list.All(n => n.Id != o.Id)))
        {
            Secrets.Set(removed.SecretKey, null);
            _extraPools.Remove(removed.Id);
        }
        foreach (var (id, secret) in newSecrets ?? new Dictionary<string, string>())
            if (list.Any(a => a.Id == id) && !string.IsNullOrWhiteSpace(secret)) Secrets.Set(ExtraPoolAccount.SecretKeyOf(id), secret.Trim());
        Config.ExtraPoolAccounts = list;
        Config.Save();
        LogEvent(null, EventCategories.Settings, L.T("Weitere Pool-Konten gespeichert ({0}) – {1}", list.Count, by));
        _ = ExtraPoolsTickAsync();
    }
}
