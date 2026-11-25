// Port of src/lib/stage1/loadStart.ts.
// ─── Load Running start/finish rule (the Load Running Recommendation model) ──
// Every planned day decides its own Load Running window from two facts:
//
//   * the day's Load Running hours at 90% OE  → how EARLY the crew must start
//   * customers just after the doors open     → how LATE load may still be
//                                               running on the floor
//
// Finish is decided FIRST, from customers - a store that gets busy quickly
// after open has to be clear of the floor sooner. Start comes from load size,
// then a minimum-window guarantee reconciles the two: busy stores keep at
// least 3 hours before their finish deadline, everyone else at least 2.5.
// 07:00 is a hard ceiling - no store starts load later than that.
//
// This is a HARD rule: when enabled it replaces the store's configured Load
// Running windows for the day. Night-fill stores are exempt (their load runs
// in the evening by their own flag). The thresholds are tunable on Labour
// Group Rules; the times themselves (07:00/06:30/06:00 starts, 10:00/09:30/
// 09:00 finishes) are the model and are settings (R15).

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Planning.Stage1;

public sealed record LoadStartRules
{
    public bool Enabled { get; init; } = true;
    /// <summary>LR hours at or below this start at 07:00.</summary>
    public double SmallLoadMaxHours { get; init; } = 12;
    /// <summary>LR hours at or above this start at 06:00 (between the two: 06:30).</summary>
    public double LargeLoadMinHours { get; init; } = 15;
    /// <summary>Morning customers at or below this may run load until 10:00.</summary>
    public double QuietCustomersMax { get; init; } = 90;
    /// <summary>Morning customers at or below this: until 09:30. Above: 09:00 + 3h window.</summary>
    public double BusyCustomersMax { get; init; } = 120;
    /// <summary>How many minutes after trading open the customer sample covers.</summary>
    public double CustomerWindowMin { get; init; } = 60;
    /// <summary>The clock times (owner decision R15: settings, not code).</summary>
    public double EarliestStartMin { get; init; } = 6 * 60;
    public double LatestStartMin { get; init; } = 7 * 60;
    public double FinishQuietMin { get; init; } = 10 * 60;
    public double FinishSteadyMin { get; init; } = 9 * 60 + 30;
    public double FinishBusyMin { get; init; } = 9 * 60;
}

public sealed record LoadWindowRec(
    double Start, // minutes from midnight
    double Finish,
    /// <summary>The load-size start before the minimum-window guarantee pulled it.</summary>
    double BaseStart,
    /// <summary>Minimum guaranteed window in minutes (180 busy, 150 otherwise).</summary>
    double MinWindow,
    /// <summary>The morning-customer figure the decision used; null = no POS data.</summary>
    double? Customers);

/// <summary>A POS interval's customers (MCO + SCO), by date and quarter-hour.</summary>
public sealed record PosCustomerRow(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("time")] double Time, // minutes from midnight
    [property: JsonPropertyName("customers")] double Customers);

public static class LoadStart
{
    public static readonly LoadStartRules DefaultRules = new();

    // rostering_rules keys (numeric key-value store; enabled is 0/1).
    public static class Keys
    {
        public const string Enabled = "lrStartEnabled";
        public const string SmallLoadMaxHours = "lrStartSmallLoadMaxHours";
        public const string LargeLoadMinHours = "lrStartLargeLoadMinHours";
        public const string QuietCustomersMax = "lrStartQuietCustomersMax";
        public const string BusyCustomersMax = "lrStartBusyCustomersMax";
        public const string CustomerWindowMin = "lrStartCustomerWindowMin";
        public const string EarliestStartMin = "lrEarliestStartMin";
        public const string LatestStartMin = "lrLatestStartMin";
        public const string FinishQuietMin = "lrFinishQuietMin";
        public const string FinishSteadyMin = "lrFinishSteadyMin";
        public const string FinishBusyMin = "lrFinishBusyMin";
    }

