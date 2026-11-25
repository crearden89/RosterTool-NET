// Ports of src/lib/payroll/payTiers.test.ts and periodTiers.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.Pay;

public class PayTiersTests
{
    private const double H = 60;

    private static readonly PayRuleSettings Hourly = new()
    {
        DailyOtAfterPaidHours = 9,
        PeriodOtAfterPaidHours = 76,
        SundayPremiumFromMin = 0,
        SundayPremiumToMin = 9 * H,
        ShortBreakHours = 10,
        Ot15Multiplier = 1.5,
        Ot19Multiplier = 1.9,
        DoubleTimeMultiplier = 2,
    };

    private static readonly PayRuleSettings Salaried = Hourly with { DailyOtAfterPaidHours = null, PeriodOtAfterPaidHours = null, ShortBreakHours = null };

    private static PayRuleSettings RulesFor(string role) => role is "SM" or "ASM" ? Salaried : Hourly;

    // 2026-08-03 is a Monday, so 2026-08-09 and 2026-08-16 are Sundays.
    private static readonly DateOnly Mon = ParseIso("2026-08-03");
    private static readonly DateOnly Sun = ParseIso("2026-08-09");

    private static TierShift Shift(DateOnly date, double start, double end, string employeeId = "e1", string role = "SA", double unpaidBreakMin = 30) =>
        new() { EmployeeId = employeeId, Role = role, Date = date, Start = start, End = end, UnpaidBreakMin = unpaidBreakMin };

    private static double HoursAt(JsMap<double, double> map, double mult) => map.Values().Count(m => Math.Abs(m - mult) < 1e-9) / 4.0;

