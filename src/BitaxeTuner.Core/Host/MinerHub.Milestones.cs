using System.Text.Json;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Network;
using BitaxeTuner.Core.Tax.Models;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.20: eigene Meilensteine als Neuigkeiten (Art „milestone“) – entstehen auf dem eigenen Server und verlassen das
/// Heimnetz nie: neuer Best-Diff-Rekord, erste Auszahlung vom Pool, jedes volle Betriebsjahr eines Miners, Gesamtverbrauch
/// (100 / 500 / 1.000 … kWh) und der erste Monat im Plus. Je Meilenstein genau eine Meldung (Kennung in news-local.json).
/// Der Gesamtverbrauch zählt in milestones.json weiter, weil der Verlauf nur <see cref="AppConfig.HistoryDays"/> Tage hält.
/// </summary>
public sealed partial class MinerHub
{
    public static readonly double[] KwhMilestones = [100, 500, 1000, 2500, 5000, 10000, 25000, 50000];
    private const double HoursPerYear = 8760;
    private DateTime _milestonesAt = DateTime.MinValue;

    /// <summary>Zustand in milestones.json (additiv): gezählter Gesamtverbrauch und bis wann.</summary>
    internal sealed class MilestoneState
    {
        public double TotalKwh { get; set; }
        /// <summary>Bis zu dieser vollen Stunde (lokal) ist der Verbrauch gezählt; null = noch nie.</summary>
        public DateTime? CountedUntil { get; set; }
        /// <summary>Höchster schon gemeldeter kWh-Meilenstein (kleinere werden nicht nachgemeldet).</summary>
        public double AnnouncedKwh { get; set; }
        /// <summary>Schon gemeldete Meilensteine – die Neuigkeiten behalten nur die 40 neuesten, dies hier bleibt.</summary>
        public HashSet<string> Announced { get; set; } = [];
    }

    /// <summary>Meilenstein einmalig melden (gemerkt in milestones.json).</summary>
    private void Announce(MilestoneState s, string id, DateTime now, string title, string text)
    {
        if (!s.Announced.Add(id)) return;
        News.AddLocal(Milestone(id, now, title, text));
        SaveMilestoneState(s);
    }

    private string MilestoneFile => Path.Combine(DataDirectory, "milestones.json");