    public static LoadStartRules LoadStartRulesFrom(IReadOnlyDictionary<string, double> overrides)
    {
        var d = DefaultRules;
        double N(string key, double dflt) => overrides.TryGetValue(key, out var v) && double.IsFinite(v) ? v : dflt;
        return new LoadStartRules
        {
            Enabled = N(Keys.Enabled, d.Enabled ? 1 : 0) != 0,
            SmallLoadMaxHours = N(Keys.SmallLoadMaxHours, d.SmallLoadMaxHours),
            LargeLoadMinHours = N(Keys.LargeLoadMinHours, d.LargeLoadMinHours),
            QuietCustomersMax = N(Keys.QuietCustomersMax, d.QuietCustomersMax),
            BusyCustomersMax = N(Keys.BusyCustomersMax, d.BusyCustomersMax),
            CustomerWindowMin = N(Keys.CustomerWindowMin, d.CustomerWindowMin),
            EarliestStartMin = N(Keys.EarliestStartMin, d.EarliestStartMin),
            LatestStartMin = N(Keys.LatestStartMin, d.LatestStartMin),
            FinishQuietMin = N(Keys.FinishQuietMin, d.FinishQuietMin),
            FinishSteadyMin = N(Keys.FinishSteadyMin, d.FinishSteadyMin),
            FinishBusyMin = N(Keys.FinishBusyMin, d.FinishBusyMin),
        };
    }

    /// <summary>
    /// The recommendation itself. <paramref name="customers"/> null (no POS data for
    /// the store) falls to the quiet band: the finish relaxes to 10:00 and the start
    /// is the pure load-size rule - the half of the model whose input (benchmark LR
    /// hours) every store always has.
    /// </summary>
    public static LoadWindowRec RecommendLoadWindow(double lrHours90, double? customers, LoadStartRules r)
    {
        var mid = Js.Round((r.EarliestStartMin + r.LatestStartMin) / 2 / 15) * 15;
        var baseStart = lrHours90 <= r.SmallLoadMaxHours ? r.LatestStartMin : lrHours90 < r.LargeLoadMinHours ? mid : r.EarliestStartMin;
        var finish = customers is null || customers <= r.QuietCustomersMax
            ? r.FinishQuietMin
            : customers <= r.BusyCustomersMax ? r.FinishSteadyMin : r.FinishBusyMin;
        double minWindow = customers is not null && customers > r.BusyCustomersMax ? 180 : 150;
        var start = Math.Min(Math.Min(baseStart, finish - minWindow), r.LatestStartMin);
        return new LoadWindowRec(start, finish, baseStart, minWindow, customers);
    }

    // ─── Morning customers from the POS interval statistics ─────────────────

    /// <summary>
    /// Average customers in the first <paramref name="windowMin"/> minutes after
    /// trading open, for one weekday, across every usable date the POS sample holds
    /// (all four weeks). Public holidays and zero-trade dates are excluded, exactly
    /// as the curve derivation excludes them - a holiday's morning says nothing
    /// about a normal one. Returns null when no usable date exists (store has no
    /// POS data yet).
    /// </summary>
    public static double? MorningCustomers(IEnumerable<PosCustomerRow> rows, int dow, double tradingOpen, double windowMin, ICollection<DateOnly> holidays)
    {
        var list = rows as IReadOnlyList<PosCustomerRow> ?? rows.ToList();
        var dayTotal = new JsMap<DateOnly, double>();
        foreach (var r in list) dayTotal.Set(r.Date, dayTotal.GetOr(r.Date, 0) + r.Customers);

        var usable = new JsSet<DateOnly>();
        foreach (var (date, total) in dayTotal)
        {
            if (Period.DowMon0(date) != dow || holidays.Contains(date) || total <= 0) continue;
            usable.Add(date);
        }
        if (usable.Size == 0) return null;

        double sum = 0;
        foreach (var r in list)
        {
            if (!usable.Has(r.Date)) continue;
            if (r.Time >= tradingOpen && r.Time < tradingOpen + windowMin) sum += r.Customers;
        }
        return sum / usable.Size;
    }
}
