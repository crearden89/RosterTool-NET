// Port of src/lib/payroll/premiumTime.test.ts.

using Roster.Engine.Pay;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Pay;

public class PremiumTimeTests
{
    private static readonly int[] HoursOfDay = Enumerable.Range(0, 24).ToArray();

    // A grid shaped like a retail award: ordinary 07:00-18:00 on weekdays, a
    // loading before 07:00 and from 18:00, all of Saturday, Sunday and public
    // holidays above ordinary. A flat-rate manager position for contrast.
    private static List<PayRateRow> Grid(string position, double @base, bool flat = false)
    {
        var rows = new List<PayRateRow>();
        foreach (var h in HoursOfDay)
        {
            var weekday = flat || (h >= 7 && h < 18) ? @base : @base * 1.25;
            rows.Add(new("weekday", h, position, weekday));
            rows.Add(new("saturday", h, position, flat ? @base : @base * 1.25));
            rows.Add(new("sunday", h, position, flat ? @base : @base * 1.5));
            rows.Add(new("public_holiday", h, position, flat ? @base : @base * 2.25));
        }
        return rows;
    }

    private static readonly List<PayRateRow> Rates = [.. Grid("SA-FF", 28.12), .. Grid("JSA-16", 14.06), .. Grid("SM-45-1", 41.5, true)];
    private static readonly List<double> EarlyLate = [0, 1, 2, 3, 4, 5, 6, 18, 19, 20, 21, 22, 23];
    private static readonly string[] Crew = ["SA-FF", "SA-FF", "JSA-16"];

    [Fact]
    public void PayDayTypeFor_splits_weekday_Saturday_and_Sunday_with_a_public_holiday_first()
    {
        Assert.Equal("weekday", PremiumTime.PayDayTypeFor(ParseIso("2026-11-20"), false)); // Friday
        Assert.Equal("saturday", PremiumTime.PayDayTypeFor(ParseIso("2026-11-21"), false));
        Assert.Equal("sunday", PremiumTime.PayDayTypeFor(ParseIso("2026-11-22"), false));
        Assert.Equal("public_holiday", PremiumTime.PayDayTypeFor(ParseIso("2026-12-25"), true));
        Assert.Equal("public_holiday", PremiumTime.PayDayTypeFor(ParseIso("2026-11-21"), true));
    }

    [Fact]
    public void RateKeysFor_falls_back_through_a_positions_coarser_forms()
    {
        Assert.Equal(["SM-45-3", "SM-45", "SM"], PremiumTime.RateKeysFor("SM-45-3"));
        Assert.Equal(["DSM"], PremiumTime.RateKeysFor("DSM"));
    }

    [Fact]
    public void On_a_weekday_the_hours_paid_above_the_standard_weekday_rate() =>
        Assert.Equal(EarlyLate, PremiumTime.PremiumHours(Rates, "weekday", Crew));

    [Fact]
    public void Judges_each_day_on_its_own_rates_nothing_on_a_day_paid_the_same_all_day()
    {
        Assert.Empty(PremiumTime.PremiumHours(Rates, "saturday", Crew));
        Assert.Empty(PremiumTime.PremiumHours(Rates, "sunday", Crew));
        Assert.Empty(PremiumTime.PremiumHours(Rates, "public_holiday", Crew));
    }

