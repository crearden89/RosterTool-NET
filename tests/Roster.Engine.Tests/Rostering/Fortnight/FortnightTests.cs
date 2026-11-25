// Port of src/lib/stage2/fortnight.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class FortnightSlotFillingTests
{
    // ── slot filling - substitution ladder ──

    [Fact]
    public void Fills_MGR_slots_with_SM_ASM_ahead_of_DSM_and_RSM()
    {
        var empIds = new[] { "rsm", "dsm", "sm", "asm" };
        var (dates, days) = OneDay(MgrPair(), empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("rsm", "RSM", 76), Emp("dsm", "DSM", 76), Emp("sm", "SM", 76), Emp("asm", "ASM", 76)]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.Equal(2, shifts.Count);
        foreach (var s in shifts) Assert.Contains(s.Role, new[] { "SM", "ASM" });
    }

    [Fact]
    public void Deploys_the_DSM_cohort_onto_SA_slots_so_senior_leaders_stay_on_the_floor()
    {
        var empIds = new[] { "sm", "asm", "dsm" };
        var (dates, days) = OneDay([.. MgrPair(), Slot("sa-1", "SA", 9 * H, 15 * H)], empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("dsm", "DSM", 76)]));
        var shifts = ShiftsOn(result, dates[0]);
        var dsmShift = shifts.Find(s => s.EmployeeId == "dsm");
        Assert.NotNull(dsmShift);
        Assert.Equal(9 * H, dsmShift.Start);
        Assert.Equal(15 * H, dsmShift.End);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    [Fact]
    public void Fills_the_biggest_contract_first_on_SA_slots()
    {
        var empIds = new[] { "sm", "asm", "big", "small" };
        var (dates, days) = OneDay([.. MgrPair(), Slot("sa-1", "SA", 9 * H, 15 * H)], empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("big", "SA", 76), Emp("small", "SA", 30)]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.Contains(shifts, s => s.EmployeeId == "big");
        Assert.DoesNotContain(shifts, s => s.EmployeeId == "small");
    }

    // ── slot filling - JSA resourcing rules ──

    private static List<PlanSlot> JsaSlots() =>
        [.. MgrPair(), Slot("sa-1", "SA", 9 * H, 15 * H), Slot("jsa-1", "JSA", 12 * H, 15 * H)];

    [Fact]
    public void Does_not_use_JSAs_at_all_when_the_store_is_over_resourced()
    {
        // Adult contracts (38x4 = 152h/wk) far exceed the plan's ~17h - the spare
        // adult takes the JSA slot for contract hours; the junior sits out.
        var empIds = new[] { "sm", "asm", "sa1", "sa2", "jsa" };
        var (dates, days) = OneDay(JsaSlots(), empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
        [
            Emp("sm", "SM", 76),
            Emp("asm", "ASM", 76),
            Emp("sa1", "SA", 76),
            Emp("sa2", "SA", 76),
            Emp("jsa", "JSA", 0),
        ]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.DoesNotContain(shifts, s => s.EmployeeId == "jsa");
        Assert.Contains(shifts, s => s.EmployeeId == "sa2"); // adult covers the JSA slot
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    [Fact]
    public void Flexes_the_JSA_up_when_the_store_is_under_resourced()
    {
        // Tiny adult contracts (16h/wk total) vs ~21h of plan - the JSA slot goes
        // to the junior (cheaper than pushing adults further above contract). A
        // second SA slot keeps ≥3 adults on the floor through the JSA's shift.
        List<PlanSlot> underSlots = [.. JsaSlots(), Slot("sa-2", "SA", 12 * H, 15 * H)];
        var empIds = new[] { "sm", "asm", "sa1", "sa2", "jsa" };
        var (dates, days) = OneDay(underSlots, empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
        [
            Emp("sm", "SM", 8),
            Emp("asm", "ASM", 8),
            Emp("sa1", "SA", 8),
            Emp("sa2", "SA", 8),
            Emp("jsa", "JSA", 0),
        ]));
        var shifts = ShiftsOn(result, dates[0]);
        var jsaShift = shifts.Find(s => s.EmployeeId == "jsa");
        Assert.NotNull(jsaShift);
        Assert.Equal(12 * H, jsaShift.Start);
        Assert.Equal(15 * H, jsaShift.End);
    }

    // ── slot filling - reshape as last resort ──

    [Fact]
    public void SPLITS_a_slot_so_two_part_availability_people_can_each_take_a_piece()
    {
        // One 6:00-15:00 SA slot; SA-A can only work mornings, SA-B only from 11:00.
        var avail = Avail(
            ("sm", OPEN),
            ("asm", OPEN),
            ("a", OPEN with { ToMin = 11 * H }),
            ("b", OPEN with { FromMin = 11 * H }));
        var (dates, days) = OneDay(
            [
                Slot("mgr-open", "MGR", 6 * H, 11 * H),
                Slot("mgr-close", "MGR", 10 * H, 15 * H),
                Slot("sa-1", "SA", 6 * H, 15 * H),
            ],
            ["sm", "asm", "a", "b"],
            avail);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("a", "SA", 40), Emp("b", "SA", 40)]));
        var shifts = ShiftsOn(result, dates[0]);
        var aShift = shifts.Find(s => s.EmployeeId == "a");
        var bShift = shifts.Find(s => s.EmployeeId == "b");
        Assert.NotNull(aShift);
        Assert.NotNull(bShift);
        // The two pieces butt together and cover the whole slot.
        Assert.Equal(6 * H, aShift.Start);
        Assert.Equal(bShift.Start, aShift.End);
        Assert.Equal(15 * H, bShift.End);
        Assert.True(aShift.End - aShift.Start >= 180);
        Assert.True(bShift.End - bShift.Start >= 180);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    [Fact]
    public void MERGES_adjacent_slots_into_one_shift_when_there_arent_enough_bodies()
    {
        // A 8:00-11:00 and an 11:00-15:00 slot with only ONE Store Assistant: the
        // interview's example - combine into a single 8:00-15:00 shift.
        var (dates, days) = OneDay(
            [
                Slot("mgr-open", "MGR", 8 * H, 12 * H),
                Slot("mgr-close", "MGR", 11 * H, 15 * H),
                Slot("sa-1", "SA", 8 * H, 11 * H),
                Slot("sa-2", "SA", 11 * H, 15 * H),
            ],
            ["sm", "asm", "solo"]);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("solo", "SA", 76)]));
        var shifts = ShiftsOn(result, dates[0]);
        var solo = shifts.Where(s => s.EmployeeId == "solo").ToList();
        Assert.Single(solo);
        Assert.Equal(8 * H, solo[0].Start);
        Assert.Equal(15 * H, solo[0].End);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    // ── slot filling - one extra manager per day cap ──

    private static List<PlanSlot> CapSlots() =>
        [.. MgrPair(), Slot("sa-1", "SA", 9 * H, 15 * H), Slot("sa-2", "SA", 9 * H, 15 * H)];

    [Fact]
    public void Lets_at_most_ONE_spare_CORE_manager_join_the_floor_and_the_RSM_is_not_one()
    {
        // The RSM is a store assistant on store-assistant rules: it does not eat
        // the one-extra-manager-a-day allowance, and it competes for SA slots by
        // contract size like anyone else - so the bigger-contract SA beats it.
        var empIds = new[] { "sm", "asm", "dsm", "rsm", "sa1" };
        var (dates, days) = OneDay(CapSlots(), empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
        [
            Emp("sm", "SM", 76),
            Emp("asm", "ASM", 76),
            Emp("dsm", "DSM", 76),
            Emp("rsm", "RSM", 40),
            Emp("sa1", "SA", 76),
        ]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
        Assert.Contains(shifts, s => s.EmployeeId == "sa1");
        // SM+ASM hold the MGR slots; exactly one CORE manager joins the floor.
        Assert.Single(shifts, s => s.Role == "DSM");
        // The RSM loses the remaining SA slot to the bigger contract.
        Assert.DoesNotContain(shifts, s => s.EmployeeId == "rsm");
        Assert.Equal(4, shifts.Count); // not every manager every day
    }

    [Fact]
    public void Still_prefers_a_second_spare_manager_over_an_UNFILLED_slot()
    {
        var empIds = new[] { "sm", "asm", "dsm", "rsm" };
        var (dates, days) = OneDay(CapSlots(), empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("dsm", "DSM", 76), Emp("rsm", "RSM", 76)]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
        Assert.Equal(4, shifts.Count); // everyone deployed rather than external cover
    }

    // ── slot filling - unfillable slots stay visible ──

    [Fact]
    public void Emits_an_UNFILLED_row_and_flags_including_the_Why_breakdown()
    {
        var (dates, days) = OneDay(MgrPair(), ["sa"]);
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sa", "SA", 76)])); // nobody can manage
        var day0 = result.ByDate[dates[0]];
        Assert.Equal(2, day0.Shifts.Count(s => s.Unfilled));
        Assert.Contains(day0.Flags, f => f.StartsWith("No manager available", StringComparison.Ordinal));
        Assert.Contains(day0.Flags, f => f.StartsWith("Why: ", StringComparison.Ordinal));
    }
}