    [Fact]
    public void Leaves_a_9_paid_hour_shift_entirely_at_ordinary_time()
    {
        // 06:00-15:30 clock, 30 min unpaid = exactly 9.0 paid.
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Mon, 6 * H, 15.5 * H)], RulesFor), "e1", Mon, 6 * H);
        Assert.Equal(0, t.MultByMinute.Size);
        Assert.Equal(0, t.Ot15Hours);
    }

    [Fact]
    public void Pays_only_the_hours_past_nine_at_1_5x_as_the_tail_of_the_shift()
    {
        // 06:00-17:30 clock, 30 min unpaid = 11.0 paid -> 2h of overtime.
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Mon, 6 * H, 17.5 * H)], RulesFor), "e1", Mon, 6 * H);
        Assert.Equal(2, t.Ot15Hours);
        Assert.Equal(0, t.Ot19Hours);
        // The tail, not the head: 15:30-17:30 is loaded, 06:00 is not.
        Assert.False(t.MultByMinute.Has(6 * H));
        Assert.Equal(1.5, t.MultByMinute.Get(16 * H));
    }

    [Fact]
    public void Never_gives_a_salaried_manager_daily_overtime() =>
        Assert.Equal(0, PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Mon, 6 * H, 19 * H, role: "ASM")], RulesFor), "e1", Mon, 6 * H).MultByMinute.Size);

    // Nine 9-paid-hour days = 81 paid hours: the 76h line is crossed inside the
    // ninth shift, so 5 of its hours are overtime and the rest ordinary.
    private static readonly DateOnly[] NineDays = Enumerable.Range(0, 9).Select(i => Mon.AddDays(i)).ToArray();

    [Fact]
    public void Charges_only_the_hours_past_76_in_the_shift_that_crosses_the_line()
    {
        var map = PayTiers.TierPayPeriod(NineDays.Select(d => Shift(d, 6 * H, 15.5 * H)), RulesFor);
        // Days 1-8 (72 paid hours) are entirely ordinary.
        foreach (var d in NineDays.Take(8)) Assert.Equal(0, PayTiers.TiersFor(map, "e1", d, 6 * H).Ot15Hours);
        // The ninth crosses 76 after 4 of its 9 hours -> 5 hours of overtime.
        Assert.Equal(5, PayTiers.TiersFor(map, "e1", NineDays[8], 6 * H).Ot15Hours);
    }

    [Fact]
    public void Does_not_apply_the_period_threshold_to_a_salaried_manager()
    {
        var map = PayTiers.TierPayPeriod(NineDays.Select(d => Shift(d, 6 * H, 15.5 * H, role: "SM")), RulesFor);
        foreach (var d in NineDays) Assert.Equal(0, PayTiers.TiersFor(map, "e1", d, 6 * H).Ot15Hours);
    }

    // A 5-hour daily threshold makes the overtime tail land where the band can
    // be seen; the real 9-hour threshold is exercised in the suites above.
    private static PayRuleSettings Early(string role) => RulesFor(role) with { DailyOtAfterPaidHours = 5 };

    [Fact]
    public void Pays_overtime_inside_the_Sunday_band_at_1_9x()
    {
        // Sunday 00:00-08:00, no break = 8 paid, threshold 5 -> the 05:00-08:00
        // tail is overtime and every hour of it is before 09:00.
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Sun, 0, 8 * H, unpaidBreakMin: 0)], Early), "e1", Sun, 0);
        Assert.Equal(3, t.Ot19Hours);
        Assert.Equal(0, t.Ot15Hours);
        Assert.Equal(3, HoursAt(t.MultByMinute, 1.9));
    }

    [Fact]
    public void Splits_a_tail_that_straddles_09_00()
    {
        // Sunday 03:00-14:00, no break = 11 paid, threshold 5 -> a 6h tail from
        // 08:00: one hour inside the band at 1.9x, five outside at 1.5x.
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Sun, 3 * H, 14 * H, unpaidBreakMin: 0)], Early), "e1", Sun, 3 * H);
        Assert.Equal(1, t.Ot19Hours);
        Assert.Equal(5, t.Ot15Hours);
        Assert.Equal(1.9, t.MultByMinute.Get(8 * H));
        Assert.Equal(1.5, t.MultByMinute.Get(9 * H));
    }

    [Fact]
    public void Leaves_Sunday_overtime_that_finishes_after_the_band_at_1_5x()
    {
        // Sunday 06:00-17:30, 30 min unpaid = 11 paid -> a 2h tail at 15:30.
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Sun, 6 * H, 17.5 * H)], RulesFor), "e1", Sun, 6 * H);
        Assert.Equal(0, t.Ot19Hours);
        Assert.Equal(2, t.Ot15Hours);
    }

    [Fact]
    public void Never_pays_1_9x_for_the_same_hours_on_a_weekday()
    {
        var t = PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Mon, 0, 8 * H, unpaidBreakMin: 0)], Early), "e1", Mon, 0);
        Assert.Equal(3, t.Ot15Hours);
        Assert.Equal(0, t.Ot19Hours);
    }

    [Fact]
    public void Does_not_lift_ordinary_Sunday_hours_only_overtime() =>
        // Sunday 00:00-05:00, no break = 5 paid, exactly the threshold: the band
        // is a premium on overtime, not a blanket early-Sunday loading.
        Assert.Equal(0, PayTiers.TiersFor(PayTiers.TierPayPeriod([Shift(Sun, 0, 5 * H, unpaidBreakMin: 0)], Early), "e1", Sun, 0).MultByMinute.Size);

    private static readonly DateOnly Tue = ParseIso("2026-08-04");

    [Fact]
    public void Pays_the_whole_following_shift_at_double_time()
    {
        // Finish 21:00 Monday, start 06:00 Tuesday = a 9-hour break.
        var map = PayTiers.TierPayPeriod([Shift(Mon, 12 * H, 21 * H), Shift(Tue, 6 * H, 14 * H)], RulesFor);
        var second = PayTiers.TiersFor(map, "e1", Tue, 6 * H);
        Assert.Equal(8, second.DoubleHours);
        Assert.Equal(8, HoursAt(second.MultByMinute, 2));
        // The first shift is untouched - it was worked before the short break.
        Assert.Equal(0, PayTiers.TiersFor(map, "e1", Mon, 12 * H).MultByMinute.Size);
    }

    [Fact]
    public void Leaves_a_10_hour_break_alone()
    {
        var map = PayTiers.TierPayPeriod([Shift(Mon, 11 * H, 20 * H), Shift(Tue, 6 * H, 14 * H)], RulesFor);
        Assert.Equal(0, PayTiers.TiersFor(map, "e1", Tue, 6 * H).MultByMinute.Size);
    }

    [Fact]
    public void Keeps_paying_double_until_a_full_break_finally_happens()
    {
        var wed = ParseIso("2026-08-05");
        var thu = ParseIso("2026-08-06");
        var map = PayTiers.TierPayPeriod(
            [Shift(Mon, 12 * H, 21 * H), Shift(Tue, 6 * H, 14 * H), Shift(wed, 6 * H, 14 * H), Shift(thu, 6 * H, 14 * H)], RulesFor);
        Assert.Equal(8, PayTiers.TiersFor(map, "e1", Tue, 6 * H).DoubleHours);
        Assert.Equal(0, PayTiers.TiersFor(map, "e1", wed, 6 * H).DoubleHours);
        Assert.Equal(0, PayTiers.TiersFor(map, "e1", thu, 6 * H).DoubleHours);
    }

    [Fact]
    public void Supersedes_overtime_rather_than_stacking_on_top_of_it()
    {
        // A long shift after a short break: double time, and NOT double + 1.5x.
        var map = PayTiers.TierPayPeriod([Shift(Mon, 12 * H, 21 * H), Shift(Tue, 6 * H, 17.5 * H)], RulesFor);
        var t = PayTiers.TiersFor(map, "e1", Tue, 6 * H);
        Assert.Equal(0, t.Ot15Hours);
        Assert.Equal(11.5, t.DoubleHours);
        Assert.All(t.MultByMinute.Values(), m => Assert.Equal(2, m));
    }

    [Fact]
    public void Double_time_does_not_apply_to_the_salaried_managers()
    {
        var map = PayTiers.TierPayPeriod([Shift(Mon, 12 * H, 21 * H, role: "SM"), Shift(Tue, 6 * H, 14 * H, role: "SM")], RulesFor);
        Assert.Equal(0, PayTiers.TiersFor(map, "e1", Tue, 6 * H).MultByMinute.Size);
    }

    [Fact]
    public void Tracks_each_employees_own_history()
    {
        var map = PayTiers.TierPayPeriod(
            [Shift(Mon, 12 * H, 21 * H, employeeId: "a"), Shift(Tue, 6 * H, 14 * H, employeeId: "a"), Shift(Tue, 6 * H, 14 * H, employeeId: "b")], RulesFor);
        Assert.Equal(8, PayTiers.TiersFor(map, "a", Tue, 6 * H).DoubleHours);
        Assert.Equal(0, PayTiers.TiersFor(map, "b", Tue, 6 * H).DoubleHours);
    }

    [Fact]
    public void Make_up_pay_is_the_gap_between_contract_and_everything_actually_credited()
    {
        Assert.Equal(6, PayTiers.MakeUpHours("SA", 50, 44, 0));
        Assert.Equal(0, PayTiers.MakeUpHours("SA", 50, 20, 30)); // leave/PH credit closes it
        Assert.Equal(0, PayTiers.MakeUpHours("SA", 50, 55, 0)); // over contract owes nothing
    }

    [Fact]
    public void Make_up_pay_never_applies_to_the_salaried_managers()
    {
        Assert.Equal(0, PayTiers.MakeUpHours("SM", 90, 70, 0));
        Assert.Equal(0, PayTiers.MakeUpHours("ASM", 90, 70, 0));
        Assert.Equal(10, PayTiers.MakeUpHours("DSM", 70, 60, 0));
    }
}