    [Fact]
    public void Shades_the_hours_that_pay_more_than_the_rest_of_that_day()
    {
        // A Sunday loading before 09:00 for the hourly-paid positions.
        var early = Rates.Select(r => r.DayType == "sunday" && r.Hour < 9 && r.Role != "SM-45-1" ? r with { Rate = r.Rate * 1.2 } : r).ToList();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], PremiumTime.PremiumHours(early, "sunday", Crew));
        Assert.Empty(PremiumTime.PremiumHours(early, "saturday", Crew));
    }

    [Fact]
    public void Never_from_a_flat_rate_position()
    {
        Assert.Empty(PremiumTime.PremiumHours(Rates, "weekday", ["SM-45-1"]));
        Assert.Empty(PremiumTime.PremiumHours(Rates, "sunday", ["SM-45-1"]));
    }

    [Fact]
    public void Shades_an_hour_only_when_it_is_premium_for_most_of_the_people()
    {
        // SA-LR is paid ordinary time until 19:00.
        var late = Grid("SA-LR", 28.12).Select(r => r.DayType == "weekday" && r.Hour == 18 ? r with { Rate = 28.12 } : r);
        var rates = Rates.Concat(late).ToList();
        Assert.DoesNotContain(18, PremiumTime.PremiumHours(rates, "weekday", ["SA-FF", "SA-LR", "SA-LR"]));
        Assert.Contains(18, PremiumTime.PremiumHours(rates, "weekday", ["SA-FF", "SA-FF", "SA-LR"]));
        // Two against two is not most.
        Assert.DoesNotContain(18, PremiumTime.PremiumHours(rates, "weekday", ["SA-FF", "SA-FF", "SA-LR", "SA-LR"]));
    }

    [Fact]
    public void Counts_each_person_so_a_crowd_on_one_position_carries_the_hour()
    {
        Assert.Equal(EarlyLate, PremiumTime.PremiumHours(Rates, "weekday", ["SM-45-1", "SA-FF", "SA-FF"]));
        Assert.Empty(PremiumTime.PremiumHours(Rates, "weekday", ["SM-45-1", "SM-45-1", "SA-FF"]));
    }

    [Fact]
    public void Prices_a_position_through_its_coarser_form_as_the_shift_cost_does() =>
        Assert.Equal(EarlyLate, PremiumTime.PremiumHours(Grid("SA", 28.12), "weekday", ["SA-FF"]));

    [Fact]
    public void Ignores_zero_rates_which_are_blank_cells_rather_than_wages()
    {
        var withBlank = Rates.Select(r => r.Role == "SA-FF" && r.DayType == "weekday" && r.Hour == 3 ? r with { Rate = 0 } : r).ToList();
        Assert.Equal(EarlyLate.Where(h => h != 3), PremiumTime.PremiumHours(withBlank, "weekday", ["SA-FF"]));
    }

    [Fact]
    public void Has_no_premium_time_without_rates_or_without_anyone_to_judge_it_on()
    {
        Assert.Empty(PremiumTime.PremiumHours([], "weekday", Crew));
        Assert.Empty(PremiumTime.PremiumHours(Rates, "weekday", []));
        // Rates for other day types only: nothing to judge the day on.
        Assert.Empty(PremiumTime.PremiumHours(Rates.Where(r => r.DayType == "weekday"), "sunday", Crew));
    }

    [Fact]
    public void Treats_rates_within_a_few_cents_of_the_days_standard_as_standard()
    {
        // 12:00 four cents above the $28.12 standard, 13:00 a cent below it.
        var nudged = Rates.Select(r =>
            r.Role != "SA-FF" || r.DayType != "weekday" ? r : r.Hour == 12 ? r with { Rate = 28.16 } : r.Hour == 13 ? r with { Rate = 28.11 } : r).ToList();
        Assert.Equal(EarlyLate, PremiumTime.PremiumHours(nudged, "weekday", ["SA-FF"]));
    }

    // F9 in the run-through report: the Pay Rates tab (DSM / Store Assistant /
    // JSA) as it stood on 15/11/2026, 06:00-21:00, with its one-cent slips -
    // Fully Flexible $31.26 at 10:00 among $31.27s, Limited Roster $32.75 at
    // 09:00 among $32.76s. Taking the slip as ordinary shaded 07:00-10:00 and
    // 11:00-21:00 on every weekday.
    private static List<PayRateRow> Tab()
    {
        static IEnumerable<int> Day(int from, int to) => Enumerable.Range(from, to - from + 1);
        var tab = new List<PayRateRow>();
        void Put(string role, string dayType, IEnumerable<int> hours, double rate)
        {
            foreach (var hour in hours) tab.Add(new(dayType, hour, role, rate));
        }
        void Slip(string role, string dayType, int hour, double rate)
        {
            var i = tab.FindIndex(r => r.Role == role && r.DayType == dayType && r.Hour == hour);
            tab[i] = tab[i] with { Rate = rate };
        }
        // Weekday: a loading at 06:00 and from 18:00.
        Put("SA-FF", "weekday", [6], 47.48);
        Put("SA-FF", "weekday", Day(7, 17), 31.27);
        Put("SA-FF", "weekday", Day(18, 21), 40.12);
        Put("SA-LR", "weekday", [6], 46.57);
        Put("SA-LR", "weekday", Day(7, 17), 32.76);
        Put("SA-LR", "weekday", Day(18, 21), 39.36);
        Put("JSA-16", "weekday", [6], 26.25);
        Put("JSA-16", "weekday", Day(7, 17), 18.78);
        Put("JSA-16", "weekday", Day(18, 21), 22.5);
        Put("JSA-17", "weekday", [6], 31.51);
        Put("JSA-17", "weekday", Day(7, 17), 22.5);
        Put("JSA-17", "weekday", Day(18, 21), 27.01);
        // The slips: 10:00 and 09:00 replace the hour put above.
        Slip("SA-FF", "weekday", 10, 31.26);
        Slip("SA-LR", "weekday", 9, 32.75);
        // Saturday: a loading at 06:00, and from 20:00 for the store assistants,
        // with a slip of its own (Limited Roster $39.35 at 16:00).
        Put("SA-FF", "saturday", [6], 47.48);
        Put("SA-FF", "saturday", Day(7, 19), 40.12);
        Put("SA-FF", "saturday", Day(20, 21), 47.07);
        Put("SA-LR", "saturday", [6], 46.57);
        Put("SA-LR", "saturday", Day(7, 19), 39.36);
        Put("SA-LR", "saturday", Day(20, 21), 47.07);
        Put("JSA-16", "saturday", [6], 26.28);
        Put("JSA-16", "saturday", Day(7, 21), 22.5);
        Put("JSA-17", "saturday", [6], 31.51);
        Put("JSA-17", "saturday", Day(7, 21), 27.01);
        Slip("SA-LR", "saturday", 16, 39.35);
        // Sunday: a loading before 09:00 (06:00 only for Fully Flexible), and from
        // 20:00 for Limited Roster.
        Put("SA-FF", "sunday", [6], 59.37);
        Put("SA-FF", "sunday", Day(7, 21), 45.32);
        Put("SA-LR", "sunday", Day(6, 8), 61);
        Put("SA-LR", "sunday", [9], 46.57);
        Put("SA-LR", "sunday", Day(10, 19), 45.32);
        Put("SA-LR", "sunday", Day(20, 21), 47.07);
        Put("JSA-16", "sunday", Day(6, 8), 33.76);
        Put("JSA-16", "sunday", Day(9, 21), 26.28);
        Put("JSA-17", "sunday", Day(6, 8), 40.52);
        Put("JSA-17", "sunday", Day(9, 21), 31.51);
        return tab;
    }

    private static readonly string[] TabCrew = ["SA-FF", "SA-FF", "SA-LR", "JSA-16", "JSA-17"];

    [Fact]
    public void F9_takes_31_27_and_32_76_as_the_standard_rates_not_the_slips()
    {
        var tab = Tab();
        IEnumerable<double> Weekday(string role) => tab.Where(r => r.Role == role && r.DayType == "weekday").Select(r => r.Rate);
        Assert.Equal(31.27, PremiumTime.StandardRate(Weekday("SA-FF")));
        Assert.Equal(32.76, PremiumTime.StandardRate(Weekday("SA-LR")));
        Assert.Equal(18.78, PremiumTime.StandardRate(Weekday("JSA-16")));
    }

    [Fact]
    public void F9_shades_only_06_00_to_07_00_and_from_18_00_on_a_weekday()
    {
        var tab = Tab();
        Assert.Equal([6, 18, 19, 20, 21], PremiumTime.PremiumHours(tab, "weekday", TabCrew));
        // The same for each position on its own, slips included.
        Assert.Equal([6, 18, 19, 20, 21], PremiumTime.PremiumHours(tab, "weekday", ["SA-FF"]));
        Assert.Equal([6, 18, 19, 20, 21], PremiumTime.PremiumHours(tab, "weekday", ["SA-LR"]));
    }

    [Fact]
    public void F9_judges_a_Saturday_on_Saturdays_rates_not_weekday_ones()
    {
        var tab = Tab();
        // 06:00 for everyone; 20:00-21:00 for the three store assistants of five.
        Assert.Equal([6, 20, 21], PremiumTime.PremiumHours(tab, "saturday", TabCrew));
        // Juniors alone: only their 06:00 loading.
        Assert.Equal([6], PremiumTime.PremiumHours(tab, "saturday", ["JSA-16", "JSA-17"]));
    }

    [Fact]
    public void F9_judges_a_Sunday_on_Sundays_rates_so_not_all_day()
    {
        var tab = Tab();
        // 06:00 for everyone; 07:00-08:00 for three of five (not Fully
        // Flexible); 09:00 and 20:00-21:00 for Limited Roster alone, not most.
        Assert.Equal([6, 7, 8], PremiumTime.PremiumHours(tab, "sunday", TabCrew));
        Assert.Equal([6, 7, 8, 9, 20, 21], PremiumTime.PremiumHours(tab, "sunday", ["SA-LR"]));
        Assert.Equal([6], PremiumTime.PremiumHours(tab, "sunday", ["SA-FF"]));
    }

    [Fact]
    public void StandardRate_is_the_most_common_rate_near_the_lowest_not_the_slip()
    {
        Assert.Equal(31.27, PremiumTime.StandardRate([31.26, .. Enumerable.Repeat(31.27, 10)]));
        Assert.Equal(28.12, PremiumTime.StandardRate([28.12]));
    }

    [Fact]
    public void StandardRate_is_not_pulled_up_by_premium_hours_however_many_there_are() =>
        // 13 loaded hours against 11 ordinary ones, as in the test grid above.
        Assert.Equal(28.12, PremiumTime.StandardRate([.. Enumerable.Repeat(35.15, 13), .. Enumerable.Repeat(28.12, 11)]));

    [Fact]
    public void StandardRate_takes_the_higher_rate_on_a_tie_so_neither_counts_as_premium() =>
        Assert.Equal(31.27, PremiumTime.StandardRate([31.26, 31.27]));

    [Fact]
    public void StandardRate_ignores_blanks_and_has_none_without_rates()
    {
        Assert.Equal(31.27, PremiumTime.StandardRate([0, 31.27, 0]));
        Assert.Null(PremiumTime.StandardRate([]));
        Assert.Null(PremiumTime.StandardRate([0]));
    }

    [Fact]
    public void PremiumBands_joins_whole_hours_into_minute_ranges()
    {
        Assert.Equal([(0.0, 420.0), (1080.0, 1440.0)], PremiumTime.PremiumBands(EarlyLate));
        Assert.Empty(PremiumTime.PremiumBands([]));
        Assert.Equal([(420.0, 600.0)], PremiumTime.PremiumBands([9, 7, 8, 8]));
    }
}