public class FortnightCrossDayTests
{
    [Fact]
    public void Honours_the_11h_rest_gap_never_both_the_closer_and_next_dawns_opener()
    {
        // Mon 12:00-21:00 and Tue 06:00-10:00 are 9h apart - one person may hold
        // only ONE of them. Which one is policy: the fill visits days in the
        // configured priority order (default Sat,Sun,Tue,Fri,Mon,...), so Tuesday
        // is offered first and Monday's slot goes unfilled - the reverse of the
        // old chronological fill. The invariant that matters is that the pair is
        // never held together.
        var dates = IsoDates();
        var empIds = new[] { "solo" };
        var days = dates.Select((d, i) =>
            i == 0 ? OpenDay(d, 0, [Slot("sa-1", "SA", 12 * H, 21 * H)], empIds)
            : i == 1 ? OpenDay(d, 1, [Slot("sa-1", "SA", 6 * H, 10 * H)], empIds)
            : ClosedDay(d, i, empIds)).ToList();
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("solo", "SA", 76)]));
        var worked = new[] { 0, 1 }.Select(i => ShiftsOn(result, dates[i]).Any(s => !s.Unfilled && s.EmployeeId == "solo")).ToList();
        Assert.Equal(1, worked.Count(w => w)); // held both sides of a 9h gap
        Assert.True(worked[1], "the priority day (Tue) should win the person");
        Assert.Contains(ShiftsOn(result, dates[0]), s => s.Unfilled);
    }

    [Fact]
    public void Keeps_a_locked_fixed_shift_as_is_and_consumes_its_slot()
    {
        var avail = Avail(("fixed", OPEN with { Fixed = new MinuteWindow(9 * H, 13 * H) }));
        var (dates, days) = OneDay([Slot("sa-1", "SA", 9 * H, 13 * H)], ["fixed"], avail);
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("fixed", "SA", 76)]));
        var shifts = ShiftsOn(result, dates[0]);
        Assert.Single(shifts);
        Assert.True(shifts[0].Locked);
        Assert.Equal(9 * H, shifts[0].Start);
        Assert.Equal(13 * H, shifts[0].End);
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    [Fact]
    public void Reserves_weekend_capable_staff_so_Saturdays_slots_still_fill()
    {
        // Mon-Sat each have two parallel 3h SA slots. Without reservation the two
        // 38h SAs burn all five week-days by Friday and Saturday goes unfilled
        // while the 20h SA sat unused.
        var dates = IsoDates();
        var empIds = new[] { "sa1", "sa2", "sa3" };
        List<PlanSlot> DaySlots() => [Slot("sa-a", "SA", 9 * H, 12 * H), Slot("sa-b", "SA", 9 * H, 12 * H)];
        var days = dates.Select((d, i) => i <= 5 ? OpenDay(d, i, DaySlots(), empIds) : ClosedDay(d, i, empIds)).ToList();
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 40)]));
        var sat = result.ByDate[dates[5]];
        Assert.Equal(2, sat.Shifts.Count(s => !s.Unfilled));
        Assert.DoesNotContain(sat.Shifts, s => s.Unfilled);
    }
}

public class FortnightLedgerHelperTests
{
    private static Ledger Led() => new();

    [Fact]
    public void Counts_consecutive_days_worked_before_an_index()
    {
        var l = Led();
        l.ShiftEndByDay.Set(0, 17 * H);
        l.ShiftEndByDay.Set(1, 17 * H);
        l.ShiftEndByDay.Set(2, 17 * H);
        Assert.Equal(3, FortnightEngine.ConsecutiveBefore(l, 3));
        Assert.Equal(1, FortnightEngine.ConsecutiveBefore(l, 1));
    }

