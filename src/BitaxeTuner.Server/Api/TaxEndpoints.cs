using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;

namespace BitaxeTuner.Server.Api;

/// <summary>
/// Steuer-Bereich im Browser (nur Admin): Zuflüsse ansehen und exportieren, seit 0.9.6 auch Wallets, Kurse/Notizen und
/// Verkäufe bearbeiten – gleiche Dateien wie die Desktop-App. Jede Änderung steht im Protokoll (ohne Adressen und Beträge).
/// </summary>
public static class TaxEndpoints
{
    public sealed record WalletRequest(string? Address, CoinType? Coin, string? Label);
    public sealed record RewardRequest(string? Price, string? Note);
    public sealed record DisposalRequest(CoinType Coin, string? Date, string? Amount, string? Proceeds, string? Note, bool AllowOversell);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/tax/rewards", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var cur = Currencies.Of(h.Config);
            var (rewards, disposals, summary, available) = h.TaxEditor.Overview(DateTime.Now, cur.Code);
            return new
            {
                // 0.9.11: Beträge in der gewählten Währung (price/value); die Euro-Felder bleiben für Kompatibilität
                currency = new { code = cur.Code, symbol = cur.Symbol, isEuro = cur.IsEuro },
                wallets = h.TaxMonitor.Wallets,
                rewards = rewards.Select(r => new
                {
                    r.Id, r.ReceivedAtUtc, r.Coin, symbol = r.Coin.Symbol(), r.Amount, r.EurPriceAtReceipt, r.EurValue,
                    price = r.PriceIn(cur.Code), value = r.ValueIn(cur.Code), priceSource = r.SourceIn(cur.Code),
                    manualPrice = r.IsManualIn(cur.Code), r.Note, r.WalletLabel, r.TxId, r.Remaining, r.HoldingStatus, r.TaxFreeFrom,
                }),
                disposals = disposals.Select(d => new
                {
                    d.Id, d.SoldAtUtc, d.Coin, symbol = d.Coin.Symbol(), d.Amount, d.ProceedsEur, d.CostBasisEur, d.TaxableGainEur,
                    d.TaxFreeAmount, d.UnmatchedAmount, d.MissingPrice, d.Note, d.ProceedsCurrency, d.EnteredProceeds, d.OtherCurrency,
                }),
                summary,
                available,
                status = h.TaxMonitor.LastPollUtc,
                warning = h.TaxRepository.Warning,
            };
        })));

        g.MapGet("/tax/rewards.csv", async (HubService hub) =>
        {
            var file = Path.Combine(hub.Settings.DataDirectory, $".export-{Guid.NewGuid():N}.csv");   // Audit N-Sec5
            await hub.RunAsync(h => { h.TaxRepository.ExportCsv(file, h.TaxMonitor.LoadRewards(), h.TaxRepository.LoadDisposals(), Currencies.Of(h.Config).Code); return true; });
            var bytes = await File.ReadAllBytesAsync(file);
            File.Delete(file);
            return Results.File(bytes, "text/csv; charset=utf-8", $"zufluesse-{DateTime.Now:yyyyMMdd}.csv");
        });

        g.MapGet("/tax/disposals.csv", async (HubService hub) =>
        {
            var file = Path.Combine(hub.Settings.DataDirectory, $".export-{Guid.NewGuid():N}.csv");   // Audit N-Sec5
            await hub.RunAsync(h =>
            {
                var code = Currencies.Of(h.Config).Code;
                var results = HoldingCalculator.Apply(h.TaxMonitor.LoadRewards(), h.TaxRepository.LoadDisposals(), code);
                h.TaxRepository.ExportDisposalsCsv(file, results, code);
                return true;
            });
            var bytes = await File.ReadAllBytesAsync(file);
            File.Delete(file);
            return Results.File(bytes, "text/csv; charset=utf-8", $"verkaeufe-{DateTime.Now:yyyyMMdd}.csv");
        });

        // ---------- Wallets ----------

        g.MapPost("/tax/wallets", async (WalletRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var w = h.TaxEditor.AddWallet(req.Address, req.Coin, req.Label);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Wallet „{0}“ ({1}) hinzugefügt.", w.Label, w.Coin.Symbol()));
            return w;
        })));

        g.MapPut("/tax/wallets/{id}", async (string id, WalletRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var w = h.TaxEditor.UpdateWallet(id, req.Label, req.Coin);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Wallet „{0}“ ({1}) geändert.", w.Label, w.Coin.Symbol()));
            return w;
        })));

        g.MapDelete("/tax/wallets/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var w = h.TaxEditor.RemoveWallet(id);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Wallet „{0}“ aus der Überwachung entfernt.", w.Label));
            return new { ok = true };
        })));

        g.MapPost("/tax/wallets/import", async (HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var (added, skipped, ambiguous) = h.TaxEditor.ImportFromMiners(h.MinerWalletCandidates());
            if (added > 0) h.LogEvent(null, EventCategories.Settings, L.T("Steuer: {0} Wallet(s) aus den Minern übernommen.", added));
            return new { added, skipped, ambiguous };
        })));

        g.MapPost("/tax/refresh", async (HubService hub) =>
        {
            var monitor = await hub.RunAsync(h => h.TaxMonitor);
            await monitor.PollOnceAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- Zuflüsse ----------

        g.MapPut("/tax/rewards/{id}", async (string id, RewardRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = h.TaxEditor.UpdateReward(id, req.Price, req.Note, Currencies.Of(h.Config).Code);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Zufluss vom {0} bearbeitet (Kurs/Notiz).", L.Short(r.ReceivedAtUtc.ToLocalTime())));
            return new { ok = true };
        })));

        g.MapDelete("/tax/rewards/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var r = h.TaxEditor.RemoveReward(id);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Zufluss vom {0} entfernt (kein Mining-Ertrag), Transaktion wird künftig ignoriert.", L.Short(r.ReceivedAtUtc.ToLocalTime())));
            return new { ok = true };
        })));

        // ---------- Verkäufe ----------

        g.MapPost("/tax/disposals", async (DisposalRequest req, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = h.TaxEditor.AddDisposal(req.Coin, req.Date, req.Amount, req.Proceeds, req.Note, req.AllowOversell, Currencies.Of(h.Config).Code);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Verkauf vom {0:d} ({1}) erfasst.", d.SoldAtLocal, d.Coin.Symbol()));
            return new { ok = true, d.Id };
        })));

        g.MapDelete("/tax/disposals/{id}", async (string id, HubService hub) => Results.Json(await hub.RunAsync(h =>
        {
            var d = h.TaxEditor.RemoveDisposal(id);
            h.LogEvent(null, EventCategories.Settings, L.T("Steuer: Verkauf vom {0:d} ({1}) gelöscht.", d.SoldAtLocal, d.Coin.Symbol()));
            return new { ok = true };
        })));
    }
}
