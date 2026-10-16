using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Pools;

namespace BitaxeTuner.Server.Api;

public sealed record ExtraPoolsRequest(List<ExtraPoolAccount>? Accounts, Dictionary<string, string>? Secrets);

public sealed record ExtraPoolTestRequest(ExtraPoolAccount? Account, string? Secret);

/// <summary>0.9.20: weitere Pool-Konten (public-pool, solo.ckpool, OCEAN, Braiins, ViaBTC). Schlüssel nur schreibbar. Nur Admin.</summary>
public static class ExtraPoolEndpoints
{
    public static object Status(MinerHub h) => new
    {
        accounts = h.ExtraPools.Select(v => new
        {
            v.Account.Id, v.Account.Provider, v.Account.Name, v.Account.Enabled, v.Account.Address, v.Account.BaseUrl, v.Account.Coin,
            v.Account.TaxImport, v.Account.NotifyPayouts, displayName = v.Account.DisplayName, v.KeySet, lastPoll = v.LastPollUtc, v.Error,
            status = v.Status is { } s ? StatusJson(s) : null,
        }).ToList(),
        providers = ExtraPoolAccount.Providers,
    };

    private static object StatusJson(ExtraPoolStatus s) => new
    {
        s.Coin, s.HashrateGh, s.Hashrate24hGh, s.ActiveWorkers, s.BestDifficulty, s.Balance,
        workers = s.Workers.Select(w => new { w.Name, w.HashrateGh, lastShare = w.LastShareUtc, w.BestDifficulty }).ToList(),
        earnings = s.Earnings.OrderByDescending(e => e.TimeUtc).Take(10).Select(e => new { time = e.TimeUtc, e.Amount }).ToList(),
        payouts = s.Payouts.OrderByDescending(p => p.TimeUtc).Take(10).Select(p => new { time = p.TimeUtc, p.Amount, p.Tx }).ToList(),
    };

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/extra-pools", async (HubService hub) => Results.Json(await hub.RunAsync(Status)));

        g.MapPut("/extra-pools", async (ExtraPoolsRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            h.SaveExtraPools(req.Accounts ?? [], req.Secrets, "Browser");
            return Status(h);
        })));

        // Einmal abfragen, ohne zu speichern (Schlüssel leer = gespeicherten verwenden)
        g.MapPost("/extra-pools/test", async (ExtraPoolTestRequest req, HubService hub) =>
        {
            var a = req.Account ?? throw new InvalidOperationException(L.N("Einstellungen fehlen."));
            if (!ExtraPoolAccount.Providers.Contains(a.Provider)) throw new LocalizedException("Unbekannter Pool-Anbieter: {0}", a.Provider);
            var status = await hub.RunAsync(h => h.FetchExtraPoolAsync(a, req.Secret));
            return Results.Json(StatusJson(status));
        });
    }
}