// The pay period's tiers for a roster the caller holds (the Roster page's held
// roster): its own shifts are tiered - a manual edit's overtime included - and
// nothing is solved again (TierRosterPeriod takes the shifts, never a solver).
public class PeriodTiersTests
{
    private const double H = 60;

    private static readonly Dictionary<string, RolePayRule> RoleRules = new()
    {
        ["SA"] = new RolePayRule(9, 76, 10),
        ["SM"] = new RolePayRule(null, null, null),
    };

    private static readonly PaySettings Settings = new()
    {
        SundayOtFromHour = 0,
        SundayOtToHour = 9,
        Overtime15Multiplier = 1.5,
        Overtime19Multiplier = 1.9,
        DoubleTimeMultiplier = 2,
    };

    private static Shift Shift(string employeeId, string role, double start, double end, bool unfilled = false) =>
        new() { EmployeeId = employeeId, Name = employeeId, Role = role, Start = start, End = end, BreakMinutes = 60, Unfilled = unfilled };

    private static readonly DateOnly Day = ParseIso("2026-11-24");

    [Fact]
    public void Tiers_a_long_manual_shifts_tail_as_overtime_from_the_shifts_it_is_given()
    {
        // A Tuesday shift edited to 11 hours (10 paid after the hour's unpaid break).
        var tiers = PeriodTiers.TierRosterPeriod([(Day, [Shift("sa-1", "SA", 7 * H, 18 * H)])], RoleRules, Settings);
        Assert.True(tiers.Get(PayTiers.ShiftKey("sa-1", Day, 7 * H))!.Ot15Hours > 0);
    }

    [Fact]
    public void Leaves_ordinary_shifts_salaried_managers_and_open_shifts_at_ordinary_time()
    {
        var tiers = PeriodTiers.TierRosterPeriod(
            [(Day, [Shift("sa-2", "SA", 9 * H, 17 * H), Shift("sm-1", "SM", 6 * H, 18 * H), Shift("", "SA", 7 * H, 19 * H, true)])],
            RoleRules, Settings);
        Assert.Equal(0, tiers.Get(PayTiers.ShiftKey("sa-2", Day, 9 * H))?.Ot15Hours ?? 0);
        Assert.Equal(0, tiers.Get(PayTiers.ShiftKey("sm-1", Day, 6 * H))?.Ot15Hours ?? 0);
        // An open shift has no employee and no wage: it is never tiered.
        Assert.DoesNotContain(tiers.Keys(), k => k.StartsWith(':'));
    }
}