    [Fact]
    public void Counts_days_worked_earlier_in_the_Mon_Sun_week()
    {
        var l = Led();
        l.ShiftEndByDay.Set(0, 17 * H);
        l.ShiftEndByDay.Set(2, 17 * H);
        Assert.Equal(2, FortnightEngine.DaysWorkedInWeekBefore(l, 5, 0));
    }

    [Fact]
    public void Computes_the_11h_rest_gap_earliest_start_SM_ASM_exempt()
    {
        var l = Led();
        l.ShiftEndByDay.Set(0, 21 * H); // ended 21:00 → earliest 08:00 next day
        Assert.Equal(8 * H, FortnightEngine.RestGapEarliestStart("SA", l, 1));
        Assert.Equal(double.NegativeInfinity, FortnightEngine.RestGapEarliestStart("SM", l, 1));
    }

    [Fact]
    public void Tracks_the_JSA_weekly_cap_by_term_holiday_week()
    {
        // Default rules (a run in this thread may have left others behind).
        FortnightEngine.RunFortnightStage2(Input([], []));
        static DayContext Day(bool school) => new()
        {
            Date = "2026-08-03",
            WeekIndex = 0,
            IsSchoolDay = school,
            Closed = false,
            StaffOpen = 6 * H,
            StaffClose = 21 * H,
            TradingOpen = 8 * H,
            TradingClose = 20 * H,
            Slots = [],
            DemandByBlock = [],
            Avail = new(),
        };
        var l = Led();
        l.PaidHoursByWeek[0] = 10;
        Assert.Equal(5, FortnightEngine.JsaWeeklyRemaining(Day(true), l, true)); // 15 - 10 in a term week
        Assert.Equal(20, FortnightEngine.JsaWeeklyRemaining(Day(false), l, false)); // 30 - 10 in a holiday week
        // The cap belongs to the WEEK: a Saturday is never a school day, but in a
        // term week it is still held to 15h - not the 30h holiday cap.
        Assert.Equal(5, FortnightEngine.JsaWeeklyRemaining(Day(false), l, true));
    }

    [Fact]
    public void Counts_a_Mon_Sun_week_as_term_when_any_day_in_it_is_a_school_day()
    {
        var term = FortnightEngine.TermWeeksOf(new (int, bool)[]
        {
            (0, true), (0, false), // week 0: one school day, then the weekend
            (1, false), (1, false), // week 1: school holidays
        });
        Assert.True(term.Has(0));
        Assert.False(term.Has(1));
    }
}

public class FortnightManagerTests
{
    private static List<PlanSlot> Pair(int i) =>
        [Slot($"d{i}-open", "MGR", 6 * H, 12.5 * H), Slot($"d{i}-close", "MGR", 10.5 * H, 20.5 * H)];

    [Fact]
    public void Never_rosters_close_then_open_for_SM_ASM_while_a_rested_manager_exists()
    {
        // Realistic opener/closer pair over 4 consecutive days with three managers:
        // whoever closes (finishing 20:30) must not open at 6:00 next morning
        // (inside the 11h rest window) when another manager slept properly.
        var empIds = new[] { "sm", "asm", "dsm" };
        var employees = new[] { Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("dsm", "DSM", 70) };
        var dates = IsoDates();
        var days = dates.Select((d, i) => i < 4 ? OpenDay(d, i, Pair(i), empIds) : ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees));

