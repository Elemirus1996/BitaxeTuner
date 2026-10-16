using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace BitaxeTuner.Core.Display;

/// <summary>0.9.16, Seite „Bitcoin-Netz“: Blockhöhe, letzter Block, Gebühren, nächste Difficulty-Anpassung, Halving.</summary>
public sealed record DisplayBitcoin(long? Height, string? LastPool, DateTime? LastTime, FeesDto? Fees, DisplayDifficulty? Difficulty,
    long NextHalving, long BlocksToHalving, double Subsidy, double NextSubsidy);

/// <summary>0.9.16, Seite „Pool“: Coin je Miner, Guthaben je Coin, letzte Auszahlung, zuverlässigster Pool.</summary>
public sealed record DisplayPoolMiner(string Name, string Coin, string? Pool, string? Mode);
public sealed record DisplayPoolCoin(string Symbol, decimal Balance, decimal? Value);
public sealed record DisplayPoolPage(IReadOnlyList<DisplayPoolMiner> Miners, IReadOnlyList<DisplayPoolCoin> Coins, string Currency,
    DateTime? LastPayout, string? LastPayoutCoin, string? BestPool, string? BestPoolNote)
{
    /// <summary>0.9.20: weitere Pool-Konten (public-pool, ckpool, OCEAN, Braiins, ViaBTC).</summary>
    public IReadOnlyList<Host.ExtraPoolSummary> Extra { get; init; } = [];
}

/// <summary>0.9.16, Seite „Monatsvergleich“: je Monat Strom und – wenn bekannt – Ertrag.</summary>
public sealed record DisplayMonthBar(DateTime Month, double Kwh, decimal Cost, decimal? Income);
public sealed record DisplayMonthCompare(IReadOnlyList<DisplayMonthBar> Months, string Currency);

public static partial class StatusRenderer
{
    // ---------- Bitcoin-Netz ----------

