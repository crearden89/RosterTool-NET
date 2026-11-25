// Port of src/lib/payroll/premiumTime.ts.
// ─── Premium time from the pay-rate grid ─────────────────────────────────────
// Which hours of a day cost more than ordinary time, read from Pay Rates
// (pay_rates: day type x hour x position -> $/hour) rather than from a fixed
// clock rule. Pure: the caller reads the rates and the holiday calendar.
//
// The rule, as the owner decided it ("Use the pay rates tab in the app to
// determine this", and on 15/11/2026 "based on the day specific rates"):
// - The day type is the one pay uses: public holiday first, then Saturday or
//   Sunday, else weekday (DayTypes).
// - Each day type is judged on its own rates, not against weekday pay: a
//   Sunday is not premium all day for being a Sunday, only in the hours that
//   pay more than the rest of that Sunday.
// - A position's standard rate for the day is the lowest rate it is paid that
//   day type, read as the rate most of the hours near it are paid at. Rates a
//   few cents apart are one rate, so a one-cent slip on the Pay Rates tab
//   ($31.26 at 10:00 among the day's $31.27s) neither becomes the standard nor
//   turns every other hour into premium time (F9 in the run-through report).
//   An hour is premium for that position when its rate is more than a few
//   cents above the day's standard.
// - The store-wide band is judged on the hourly-paid people rostered that day
//   (store assistants and juniors): an hour is premium when it is premium for
//   most of them. Managers on flat rates do not count.
// - No rates, no premium time. There is no fixed fallback.
//
// Rates are per hour, so premium time comes in whole hours.

using Roster.Engine.Core;

namespace Roster.Engine.Pay;

public static class PremiumTime
{
    // The roles paid by the hour, whose rates decide the store-wide band.
    public static readonly IReadOnlyList<string> HourlyRoles = [Roles.SA, Roles.JSA];

    // Rates this close are the same rate: a few cents either way is a typing slip
    // in the grid, never a pay step - the smallest real step between ordinary and
    // premium time on the Pay Rates tab is dollars.
    private const double NearRate = 0.05;

    /// <summary>
    /// A position's standard rate for a day type, from its rates that day (one per
    /// priced hour): the most common rate among those within a few cents of the
    /// lowest, the higher one on a tie. Null when there are none.
    /// </summary>
    public static double? StandardRate(IEnumerable<double> dayRates)
    {
        var rates = dayRates.Where(r => double.IsFinite(r) && r > 0).ToList();
        if (rates.Count == 0) return null;
        var low = Js.Min(rates);
        var counts = new JsMap<double, double>();
        foreach (var r in rates)
            if (r <= low + NearRate + 1e-9) counts.Set(r, counts.GetOr(r, 0) + 1);
        var standard = low;
        double most = 0;
        foreach (var (rate, n) in counts)
        {
            if (n > most || (n == most && rate > standard))
            {
                standard = rate;
                most = n;
            }
        }
        return standard;
    }

    // The pay day type of a date, by the same rule as DayTypes, for a caller
    // that has already looked the date up in the holiday calendar.
    public static string PayDayTypeFor(DateOnly date, bool publicHoliday) => DayTypes.DayType(date, publicHoliday);

    // Every pay_rates role a position can be priced at, finest first: a position
    // code falls back through its coarser forms ("SM-45-3" -> "SM-45" -> "SM"),
    // exactly as the shift cost does.
    public static List<string> RateKeysFor(string position)
    {
        var parts = position.Split('-');
        var keys = new List<string>();
        for (var n = parts.Length; n > 0; n--) keys.Add(string.Join("-", parts.Take(n)));
        return keys;
    }

    // Rate lookup with that fallback. A zero or missing rate is no rate: the grid
    // has held blank cells saved as zeros, and a zero is never a real wage.
    private static Func<string, double, string, double?> RateLookup(IEnumerable<PayRateRow> rates)
    {
        var map = new JsMap<string, double>();
        foreach (var r in rates)
        {
            var rate = r.Rate;
            if (double.IsFinite(rate) && rate > 0) map.Set($"{r.DayType}:{Js.NumberToString(r.Hour)}:{r.Role}", rate);
        }
        return (dayType, hour, position) =>
        {
            foreach (var key in RateKeysFor(position))
                if (map.TryGet($"{dayType}:{Js.NumberToString(hour)}:{key}", out var hit)) return hit;
            return null;
        };
    }

    /// <summary>
    /// The hours (0-23, ascending) of <paramref name="dayType"/> that are premium
    /// time for most of <paramref name="positions"/> - one entry per hourly-paid
    /// person rostered, so two people on the same position count twice. Each
    /// position is judged against its own standard rate for the day type. A person
    /// whose position has no rates for the day type cannot be judged and is left
    /// out; so is anyone with no rate for the hour.
    /// </summary>
    public static List<double> PremiumHours(IEnumerable<PayRateRow> rates, string dayType, IReadOnlyList<string> positions)
    {
        var rateFor = RateLookup(rates);
        var ordinary = new JsMap<string, double>();
        foreach (var p in new JsSet<string>(positions))
        {
            var day = new List<double>();
            for (var h = 0; h < 24; h++)
            {
                var rate = rateFor(dayType, h, p);
                if (rate is not null) day.Add(rate.Value);
            }
            var standard = StandardRate(day);
            if (standard is not null) ordinary.Set(p, standard.Value);
        }
        var hours = new List<double>();
        for (var h = 0; h < 24; h++)
        {
            var judged = 0;
            var premium = 0;
            foreach (var p in positions)
            {
                var rate = rateFor(dayType, h, p);
                if (!ordinary.TryGet(p, out var baseRate) || rate is null) continue;
                judged++;
                if (rate.Value > baseRate + NearRate) premium++;
            }
            if (judged > 0 && premium * 2 > judged) hours.Add(h);
        }
        return hours;
    }

    /// <summary>Runs of whole premium hours as minute ranges [start, end), in order.</summary>
    public static List<(double Start, double End)> PremiumBands(IEnumerable<double> hours)
    {
        var bands = new List<(double Start, double End)>();
        foreach (var h in Js.Sorted(new JsSet<double>(hours), (a, b) => a - b))
        {
            if (bands.Count > 0 && bands[^1].End == h * 60) bands[^1] = (bands[^1].Start, h * 60 + 60);
            else bands.Add((h * 60, h * 60 + 60));
        }
        return bands;
    }
}