        var endByEmpDay = new Dictionary<string, double>();
        for (var i = 0; i < 4; i++)
        {
            foreach (var s in ShiftsOn(res, dates[i]))
            {
                Assert.False(s.Unfilled);
                if (endByEmpDay.TryGetValue($"{s.EmployeeId}:{i - 1}", out var prevEnd))
                {
                    // 11h rest: start must be ≥ prevEnd - 24h + 11h.
                    Assert.True(s.Start >= prevEnd - 24 * 60 + 11 * 60,
                        $"{s.EmployeeId} clopens on day {i} ({s.Start} after closing at {prevEnd})");
                }
                endByEmpDay[$"{s.EmployeeId}:{i}"] = s.End;
            }
        }
    }

    [Fact]
    public void Gives_every_under_contract_adult_at_least_one_shift_before_anyone_doubles_up()
    {
        // One SA slot a day for 3 days; a 38h contract vs an 8h contract. Pure
        // biggest-first would give big all three days - the first-shift nudge
        // hands small the second day.
        var empIds = new[] { "big", "small" };
        var employees = new[] { Emp("big", "SA", 76), Emp("small", "SA", 16) };
        var dates = IsoDates();
        var days = dates.Select((d, i) =>
            i < 3 ? OpenDay(d, i, [Slot($"d{i}-sa", "SA", 9 * H, 17 * H)], empIds) : ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees));
        var workers = new HashSet<string>();
        for (var i = 0; i < 3; i++)
            foreach (var s in ShiftsOn(res, dates[i]))
                if (!s.Unfilled) workers.Add(s.EmployeeId);
        Assert.True(workers.Contains("small"), "the 8h-contract SA never got a shift");
        Assert.Contains("big", workers);
    }

    [Fact]
    public void Keeps_a_weekend_day_in_the_SM_ASMs_budget_when_the_other_managers_cant_work_weekends()
    {
        // SM + ASM available all week; the lone DSM is available WEEKDAYS ONLY.
        // Without weekend reservation outranking SM-first, SM+ASM burn Mon-Fri
        // and Saturday's manager slots go unfilled.
        var empIds = new[] { "sm", "asm", "dsm" };
        var employees = new[] { Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("dsm", "DSM", 70) };
        var dates = IsoDates();
        var days = dates.Select((d, i) =>
        {
            if (i >= 7) return ClosedDay(d, i, empIds); // week 1 only
            var avail = new JsMap<string, DayAvail>(empIds.Select(id =>
                (id, id == "dsm" && i % 7 >= 5 ? new DayAvail { Available = false } : OPEN)));
            return OpenDay(d, i, Pair(i), empIds, avail);
        }).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees));
        // Both weekend days must have BOTH manager slots covered.
        foreach (var wi in new[] { 5, 6 })
        {
            var day = res.ByDate[dates[wi]];
            Assert.True(day.Shifts.Count(s => !s.Unfilled) == 2, $"{dates[wi]}: {string.Join("; ", day.Flags)}");
        }
    }

    [Fact]
    public void Gives_the_biggest_manager_contracts_the_days_their_hours_demand()
    {
        // Mon-Fri week 1 only (weekend closed so weekend-reservation is neutral);
        // 5 core managers all available. 5 days x 2 slots = 10 manager-days.
        var empIds = new[] { "sm", "asm", "dsm1", "dsm2", "rsm" };
        var employees = new[]
        {
            Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("dsm1", "DSM", 70), Emp("dsm2", "DSM", 70), Emp("rsm", "RSM", 60),
        };
        var dates = IsoDates();
        var days = dates.Select((d, i) => i < 5 ? OpenDay(d, i, Pair(i), empIds) : ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees));

        var daysByEmp = new Dictionary<string, int>();
        for (var i = 0; i < 5; i++)
        {
            foreach (var s in ShiftsOn(res, dates[i]))
            {
                Assert.False(s.Unfilled);
                daysByEmp[s.EmployeeId] = daysByEmp.GetValueOrDefault(s.EmployeeId) + 1;
            }
        }
        // POLICY (the client's rule, restated during the roster audit): managers
        // MUST reach contract, filled largest-first - day counts follow from the
        // hours owed, they are not levelled for their own sake. Five open days,
        // ten manager slots, and two 45h/wk managers means SM and ASM rightly
        // work all five days; the DSMs' floor time comes from SA slots on real
        // fortnights. What must still never happen: anyone above the weekly day
        // cap, or a slot left unfilled while a manager was free.
        foreach (var (_, n) in daysByEmp) Assert.True(n <= 5);
        // 7, not 10: taking more would need close-then-open pairings, and the
        // engine never manufactures a clopen - even for the exempt SM/ASM - to
        // close contract hours. That residue is flagged for the manager instead.
        var smDays = daysByEmp.GetValueOrDefault("sm");
        var asmDays = daysByEmp.GetValueOrDefault("asm");
        Assert.True(smDays + asmDays >= 7, string.Join(",", daysByEmp));
    }

    [Fact]
    public void Shares_14_days_of_manager_cover_across_the_whole_manager_team()
    {
        // The real G999 shape: SM/ASM capped at 12h, DSM/RSM at 9h. As long as the
        // plan's manager slots fit inside 9h, every manager is eligible and the
        // fortnight's cover spreads instead of falling on the SM and ASM.
        var rules = new Dictionary<string, BreakRule>(BreakRules)
        {
            ["SM"] = Rule(maxShiftHours: 12, breakType: "flat_30_unpaid"),
            ["ASM"] = Rule(maxShiftHours: 12, breakType: "flat_30_unpaid"),
            ["DSM"] = Rule(maxShiftHours: 9, breakType: "flat_30_unpaid"),
            ["RSM"] = Rule(maxShiftHours: 9),
        };
        var empIds = new[] { "sm", "asm", "dsm1", "dsm2", "rsm" };
        var employees = new[]
        {
            Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("dsm1", "DSM", 70), Emp("dsm2", "DSM", 70), Emp("rsm", "RSM", 60),
        };
        var dates = IsoDates();
        // Slots sized as the fixed shaper now cuts them: both within the 9h cap.
        List<PlanSlot> Pair9(int i) => [Slot($"d{i}-open", "MGR", 6 * H, 13 * H), Slot($"d{i}-close", "MGR", 12 * H, 20 * H)];
        var days = dates.Select((d, i) => OpenDay(d, i, Pair9(i), empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees, rules));

        var daysByEmp = new Dictionary<string, int>();
        var unfilled = 0;
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled)
                {
                    unfilled++;
                    continue;
                }
                daysByEmp[s.EmployeeId] = daysByEmp.GetValueOrDefault(s.EmployeeId) + 1;
            }
        }
        // Every manager slot covered...
        Assert.Equal(0, unfilled);
        // ...by the whole team, not just the SM + ASM.
        Assert.True(daysByEmp.Count >= 4, string.Join(",", daysByEmp));
        // The DSMs genuinely carry a share (they were previously locked out). The
        // SM and ASM phases now legitimately claim their full five-day weeks
        // first (the approved fill order), leaving ~8 slots for the DSM phase, of
        // which the RSM backup may take one.
        var dsmDays = daysByEmp.GetValueOrDefault("dsm1") + daysByEmp.GetValueOrDefault("dsm2");
        Assert.True(dsmDays >= 7, "DSMs barely used - are they eligible for the slots?");
        // Nobody is carrying the fortnight alone.
        Assert.True(daysByEmp.Values.Max() <= 10);
    }
}

public class FortnightLeaveTests
{
    private static readonly DayAvail ON_LEAVE = new() { Available = false, OnLeave = true };

    [Fact]
    public void Credits_a_leave_day_at_a_fifth_of_the_weekly_contract_Mon_Fri_only()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [], ["away"])).ToList();
        foreach (var day in days) day.Avail.Set("away", ON_LEAVE);
        // 10 weekdays across the fortnight × (40/5) = 80h; the 4 weekend days pay
        // nothing, which is exactly the fortnightly contract of a 40h employee.
        Assert.Equal(80, FortnightEngine.LeaveHoursFor(Emp("away", "SA", 80), days));
    }

    [Fact]
    public void Does_not_flag_someone_away_for_the_whole_fortnight_as_under_contract()
    {
        var empIds = new[] { "away", "here" };
        var employees = new[] { Emp("away", "SA", 80), Emp("here", "SA", 80) };
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [Slot($"d{i}-sa", "SA", 9 * H, 17 * H)], empIds)).ToList();
        foreach (var day in days) day.Avail.Set("away", ON_LEAVE);
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees));
        Assert.DoesNotContain(res.Flags, f => f.StartsWith("away", StringComparison.Ordinal)); // leave was not credited against the contract
        // The person actually on the floor still gets a real shortfall reported.
        Assert.Contains(res.Flags, f => f.StartsWith("here", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_under_contract_managers_too_not_just_the_SA_cohort()
    {
        var empIds = new[] { "sm" };
        var dates = IsoDates();
        var days = dates.Select((d, i) => ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 90)]));
        Assert.Contains(res.Flags, f => f.StartsWith("sm", StringComparison.Ordinal) && f.Contains("under contract"));
    }
}