    internal MilestoneState LoadMilestoneState()
    {
        try { return JsonSerializer.Deserialize<MilestoneState>(File.ReadAllText(MilestoneFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void SaveMilestoneState(MilestoneState s)
    {
        var tmp = MilestoneFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, MilestoneFile, overwrite: true);
    }

    /// <summary>Aus den lokalen Neuigkeiten (alle 30 min): Meilensteine prüfen, höchstens einmal je Stunde.</summary>
    internal void TickMilestones(DateTime now)
    {
        if (now - _milestonesAt < TimeSpan.FromHours(1)) return;
        _milestonesAt = now;
        try { EnergyMilestone(now); } catch { /* Neuigkeiten sind nur Zusatz */ }
        try { RuntimeMilestones(now); } catch { }
        try { PayoutMilestone(now); } catch { }
        try { PositiveMonthMilestone(now); } catch { }
    }

    private NewsItem Milestone(string id, DateTime whenLocal, string title, string text) =>
        new($"milestone-{id}", whenLocal.ToUniversalTime(), "milestone", null, title, text, "");

    /// <summary>Gesamtverbrauch weiterzählen (ab dem ersten Mal mit dem vorhandenen Verlauf) und Schwellen melden.</summary>
    internal void EnergyMilestone(DateTime now)
    {
        if (History is null) return;
        var s = LoadMilestoneState();
        var until = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
        var from = s.CountedUntil ?? until.AddDays(-Math.Max(1, Config.HistoryDays));
        if (until > from)
        {
            var energy = Plugs.EnergyCost.Compute(History, Config, Devices.Select(d => d.Host).ToList(), from, until);
            s.TotalKwh += energy.Kwh;
            s.CountedUntil = until;
        }
        var reached = KwhMilestones.Where(t => t <= s.TotalKwh && t > s.AnnouncedKwh).DefaultIfEmpty(0).Max();
        if (reached > 0)
        {
            News.AddLocal(Milestone($"kwh-{reached}", now, L.T("{0} kWh Gesamtverbrauch", reached.ToString("N0", L.Culture)),
                L.T("Deine Miner haben zusammen {0} kWh verbraucht, seit BitaxeTuner mitzählt.", s.TotalKwh.ToString("N0", L.Culture))));
            s.AnnouncedKwh = reached;
        }
        SaveMilestoneState(s);
    }

    /// <summary>Jedes volle Betriebsjahr eines Miners (Betriebsstunden aus der Wartung, 0.9.16).</summary>
    private void RuntimeMilestones(DateTime now)
    {
        var state = LoadMilestoneState();
        foreach (var d in Devices)
        {
            if (d.Config.Upkeep is not { } u || u.Hours < HoursPerYear) continue;
            var years = (int)(u.Hours / HoursPerYear);
            Announce(state, $"runtime-{d.Host}-{years}", now, years == 1 ? L.T("{0}: 1 Jahr in Betrieb", d.Title) : L.T("{0}: {1} Jahre in Betrieb", d.Title, years),
                L.T("{0} Betriebsstunden – Zeit für einen Blick auf Lüfter, Staub und Wärmeleitpaste.", u.Hours.ToString("N0", L.Culture)));
        }
    }

    /// <summary>Erste Auszahlung im Pool-Buch (Mining-Dutch und künftige Pool-Konten).</summary>
    private void PayoutMilestone(DateTime now)
    {
        var state = LoadMilestoneState();
        if (state.Announced.Contains("first-payout")) return;
        List<Pools.PoolTransaction> ledger;
        try { ledger = TaxRepository.LoadPoolLedger(); } catch { return; }
        if (ledger.Where(t => t.IsPayout).OrderBy(t => t.TimeUtc).FirstOrDefault() is not { } first) return;
        Announce(state, "first-payout", now, L.T("Erste Auszahlung vom Pool"),
            L.T("{0} {1} am {2:d} – die erste Auszahlung, die BitaxeTuner im Pool-Konto sieht.",
                first.Amount.ToString("0.########", L.Culture), first.Symbol, first.TimeUtc.ToLocalTime()));
    }

    /// <summary>Erster abgeschlossener Monat mit positivem Saldo (Ertrag − Stromkosten), wie in der Übersicht gerechnet.</summary>
    private void PositiveMonthMilestone(DateTime now)
    {
        var state = LoadMilestoneState();
        if (History is null || state.Announced.Contains("first-positive-month")) return;
        var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
        var monthEnd = monthStart.AddMonths(1);
        var cur = Currencies.Of(Config).Code;
        List<Pools.PoolTransaction> ledger;
        try { ledger = TaxRepository.LoadPoolLedger(); } catch { ledger = []; }
        var pool = Config.PoolAccount.Enabled && ledger.Count > 0;
        List<MinedReward> rewards = [];
        if (!pool) try { rewards = TaxRepository.LoadIncome(); } catch { rewards = []; }
        var (income, partial) = pool ? PoolIncome(ledger, monthStart.ToUniversalTime(), monthEnd.ToUniversalTime(), cur)
                                     : TaxIncome(rewards, monthStart.ToUniversalTime(), monthEnd.ToUniversalTime(), cur);
        if (income is not { } inc || inc <= 0 || partial) return;
        var cost = (decimal)Plugs.EnergyCost.Compute(History, Config, Devices.Select(d => d.Host).ToList(), monthStart, monthEnd).Cost;
        var net = Math.Round(inc - cost, 2);
        if (net <= 0) return;
        var sym = Currencies.Of(Config).Symbol;
        Announce(state, "first-positive-month", now, L.T("Erster Monat im Plus: {0:MMMM yyyy}", monthStart),
            L.T("Ertrag {0} {3} − Strom {1} {3} = {2} {3}.", inc.ToString("N2", L.Culture), cost.ToString("N2", L.Culture), net.ToString("+0.00;-0.00", L.Culture), sym));
    }

    /// <summary>Best-Diff-Rekord als Meilenstein (aus <see cref="OnBestDiffRecord"/>).</summary>
    private void BestDiffMilestone(string miner, string coin, string previous, string current, DateTime now)
    {
        try
        {
            News.AddLocal(Milestone($"bestdiff-{miner}-{coin}-{current}", now, L.T("{0}: neuer Best-Diff-Rekord", miner),
                L.T("{0} ({1}) – bisher {2}.", current, coin, previous)));
        }
        catch { /* Neuigkeiten sind nur Zusatz */ }
    }

    /// <summary>
    /// Einmalig: „milestone“ in die gewählten Neuigkeiten-Arten der Anzeigen aufnehmen. Ältere Einstellungen kennen die Art
    /// noch nicht und würden sie sonst nie zeigen; wer sie danach abwählt, behält das.
    /// </summary>
    internal bool EnsureMilestoneNewsKind()
    {
        var changed = false;
        foreach (var s in new[] { Config.Display }.Concat(Config.ExtraDisplays.Select(x => x.Settings)))
        {
            if (s.NewsKindsVersion >= 1) continue;
            if (s.NewsKinds.Count > 0 && !s.NewsKinds.Contains("milestone")) s.NewsKinds.Add("milestone");
            s.NewsKindsVersion = 1;
            changed = true;
        }
        return changed;
    }
}