    private static void DrawBitcoin(IImageProcessingContext ctx, DisplayModel m)
    {
        var b = m.Bitcoin!;
        PageHeader(ctx, m, L.T("Bitcoin-Netz"));
        var w = (Width - 32) / 3f;
        Tile(ctx, 16, 76, w, L.T("Blockhöhe"), b.Height is { } h ? h.ToString("N0", De) : "–");
        Tile(ctx, 16 + w, 76, w, L.T("Blockprämie"), $"{b.Subsidy.ToString("0.###", De)} BTC");
        Tile(ctx, 16 + 2 * w, 76, w, L.T("Gebühr schnell"), b.Fees is { } f ? L.T("{0} sat/vB", f.Fastest) : "–");
        var small = Regular.Value.CreateFont(20);
        if (b.LastPool is { } pool && b.LastTime is { } t)
            Text(ctx, Fit(L.T("Letzter Block: {0} · {1:HH:mm}", pool, t), small, Width - 32), small, 16, 144, Ink);
        if (b.Fees is { } fees)
            Text(ctx, Fit(L.T("Gebühren (sat/vB): schnell {0} · 30 min {1} · 1 h {2} · sparsam {3}", fees.Fastest, fees.HalfHour, fees.Hour, fees.Economy), small, Width - 32),
                small, 16, 172, Ink);
        ctx.Fill(Crisp, Rule, new RectangleF(16, 206, Width - 32, 1));

        // Difficulty-Anpassung mit Fortschrittsbalken
        var label = Regular.Value.CreateFont(22);
        var bold = Bold.Value.CreateFont(26);
        if (b.Difficulty is { } d)
        {
            Text(ctx, L.T("Nächste Difficulty-Anpassung"), label, 16, 218, Ink);
            TextRight(ctx, (d.ExpectedChangePercent >= 0 ? "+" : "") + d.ExpectedChangePercent.ToString("0.0", De) + " %", bold, Width - 16, 216, d.ExpectedChangePercent >= 0 ? Red : Ink);
            Bar(ctx, 16, 252, Width - 32, 18, d.ProgressPercent / 100);
            Text(ctx, L.T("noch {0} Blöcke", d.RemainingBlocks.ToString("N0", De)) + (d.Eta is { } eta ? " · " + L.T("ca. {0:ddd} {0:t}", eta) : ""), small, 16, 276, Ink);
        }
        // Halving
        var done = 1 - b.BlocksToHalving / (double)Host.MinerHub.HalvingInterval;
        Text(ctx, L.T("Nächstes Halving (Block {0})", b.NextHalving.ToString("N0", De)), label, 16, 318, Ink);
        var days = b.BlocksToHalving * 10.0 / 1440;
        TextRight(ctx, days >= 730 ? L.T("ca. {0} Jahre", (days / 365).ToString("0.0", De)) : L.T("ca. {0} Tage", Math.Ceiling(days).ToString("N0", De)), bold, Width - 16, 316, Ink);
        Bar(ctx, 16, 352, Width - 32, 18, done);
        Text(ctx, L.T("noch {0} Blöcke · danach {1} BTC je Block", b.BlocksToHalving.ToString("N0", De), b.NextSubsidy.ToString("0.#####", De)), small, 16, 376, Ink);
        DrawFooter(ctx, m);
    }

    private static void Bar(IImageProcessingContext ctx, float x, float y, float w, float h, double fraction)
    {
        ctx.Draw(Crisp, Ink, 2, new RectangleF(x, y, w, h));
        var f = (float)Math.Clamp(fraction, 0, 1);
        if (f > 0) ctx.Fill(Crisp, Ink, new RectangleF(x + 3, y + 3, (w - 6) * f, h - 6));
    }

    // ---------- Pool ----------

    private static void DrawPoolPage(IImageProcessingContext ctx, DisplayModel m)
    {
        var p = m.PoolPage!;
        PageHeader(ctx, m, L.T("Pool"));
        var font = Regular.Value.CreateFont(21);
        var bold = Bold.Value.CreateFont(21);
        var y = 76f;
        // Miner: Name · Coin · Pool/Abrechnung (höchstens 6; mit weiteren Pool-Konten weniger, damit sie Platz haben)
        var maxMiners = p.Extra.Count == 0 ? 6 : p.Coins.Count > 0 ? 2 : 4 - Math.Min(2, p.Extra.Count - 1);
        foreach (var x in p.Miners.Take(maxMiners))
        {
            Text(ctx, Fit(x.Name, bold, 230), bold, 16, y, Ink);
            Text(ctx, x.Coin, bold, 260, y, Ink);
            Text(ctx, Fit(string.Join(" · ", new[] { x.Pool, x.Mode }.Where(s => !string.IsNullOrEmpty(s))), font, Width - 360 - 16), font, 360, y, Ink);
            y += 30;
        }
        if (p.Miners.Count > maxMiners) { Text(ctx, L.T("… und {0} weitere", p.Miners.Count - maxMiners), font, 16, y, Ink); y += 30; }
        y += 4;
        ctx.Fill(Crisp, Rule, new RectangleF(16, y, Width - 32, 1));
        y += 10;
        if (p.Coins.Count > 0)
        {
            Text(ctx, L.T("Guthaben beim Pool"), font, 16, y, Ink);
            y += 30;
            var col = (Width - 32) / Math.Min(4, p.Coins.Count);
            var i = 0;
            foreach (var c in p.Coins.Take(4))
            {
                var x = 16 + i++ * col;
                Text(ctx, c.Symbol, bold, x, y, Ink);
                Text(ctx, Fit(c.Balance.ToString(c.Balance >= 100 ? "N2" : "0.########", De), Bold.Value.CreateFont(26), col - 10), Bold.Value.CreateFont(26), x, y + 26, Ink);
                if (c.Value is { } v) Text(ctx, $"≈ {v.ToString("N2", De)} {p.Currency}", font, x, y + 60, Ink);
            }
            y += 96;
        }
        if (p.LastPayout is { } paid)
        {
            Text(ctx, Fit(L.T("Letzte Auszahlung: {0} · {1:g}", p.LastPayoutCoin ?? "", paid.ToLocalTime()), font, Width - 32), font, 16, y, Ink);
            y += 30;
        }
        // 0.9.20: weitere Pool-Konten – je Zeile Name, Hashrate, Worker, Guthaben (so viele, wie über die Fußzeile passen)
        if (p.Extra.Count > 0 && y <= 380)
        {
            Text(ctx, L.T("Weitere Pool-Konten"), font, 16, y, Ink);
            y += 30;
        }
        foreach (var x in p.Extra)
        {
            if (y > 410) break;
            Text(ctx, Fit(x.Name, bold, 250), bold, 16, y, x.Error is null ? Ink : Red);
            var detail = x.Error is not null && x.HashrateGh is null
                ? L.T("Abfrage fehlgeschlagen")
                : string.Join(" · ", new[]
                {
                    x.HashrateGh is { } gh ? FormatHash(gh) : null,
                    L.T("{0} Worker", x.Workers),
                    x.Balance is { } b ? $"{b.ToString("0.########", De)} {x.Coin}" + (x.Value is { } v ? $" ≈ {v.ToString("N2", De)} {p.Currency}" : "") : null,
                }.Where(s => s is not null));
            TextRight(ctx, Fit(detail, font, Width - 290), font, Width - 16, y, x.Error is null ? Ink : Red);
            y += 30;
        }
        if (p.BestPool is { } best)
        {
            y = Math.Min(y, 384);
            Text(ctx, Fit(L.T("Zuverlässigster Pool (7 Tage): {0}", best), font, Width - 32), font, 16, y, Ink);
            if (p.BestPoolNote is { } note) Text(ctx, Fit(note, font, Width - 32), font, 16, y + 28, Ink);
        }
        DrawFooter(ctx, m);
    }

    // ---------- Monatsvergleich ----------

    private static void DrawMonthCompare(IImageProcessingContext ctx, DisplayModel m)
    {
        var c = m.MonthCompare!;
        PageHeader(ctx, m, L.T("Monatsvergleich"));
        if (c.Months.Count == 0)
        {
            TextCenter(ctx, L.T("Noch keine Verlaufsdaten."), Regular.Value.CreateFont(26), Width / 2f, 200, Ink);
            DrawFooter(ctx, m);
            return;
        }
        var withIncome = c.Months.Any(x => x.Income is not null);
        var small = Regular.Value.CreateFont(17);
        var legend = Regular.Value.CreateFont(19);
        Text(ctx, withIncome ? L.T("schwarz = Stromkosten · rot = Ertrag ({0})", c.Currency) : L.T("Stromkosten je Monat ({0})", c.Currency), legend, 16, 72, Ink);
        var area = new RectangleF(70, 110, Width - 86, 270);
        var max = (double)c.Months.Max(x => Math.Max(x.Cost, x.Income ?? 0));
        if (max <= 0) max = 1;
        ctx.DrawLine(Crisp, Ink, 2, new PointF(area.Left, area.Top), new PointF(area.Left, area.Bottom), new PointF(area.Right, area.Bottom));
        TextRight(ctx, max.ToString("N0", De), small, area.Left - 6, area.Top - 8, Ink);
        TextRight(ctx, "0", small, area.Left - 6, area.Bottom - 10, Ink);
        var slot = area.Width / c.Months.Count;
        var barW = withIncome ? slot * 0.34f : slot * 0.6f;
        for (var i = 0; i < c.Months.Count; i++)
        {
            var mo = c.Months[i];
            var x0 = area.Left + i * slot + slot * 0.12f;
            var hc = (float)((double)mo.Cost / max) * area.Height;
            ctx.Fill(Crisp, Ink, new RectangleF(x0, area.Bottom - hc, barW, hc));
            if (withIncome && mo.Income is { } inc && inc > 0)
            {
                var hi = (float)((double)inc / max) * area.Height;
                ctx.Fill(Crisp, Red, new RectangleF(x0 + barW + 2, area.Bottom - hi, barW, hi));
            }
            TextCenter(ctx, mo.Month.ToString("MMM", De), small, area.Left + (i + 0.5f) * slot, area.Bottom + 4, Ink);
            TextCenter(ctx, mo.Kwh.ToString("0", De), small, area.Left + (i + 0.5f) * slot, area.Bottom + 24, Ink);
        }
        Text(ctx, L.T("Zahlen unter den Monaten: kWh"), small, 16, 422, Ink);
        DrawFooter(ctx, m);
    }
}