public class FortnightRunCapTests
{
    // Harvey's exact shape. Only Fri/Sat/Sun of week 1 and Mon-Thu of week 2 are
    // open: a 7-day run straddling the week boundary, with 3 and 4 worked days
    // in the two weeks so the 5-days-a-week cap never binds. The ONLY thing that
    // can cost a day here is the consecutive-day cap - and losing one costs a
    // 45h/wk manager ~9 paid hours they can never earn back.
    private static readonly int[] RunDays = [4, 5, 6, 7, 8, 9, 10];

    private static List<DayContext> RunFortnight(IEnumerable<string> ids) =>
        IsoDates().Select((d, i) =>
            RunDays.Contains(i) ? OpenDay(d, i, [Slot($"d{i}", "SA", 9 * H, 18.5 * H)], ids) : ClosedDay(d, i, ids)).ToList();

    private static int DaysWorked(string role, Dictionary<string, double>? rules = null)
    {
        var dates = IsoDates();
        var res = FortnightEngine.RunFortnightStage2(Input(RunFortnight(["p"]), [Emp("p", role, 45)]) with { Rules = rules });
        return dates.Count(date => ShiftsOn(res, date).Any(s => !s.Unfilled && s.EmployeeId == "p"));
    }

    [Fact]
    public void Lets_an_ASM_work_all_seven_days_of_a_run_that_crosses_the_week_boundary() => Assert.Equal(7, DaysWorked("ASM"));

    [Fact]
    public void Lets_an_SM_do_the_same() => Assert.Equal(7, DaysWorked("SM"));

    [Fact]
    public void Still_holds_a_store_assistant_to_six_in_a_row() => Assert.Equal(6, DaysWorked("SA"));

    [Fact]
    public void Honours_an_explicit_salaried_cap_when_one_is_configured()
    {
        // The exemption is a RULE, not a hard-coded licence: setting the SM/ASM
        // cap back to 6 on the Rostering Rules page restores the old behaviour.
        Assert.Equal(6, DaysWorked("ASM", new() { ["maxConsecDaysSalaried"] = 6 }));
    }

    [Fact]
    public void Does_not_warn_about_a_seven_day_run()
    {
        // Under the 10-day threshold: legal, ordinary, no noise.
        var res = FortnightEngine.RunFortnightStage2(Input(RunFortnight(["p"]), [Emp("p", "ASM", 90)]));
        Assert.DoesNotContain(res.Flags, f => f.Contains("days in a row"));
    }

    private static List<DayContext> EveryDay() =>
        IsoDates().Select((d, i) => OpenDay(d, i, [Slot($"d{i}", "SA", 9 * H, 18.5 * H)], ["p"])).ToList();

    [Fact]
    public void Warns_when_a_salaried_managers_run_passes_the_threshold()
    {
        // Every day open and no weekly day cap: the ASM runs the whole fortnight.
        // Legal (they have no hard run cap), but the manager should SEE it.
        var res = FortnightEngine.RunFortnightStage2(Input(EveryDay(), [Emp("p", "ASM", 90)]) with
        {
            Rules = new Dictionary<string, double> { ["maxWorkedPerWeek"] = 7 },
        });
        var warn = res.Flags.Find(f => f.Contains("days in a row"));
        Assert.True(warn is not null, string.Join(" | ", res.Flags));
        Assert.Contains("14 days in a row", warn);
        Assert.Contains("above the 10-day run warning", warn);
    }

    [Fact]
    public void Lets_the_threshold_be_retuned_or_switched_off()
    {
        var strict = FortnightEngine.RunFortnightStage2(Input(EveryDay(), [Emp("p", "ASM", 90)]) with
        {
            Rules = new Dictionary<string, double> { ["maxWorkedPerWeek"] = 7, ["longRunWarnDays"] = 3 },
        });
        Assert.Contains(strict.Flags, f => f.Contains("above the 3-day run warning"));

        var off = FortnightEngine.RunFortnightStage2(Input(EveryDay(), [Emp("p", "ASM", 90)]) with
        {
            Rules = new Dictionary<string, double> { ["maxWorkedPerWeek"] = 7, ["longRunWarnDays"] = 0 },
        });
        Assert.DoesNotContain(off.Flags, f => f.Contains("days in a row"));
    }
}

public class FortnightOrderAndRulesTests
{
    [Fact]
    public void Leaves_floor_slots_as_visible_UNFILLED_rows_when_the_order_has_no_fill_step()
    {
        var (dates, days) = OneDay([Slot("sa-1", "SA", 9 * H, 15 * H)], ["sa"]);
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sa", "SA", 76)]) with
        {
            Order = [new OrderStep(OrderStepTypes.AllocateSm), new OrderStep(OrderStepTypes.ContractCheck)],
        });
        var shifts = ShiftsOn(res, dates[0]);
        Assert.Contains(shifts, s => s.Unfilled);
        Assert.DoesNotContain(shifts, s => !s.Unfilled);
        // ...and the contract check still reports the person short.
        Assert.Contains(res.Flags, f => f.StartsWith("sa", StringComparison.Ordinal));
    }

    [Fact]
    public void Gives_a_scarce_person_to_the_highest_priority_day_in_the_configured_order()
    {
        // Two open days, one slot each, and a person capped at ONE worked day a
        // week: the configured day order decides which day gets them.
        var dates = IsoDates();
        var days = dates.Select((d, i) =>
            i <= 1 ? OpenDay(d, i, [Slot($"d{i}", "SA", 9 * H, 15 * H)], ["sa"]) : ClosedDay(d, i, ["sa"])).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sa", "SA", 76)]) with
        {
            Rules = new Dictionary<string, double> { ["maxWorkedPerWeek"] = 1 },
            Order = [new OrderStep(OrderStepTypes.FillFloor, [1, 0, 2, 3, 4, 5, 6]), new OrderStep(OrderStepTypes.ContractCheck)],
        });
        Assert.Contains(ShiftsOn(res, dates[1]), s => !s.Unfilled);
        Assert.Contains(ShiftsOn(res, dates[0]), s => s.Unfilled);
    }

    private static int WorkedDays(FortnightResult res, List<string> dates, int w)
    {
        var n = 0;
        for (var i = w * 7; i < w * 7 + 7; i++)
            if (ShiftsOn(res, dates[i]).Any(s => !s.Unfilled && s.EmployeeId == "sa")) n++;
        return n;
    }

    [Fact]
    public void Caps_worked_days_at_the_overridden_weekly_maximum()
    {
        var dates = IsoDates();
        List<DayContext> Days() => dates.Select((d, i) => OpenDay(d, i, [Slot($"d{i}", "SA", 9 * H, 15 * H)], ["sa"])).ToList();
        var dflt = FortnightEngine.RunFortnightStage2(Input(Days(), [Emp("sa", "SA", 80)]));
        Assert.Equal(5, WorkedDays(dflt, dates, 0)); // shipped default

        var res = FortnightEngine.RunFortnightStage2(Input(Days(), [Emp("sa", "SA", 80)]) with
        {
            Rules = new Dictionary<string, double> { ["maxWorkedPerWeek"] = 2 },
        });
        foreach (var w in new[] { 0, 1 }) Assert.True(WorkedDays(res, dates, w) <= 2);
    }
}

public class FortnightCreditTests
{
    private static readonly DayAvail ON_LEAVE = new() { Available = false, OnLeave = true };

    [Fact]
    public void Credits_nothing_for_a_leave_day_the_store_is_closed_the_person_stays_short()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => i == 2 ? ClosedDay(d, i, ["away"]) : OpenDay(d, i, [], ["away"])).ToList();
        foreach (var day in days) day.Avail.Set("away", ON_LEAVE);
        // 9 open weekdays × 8h = 72, not 80: the closed Wednesday credits nothing
        // (client decision - no notional top-up on closed days for now).
        Assert.Equal(72, FortnightEngine.LeaveHoursFor(Emp("away", "SA", 80), days));
    }

    [Fact]
    public void Credits_an_explicit_hours_per_day_amount_instead_of_the_full_day()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [], ["away"])).ToList();
        foreach (var day in days) day.Avail.Set("away", ON_LEAVE with { LeaveDayHours = 4 });
        Assert.Equal(40, FortnightEngine.LeaveHoursFor(Emp("away", "SA", 80), days)); // 10 weekdays × 4h
    }

    [Fact]
    public void Resolves_a_full_contract_day_to_the_notional_override_when_one_is_set()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [], ["away"])).ToList();
        foreach (var day in days) day.Avail.Set("away", ON_LEAVE);
        var e = Emp("away", "SA", 80) with { NotionalHours = 6 };
        Assert.Equal(6, FortnightEngine.NotionalHoursOf(e));
        Assert.Equal(8, FortnightEngine.NotionalHoursOf(Emp("away", "SA", 80))); // default contract/5
        Assert.Equal(60, FortnightEngine.LeaveHoursFor(e, days)); // 10 weekdays × 6h
    }

    [Fact]
    public void Credits_a_public_holiday_not_worked_at_the_notional_never_doubled_with_leave()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [], ["p"])).ToList();
        days[0] = days[0] with { PublicHoliday = true };
        foreach (var day in days) day.Avail.Set("p", ON_LEAVE);
        var e = Emp("p", "SA", 80);
        // The PH Monday is excluded from the LEAVE credit...
        Assert.Equal(72, FortnightEngine.LeaveHoursFor(e, days));
        // ...and carried by the PH credit instead: target-side (unavailable, so
        // knowable up front) and outcome-side agree.
        Assert.Equal(8, FortnightEngine.PhCreditInWeek(e, days, 0));
        Assert.Equal(8, FortnightEngine.PhCreditFor(e, days, _ => false));
        // Working the PH earns paid penalty-rate hours INSTEAD of the credit.
        Assert.Equal(0, FortnightEngine.PhCreditFor(e, days, di => di == 0));
    }

    [Fact]
    public void Gives_an_available_employee_no_PH_target_credit_before_the_roster_exists()
    {
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, [], ["p"])).ToList();
        days[0] = days[0] with { PublicHoliday = true };
        // Available on an open PH: the engine may still roster them, so the fill
        // target must not assume the notional. The outcome-side credit still makes
        // the fortnight whole if they end up not working it.
        Assert.Equal(0, FortnightEngine.PhCreditInWeek(Emp("p", "SA", 80), days, 0));
        Assert.Equal(8, FortnightEngine.PhCreditFor(Emp("p", "SA", 80), days, _ => false));
    }
}

public class FortnightContractOrderTests
{
    [Fact]
    public void Puts_a_short_contract_manager_on_the_floor_ahead_of_a_bigger_contract_SA()
    {
        // One SA slot, both candidates under contract. The interview order fills
        // DOWN from the managers so any residual shortfall lands on the smallest
        // contracts (the ones the business can put on leave), not on the DSM.
        var empIds = new[] { "dsm", "sa" };
        var dates = IsoDates();
        var days = dates.Select((d, i) =>
            i == 0 ? OpenDay(d, 0, [Slot("sa-1", "SA", 9 * H, 17 * H)], empIds) : ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("dsm", "DSM", 60), Emp("sa", "SA", 76)]));
        var shifts = ShiftsOn(res, dates[0]);
        Assert.Single(shifts);
        Assert.Equal("dsm", shifts[0].EmployeeId);
    }

    [Fact]
    public void Still_fills_the_biggest_contract_first_among_equals()
    {
        var empIds = new[] { "big", "small" };
        var dates = IsoDates();
        var days = dates.Select((d, i) =>
            i == 0 ? OpenDay(d, 0, [Slot("sa-1", "SA", 9 * H, 17 * H)], empIds) : ClosedDay(d, i, empIds)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("big", "SA", 76), Emp("small", "SA", 40)]));
        Assert.Equal("big", ShiftsOn(res, dates[0])[0].EmployeeId);
    }

    // ── leave frees the casual pool ──
    // MGR pair + two SA slots + a JSA slot: ~21h of plan a week. Five adults on
    // 4h contracts (20h) read as over-resourced, so an adult down-covers the JSA
    // slot. Put one adult on leave and only 16h of contract is actually
    // ROSTERABLE - the casual should come back for the slot the leave freed.
    private static List<FShift> RunWith(string? onLeave)
    {
        List<PlanSlot> slots = [.. MgrPair(), Slot("sa-1", "SA", 9 * H, 15 * H), Slot("sa-2", "SA", 12 * H, 15 * H), Slot("jsa-1", "JSA", 12 * H, 15 * H)];
        var ids = new[] { "sm", "asm", "sa1", "sa2", "sa3", "jsa" };
        var team = new[] { Emp("sm", "SM", 8), Emp("asm", "ASM", 8), Emp("sa1", "SA", 8), Emp("sa2", "SA", 8), Emp("sa3", "SA", 8), Emp("jsa", "JSA", 0) };
        var (dates, days) = OneDay(slots, ids);
        if (onLeave is not null) foreach (var day in days) day.Avail.Set(onLeave, new DayAvail { Available = false, OnLeave = true });
        var res = FortnightEngine.RunFortnightStage2(Input(days, team));
        return ShiftsOn(res, dates[0]);
    }

    [Fact]
    public void Keeps_casuals_off_while_every_adult_contract_is_rosterable()
    {
        var shifts = RunWith(null);
        Assert.DoesNotContain(shifts, s => s.EmployeeId == "jsa");
        Assert.DoesNotContain(shifts, s => s.Unfilled);
    }

    [Fact]
    public void Switches_casuals_back_on_when_leave_drops_rosterable_contract_below_the_plan()
    {
        // The bug: the gate summed EVERY adult's contract, including hours nobody
        // could work, so putting people on leave read as still over-resourced and
        // the freed slot went unfilled instead of to a casual.
        var shifts = RunWith("sa3");
        Assert.DoesNotContain(shifts, s => s.EmployeeId == "sa3");
        Assert.True(shifts.Any(s => s.EmployeeId == "jsa"), "casual never used despite an adult being on leave");
        Assert.False(shifts.Any(s => s.Unfilled), "a slot was left open");
    }
}

public class FortnightManagersReachContractTests
{
    // Two MGR slots plus two SA slots a day. Two managers on big contracts and
    // two SAs on small ones. Two MGR slots a day cannot carry a manager's
    // contract, so the balance has to come off the SA slots - the
    // one-extra-manager-a-day cap used to block exactly that and leave every
    // manager short.
    private static List<PlanSlot> Slots(int i) =>
    [
        Slot($"d{i}-open", "MGR", 6 * H, 14 * H),
        Slot($"d{i}-close", "MGR", 12 * H, 20 * H),
        Slot($"d{i}-sa1", "SA", 8 * H, 16 * H),
        Slot($"d{i}-sa2", "SA", 11 * H, 19 * H),
    ];

    private static (Dictionary<string, double> Paid, List<string> Flags) PaidByEmp()
    {
        var ids = new[] { "sm", "asm", "sa1", "sa2" };
        var team = new[] { Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("sa1", "SA", 30), Emp("sa2", "SA", 30) };
        var dates = IsoDates();
        var days = dates.Select((d, i) => OpenDay(d, i, Slots(i), ids)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, team));
        var paid = new Dictionary<string, double>();
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled) continue;
                var hrs = (s.End - s.Start - s.BreakMinutes) / 60;
                paid[s.EmployeeId] = paid.GetValueOrDefault(s.EmployeeId) + hrs;
            }
        }
        return (paid, res.Flags);
    }

    [Fact]
    public void Works_both_managers_their_full_five_days_a_week()
    {
        var (paid, _) = PaidByEmp();
        // 5 days a week x 2 weeks is the hard ceiling; both managers must be on it.
        foreach (var id in new[] { "sm", "asm" })
            Assert.True(paid.GetValueOrDefault(id) > 60, $"{id} barely rostered");
    }

    [Fact]
    public void Puts_the_managers_ahead_of_the_store_assistants_not_behind_them()
    {
        var (paid, _) = PaidByEmp();
        var mgrMin = Math.Min(paid.GetValueOrDefault("sm"), paid.GetValueOrDefault("asm"));
        var saMax = Math.Max(paid.GetValueOrDefault("sa1"), paid.GetValueOrDefault("sa2"));
        Assert.True(mgrMin > saMax, "an SA out-earned a manager who was still under contract");
    }

    [Fact]
    public void Says_so_when_a_contract_is_above_the_physical_ceiling()
    {
        // A DSM caps at 9 PAID hours a shift and 5 shifts a week => 45h/wk. A 45h
        // contract is exactly reachable; 48h is not, and the flag must say so
        // rather than reporting a bare shortfall every fortnight.
        var dates = IsoDates();
        var rules = new Dictionary<string, BreakRule>(BreakRules) { ["DSM"] = Rule(maxShiftHours: 9, breakType: "flat_30_unpaid") };
        var onCeiling = FortnightEngine.RunFortnightStage2(Input(
            dates.Select((d, i) => OpenDay(d, i, Slots(i), ["dsm"])).ToList(), [Emp("dsm", "DSM", 90)], rules));
        Assert.DoesNotContain("unreachable", onCeiling.Flags.Find(f => f.StartsWith("dsm", StringComparison.Ordinal)) ?? "");

        var res = FortnightEngine.RunFortnightStage2(Input(
            dates.Select((d, i) => OpenDay(d, i, Slots(i), ["dsm"])).ToList(), [Emp("dsm", "DSM", 96)], rules));
        var flag = res.Flags.Find(f => f.StartsWith("dsm", StringComparison.Ordinal));
        Assert.NotNull(flag);
        Assert.Contains("unreachable", flag);
        Assert.Contains("90.00h/fortnight ceiling", flag);
    }

    [Fact]
    public void Gives_a_9_paid_hour_role_a_9_5h_shift_and_pays_all_9()
    {
        // The rule the client stated: a store assistant works up to 9.5 hours but
        // only 9 of them are paid (30-minute unpaid break), and overtime starts
        // after those 9. Reading the cap as clock time instead produced 8.5h paid
        // shifts, which is why a 45h/wk manager could never be filled: five days
        // at 8.5h is 42.5h, and the shortfall reappeared every fortnight.
        var dates = IsoDates();
        var rules = new Dictionary<string, BreakRule>(BreakRules) { ["SM"] = Rule(maxShiftHours: 9, breakType: "flat_30_unpaid") };
        var days = dates.Select((d, i) => OpenDay(d, i, [Slot($"d{i}-open", "MGR", 6 * H, 15.5 * H)], ["sm"])).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 90)], rules));

        double paid = 0;
        var shifts = 0;
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled) continue;
                shifts++;
                Assert.Equal(9.5 * H, s.End - s.Start); // the 9.5h span was rejected or trimmed
                paid += (s.End - s.Start - 30) / 60;
            }
        }
        // Five workable days a week, two weeks, 9 paid hours each.
        Assert.Equal(10, shifts);
        Assert.Equal(90, paid);
        // ...which is exactly the fortnightly contract, so nothing is flagged.
        Assert.DoesNotContain(res.Flags, f => f.StartsWith("sm", StringComparison.Ordinal));
    }
}

public class FortnightLengthFitTests
{
    // Every day offers one long slot and one short one. A 45h/wk manager needs
    // nine paid hours from each of their five days; a 10.5h/wk casual needs about
    // five. Ranking on contract size alone gave the manager BOTH slots on day
    // one, burning a day on the short one they could never earn back, while the
    // casual - whom the short slot exactly suited - got nothing.
    private static List<PlanSlot> Slots(int i) =>
    [
        Slot($"d{i}-long", "SA", 6 * H, 15.5 * H), // 9.5h clock -> 9h paid
        Slot($"d{i}-short", "SA", 12 * H, 17.5 * H), // 5.5h clock -> 5.33h paid
    ];

    private static Dictionary<string, List<double>> Run()
    {
        var dates = IsoDates();
        var ids = new[] { "big", "small" };
        var days = dates.Select((d, i) => OpenDay(d, i, Slots(i), ids)).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("big", "SA", 90), Emp("small", "SA", 21)]));
        var byEmp = new Dictionary<string, List<double>>();
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled) continue;
                // PAID hours: clock less the UNPAID part of the break only.
                // s.BreakMinutes is the whole time off the floor, half of which is paid.
                var clockH = (s.End - s.Start) / 60;
                if (!byEmp.TryGetValue(s.EmployeeId, out var arr)) byEmp[s.EmployeeId] = arr = new();
                arr.Add(clockH - global::Roster.Engine.Pay.Breaks.BreakForShift("tiered", clockH).UnpaidMinutes / 60);
            }
        }
        return byEmp;
    }

    [Fact]
    public void Gives_the_long_slots_to_the_big_contract_and_the_short_ones_to_the_small()
    {
        var byEmp = Run();
        var big = byEmp.GetValueOrDefault("big") ?? [];
        var small = byEmp.GetValueOrDefault("small") ?? [];
        Assert.True(big.Count > 0, "the big contract got no shifts");
        Assert.True(small.Count > 0, "the small contract got no shifts");
        // Every shift the big contract works is a long one...
        Assert.True(big.Min() > 8, $"big took a short slot: {string.Join(",", big)}");
        // ...and the small contract never takes a long one off them.
        Assert.True(small.Max() < 8, $"small took a long slot: {string.Join(",", small)}");
    }

    [Fact]
    public void Lands_the_big_contract_on_its_number_instead_of_stranding_it_short()
    {
        var bigTotal = (Run().GetValueOrDefault("big") ?? []).Aggregate(0.0, (s, h) => s + h);
        // Ten workable days at 9 paid hours is exactly the 45h/wk fortnight.
        Assert.Equal(90, bigTotal);
    }

    [Fact]
    public void Takes_a_slightly_short_slot_rather_than_sitting_out_a_day_it_cannot_spare()
    {
        // The G999 case. A 45h/wk SM with BOTH weekend days marked off has exactly
        // five weekdays to make contract - he cannot skip one. Each day offers a
        // 7.5h-paid opener and a 6h-paid second slot, so nothing pays his full 9h.
        // Length-fit alone handed every opener to the 35h/wk DSM (a closer match at
        // 7h/day) and the SM lost the entire day to dodge a 1.5h mismatch.
        var dates = IsoDates();
        var ids = new[] { "sm", "dsm" };
        List<PlanSlot> S(int i) =>
        [
            Slot($"d{i}-a", "MGR", 6 * H, 14 * H), // 8h clock -> 7.5h paid
            Slot($"d{i}-b", "MGR", 12 * H, 18.5 * H), // 6.5h clock -> 6h paid
        ];
        var days = dates.Select((d, i) =>
        {
            var day = OpenDay(d, i, S(i), ids);
            if (i % 7 >= 5)
                foreach (var id in ids) day.Avail.Set(id, new DayAvail { Available = false });
            return day;
        }).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 90), Emp("dsm", "DSM", 70)]));

        var daysByEmp = new Dictionary<string, int>();
        foreach (var date in dates)
            foreach (var s in ShiftsOn(res, date))
                if (!s.Unfilled) daysByEmp[s.EmployeeId] = daysByEmp.GetValueOrDefault(s.EmployeeId) + 1;
        // Ten weekdays across the fortnight, five a week - the SM must work every
        // one of them. Missing even a single day puts 45h/wk permanently out of
        // reach, and no ranking preference is worth that.
        Assert.Equal(10, daysByEmp.GetValueOrDefault("sm"));
        Assert.Equal(10, daysByEmp.GetValueOrDefault("dsm"));
    }

    [Fact]
    public void Works_a_45h_wk_SM_all_five_days_when_nothing_pays_more_than_nine()
    {
        // The award lets an SM work 12 paid hours, so "45h needs four days" is
        // arithmetically true and operationally nonsense - the longest slot the
        // shaper cuts is 9.5h clock, i.e. 9 paid. Measured against the ceiling the
        // must-work check never fired: the SM drifted to four days and ~36h a week
        // while store assistants picked up the fifth slot and went OVER contract.
        var dates = IsoDates();
        var ids = new[] { "sm", "sa" };
        List<PlanSlot> S(int i) =>
        [
            Slot($"d{i}-long", "MGR", 6 * H, 15.5 * H), // 9.5h clock -> 9h paid
            Slot($"d{i}-sa", "SA", 9 * H, 15 * H),
        ];
        var days = dates.Select((d, i) => OpenDay(d, i, S(i), ids)).ToList();
        // SM capped at 12 PAID hours by the award, as in the real break rules.
        var rules = new Dictionary<string, BreakRule>(BreakRules) { ["SM"] = Rule(maxShiftHours: 12, breakType: "flat_30_unpaid") };
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 90), Emp("sa", "SA", 90)], rules));

        var smDays = 0;
        double smPaid = 0;
        foreach (var date in dates)
        {
            foreach (var s in ShiftsOn(res, date))
            {
                if (s.Unfilled || s.EmployeeId != "sm") continue;
                smDays++;
                smPaid += (s.End - s.Start - 30) / 60; // flat 30-min unpaid break
            }
        }
        Assert.Equal(10, smDays); // the SM was not worked all five days a week
        Assert.Equal(90, smPaid);
        Assert.DoesNotContain(res.Flags, f => f.StartsWith("sm", StringComparison.Ordinal));
    }
}
