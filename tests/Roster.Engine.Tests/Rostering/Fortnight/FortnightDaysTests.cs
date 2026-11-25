// Port of src/lib/stage2/fortnight.days.test.ts.
// Shift-DAYS, not just hours: nobody works more than five days a week, so a
// plan of many short shifts can run the team out of days while everyone is
// still short of contract. Found on the live stores (16/11/2026): Thursday
// morning load crews - the last day of the fill order - went to external
// cover, and the juniors sat idle while adults spent their days on junior
// slots.

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class FortnightDaysTests
{
    // Week 0 open with the given slots, week 1 closed.
    private static (List<string> Dates, List<DayContext> Days) Fortnight(
        Func<int, List<PlanSlot>> slotsFor,
        IEnumerable<string> empIds,
        Func<string, int, DayAvail>? avail = null)
    {
        var ids = empIds.ToList();
        var dates = IsoDates();
        var days = dates.Select((date, i) =>
        {
            var slots = i < 7 ? slotsFor(i) : [];
            return new DayContext
            {
                Date = date,
                WeekIndex = i < 7 ? 0 : 1,
                IsSchoolDay = false,
                Closed = slots.Count == 0,
                StaffOpen = 6 * H,
                StaffClose = 21 * H + 30,
                TradingOpen = 8 * H,
                TradingClose = 20 * H,
                Slots = slots,
                DemandByBlock = SlotDemand(slots),
                Avail = new JsMap<string, DayAvail>(ids.Select(id => (id, avail is null ? OPEN : avail(id, i)))),
            };
        }).ToList();
        return (dates, days);
    }

    private static List<FShift> Week0(FortnightResult r, List<string> dates) =>
        dates.Take(7).SelectMany(d => r.ByDate[d].Shifts).ToList();

    // ── shift-days: juniors and day exchanges before external cover ──

    [Fact]
    public void Gives_the_junior_slots_to_juniors_when_the_adults_have_too_few_days_for_the_week()
    {
        // 28 slot-days a week; five adults have 25 days between them. Their
        // contracts cover the hours, so the juniors used to be benched - and the
        // adults' days ran out before the week's last day.
        var empIds = new[] { "sm", "asm", "sa1", "sa2", "sa3", "j1", "j2" };
        var (dates, days) = Fortnight(
            i => [Slot($"m{i}", "MGR", 9 * H, 15 * H), Slot($"a{i}", "SA", 9 * H, 15 * H), Slot($"b{i}", "SA", 9 * H, 15 * H), Slot($"j{i}", "JSA", 12 * H, 15 * H)],
            empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 76), Emp("j1", "JSA", 0), Emp("j2", "JSA", 0)]));
        var week = Week0(result, dates);
        Assert.DoesNotContain(week, s => s.Unfilled);
        Assert.True(week.Count(s => s.Role == "JSA") >= 3);
    }

    [Fact]
    public void Fills_every_slot_when_the_team_has_the_days_whatever_the_fill_order()
    {
        // 21 slot-days for five adults with 25 days: enough, but the fill order
        // leaves Thursday last, when nobody has a day left.
        var empIds = new[] { "sm", "asm", "sa1", "sa2", "sa3" };
        var (dates, days) = Fortnight(
            i => [Slot($"m{i}", "MGR", 9 * H, 15 * H), Slot($"a{i}", "SA", 9 * H, 15 * H), Slot($"b{i}", "SA", 9 * H, 15 * H)],
            empIds);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 76), Emp("asm", "ASM", 76), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 76)]));
        Assert.DoesNotContain(Week0(result, dates), s => s.Unfilled);
    }

    // ── hours above and below contract are information ──

    private static List<PlanSlot> ThreeSlots(int i) =>
        [Slot($"m{i}", "MGR", 9 * H, 15 * H), Slot($"a{i}", "SA", 9 * H, 15 * H), Slot($"b{i}", "SA", 9 * H, 15 * H)];

    private static readonly string[] Ids = ["sm", "asm", "sa1", "sa2", "sa3"];

    [Fact]
    public void Names_the_people_rostered_above_contract_when_the_plans_need_more_hours_than_the_contracts_hold()
    {
        var (dates, days) = Fortnight(ThreeSlots, Ids);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 20), Emp("asm", "ASM", 20), Emp("sa1", "SA", 20), Emp("sa2", "SA", 20), Emp("sa3", "SA", 20)]));
        Assert.DoesNotContain(Week0(result, dates), s => s.Unfilled);
        Assert.Contains(result.Flags, f => Regex.IsMatch(f, @"^The plans need more hours than the team's contracts hold: \d+ people are rostered [\d.]+h above contract this fortnight$"));
        Assert.Contains(result.Flags, f => Regex.IsMatch(f, @"^sa1 is [\d.]+h above contract this fortnight$"));
    }

    [Fact]
    public void Says_when_the_contracts_hold_more_hours_than_the_plans_need()
    {
        var (_, days) = Fortnight(ThreeSlots, Ids);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 76)]));
        Assert.Contains(result.Flags, f => Regex.IsMatch(f, @"^The team's contracts hold [\d.]+h more than the plans need this fortnight: 5 people are short of contract and paid the difference$"));
    }

    // ── a fixed roster takes its own slot ──

    [Fact]
    public void Leaves_the_longer_load_shift_that_overlaps_it_as_much_to_someone_else()
    {
        // The plan cuts a slot for the fixed roster (09:00-15:45) and a load shift
        // 07:00-15:45 that overlaps it just as much. The fixed person must stand for
        // their own slot, so the 07:00 start is still worked.
        var empIds = new[] { "sm", "fx", "sa" };
        var (dates, days) = Fortnight(
            i => i == 0 ? [Slot("mgr-open", "MGR", 7 * H, 15 * H), Slot("sa-1", "SA", 7 * H, 15 * H + 45), Slot("fixed-1", "SA", 9 * H, 15 * H + 45)] : [],
            empIds,
            (id, _) => id == "fx" ? OPEN with { Fixed = new MinuteWindow(9 * H, 15 * H + 45) } : OPEN);
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 76), Emp("fx", "SA", 76), Emp("sa", "SA", 76)]));
        var shifts = ShiftsOn(result, dates[0]).Where(s => !s.Unfilled).ToList();
        var fx = shifts.Find(s => s.EmployeeId == "fx");
        Assert.NotNull(fx);
        Assert.Equal((9 * H, 15 * H + 45), (fx.Start, fx.End));
        Assert.Contains(shifts, s => s.EmployeeId == "sa" && s.Start == 7 * H);
    }

    // ── the roster keeps the held plan's tiles ──

    private static List<ShiftBlock> Tiles(double start, double end, double[] breakAt, string group) =>
        Enumerable.Range(0, (int)((end - start) / 15))
            .Select(i => start + i * 15)
            .Select(time => new ShiftBlock(time, breakAt.Contains(time) ? TaskGroups.Break : group))
            .ToList();

    private static List<(double, string)> Shape(IEnumerable<ShiftBlock> blocks) => blocks.Select(b => (b.Time, b.Group)).ToList();

    [Fact]
    public void Keeps_a_shifts_approved_tiles_when_they_are_legal_for_the_person_in_it()
    {
        var (dates, days) = Fortnight(i => i == 0 ? [Slot("sa-1", "SA", 9 * H, 15 * H)] : [], ["sa"]);
        var held = Tiles(9 * H, 15 * H, [11 * H, 11 * H + 15], TaskGroups.Reset);
        days[0] = days[0] with { HeldShifts = [new HeldShift { Role = "SA", Start = 9 * H, End = 15 * H, Blocks = held }] };
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sa", "SA", 76)]));
        Assert.Equal(Shape(held), Shape(ShiftsOn(result, dates[0])[0].Blocks));
    }

    [Fact]
    public void Paints_afresh_where_the_approved_tiles_do_not_suit_the_person_a_managers_break()
    {
        // A manager in a store assistant's 6-hour shift takes a flat 30-minute
        // break; the plan placed it at 09:15, inside the first hour - illegal.
        var (dates, days) = Fortnight(i => i == 0 ? [Slot("sa-1", "SA", 9 * H, 15 * H)] : [], ["dsm"]);
        var held = Tiles(9 * H, 15 * H, [9 * H + 15, 9 * H + 30], TaskGroups.Reset);
        days[0] = days[0] with { HeldShifts = [new HeldShift { Role = "SA", Start = 9 * H, End = 15 * H, Blocks = held }] };
        var result = FortnightEngine.RunFortnightStage2(Input(days, [Emp("dsm", "DSM", 76)]));
        Assert.NotEqual(Shape(held), Shape(ShiftsOn(result, dates[0])[0].Blocks));
    }

    // ── the role's weekly cap ──

    [Fact]
    public void Never_rosters_anyone_past_it_even_when_the_plans_need_more_than_the_contracts_hold()
    {
        // Two store assistants on small contracts and a week of 9.5-hour shifts:
        // without the cap each would be given up to five of them (42.5 paid hours).
        var capped = new Dictionary<string, BreakRule>(BreakRules) { ["SA"] = BreakRules["SA"] with { WeeklyCap = 38 } };
        var (dates, days) = Fortnight(
            i => [Slot($"m{i}", "MGR", 9 * H, 15 * H), Slot($"a{i}", "SA", 8 * H, 17 * H + 30), Slot($"b{i}", "SA", 8 * H, 17 * H + 30)],
            ["sm", "asm", "sa1", "sa2"]);
        var result = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 20), Emp("asm", "ASM", 20), Emp("sa1", "SA", 20), Emp("sa2", "SA", 20)], capped));
        foreach (var id in new[] { "sa1", "sa2" })
        {
            var paid = Week0(result, dates)
                .Where(s => s.EmployeeId == id)
                .Aggregate(0.0, (n, s) => n + (s.End - s.Start - (s.End - s.Start > 6 * H ? 30 : s.End - s.Start > 4 * H ? 10 : 0)) / 60);
            Assert.True(paid <= 38, $"{id}: {paid}");
        }
    }

    // ── breaks that cost work on the roster (decision 4.2) ──
    // The approved plan priced each shift's break by its slot: 15 minutes for a
    // store assistant's 3-hour shift, a flat 30 for a manager. Every other
    // quarter-hour is work. A manager in the assistant's shift owes 30, so a
    // quarter-hour of the plan has nowhere to go unless the roster swaps who
    // holds which shift or adds a little time.

    private static readonly Dictionary<string, string> PlanRole = new() { ["MGR"] = "ASM", ["SA"] = "SA", ["JSA"] = "JSA" };

    private static List<HeldShift> Held(List<PlanSlot> slots) => slots.Select(sl =>
    {
        var n = Breaks.BreakForShift(BreakRules[PlanRole[sl.Role]].BreakType, (sl.End - sl.Start) / 60).TotalMinutes / 15;
        var mid = sl.Start + Math.Floor((sl.End - sl.Start) / 30 / 15) * 15;
        var blocks = new List<ShiftBlock>();
        for (var t = sl.Start; t < sl.End; t += 15)
            blocks.Add(new ShiftBlock(t, t >= mid && t < mid + n * 15 ? TaskGroups.Break : TaskGroups.Reset));
        return new HeldShift { Role = sl.Role, Start = sl.Start, End = sl.End, Blocks = blocks };
    }).ToList();

    private static List<DayContext> WithHeld(List<DayContext> days) => days.Select(d =>
    {
        if (d.Slots.Count == 0) return d;
        var h = Held(d.Slots);
        return d with { HeldShifts = h, DemandByBlock = HeldPlan.DemandFromPlanShifts(h.Select(x => x.Blocks.Cast<ITile>())) };
    }).ToList();

    private static double LostH(IEnumerable<string> flags) =>
        flags.Aggregate(0.0, (n, f) =>
        {
            var m = Regex.Match(f, @"([\d.]+)h of today's plan is on nobody's shift");
            return n + (m.Success ? Js.Number(m.Groups[1].Value) : 0);
        });

    [Fact]
    public void Swaps_who_holds_two_shifts_first_and_keeps_each_persons_hours_right()
    {
        // The fill gives the duty manager the 3-hour shift; in the 5-hour shift
        // their 30 minutes is what the assistant takes anyway.
        var f = Fortnight(
            i => i == 0 ? [Slot("mgr-open", "MGR", 7 * H, 13 * H), Slot("sa-1", "SA", 7 * H, 10 * H), Slot("sa-2", "SA", 7 * H, 12 * H)] : [],
            ["sm", "dsm", "sa"],
            (_, i) => i == 0 ? OPEN : OPEN with { Available = false });
        var days = WithHeld(f.Days);
        FortnightEmp[] employees = [Emp("sm", "SM", 10), Emp("dsm", "DSM", 4), Emp("sa", "SA", 10)];
        var off = FortnightEngine.RunFortnightStage2(Input(days, employees) with { Rules = new Dictionary<string, double> { ["breakTimeAddMaxMin"] = 0 } });
        var on = FortnightEngine.RunFortnightStage2(Input(days, employees));
        var before = off.ByDate[f.Dates[0]];
        var after = on.ByDate[f.Dates[0]];
        var dsm = before.Shifts.Find(s => s.EmployeeId == "dsm");
        Assert.NotNull(dsm);
        Assert.Equal((7 * H, 10 * H), (dsm.Start, dsm.End));
        Assert.True(LostH(before.Flags) > 0);
        Assert.Contains(
            "Shifts swapped so breaks don't cost work: dsm works 07:00–12:00 and sa 07:00–10:00, because their breaks differ in length",
            after.Flags);
        Assert.Equal(0, LostH(after.Flags));
        Assert.DoesNotContain(after.Flags, x => BreakTime.RosterBreakTimeNotePattern.IsMatch(x));
        foreach (var o in on.Outcomes)
        {
            var s = after.Shifts.Find(x => x.EmployeeId == o.EmployeeId);
            var paid = s is not null ? (s.End - s.Start - Breaks.BreakForShift(BreakRules[s.Role].BreakType, (s.End - s.Start) / 60).UnpaidMinutes) / 60 : 0;
            Assert.Equal(paid, o.PaidHours, 6);
        }
    }

    [Fact]
    public void Adds_a_little_time_when_no_swap_helps_says_so_and_keeps_inside_the_allowance()
    {
        // The duty manager may only work to 10:00, so only the 07:00-10:00 shift fits them.
        var f = Fortnight(
            i => i == 0 ? [Slot("mgr-open", "MGR", 6 * H, 13 * H), Slot("sa-2", "SA", 6 * H, 13 * H), Slot("sa-1", "SA", 7 * H, 10 * H)] : [],
            ["sm", "dsm", "sa"],
            (id, i) => id == "dsm" && i == 0 ? OPEN with { ToMin = 10 * H } : i == 0 ? OPEN : OPEN with { Available = false });
        var days = WithHeld(f.Days);
        FortnightEmp[] employees = [Emp("sm", "SM", 20), Emp("dsm", "DSM", 20), Emp("sa", "SA", 20)];
        var off = FortnightEngine.RunFortnightStage2(Input(days, employees) with { Rules = new Dictionary<string, double> { ["breakTimeAddMaxMin"] = 0 } });
        var on = FortnightEngine.RunFortnightStage2(Input(days, employees));
        var before = off.ByDate[f.Dates[0]];
        var after = on.ByDate[f.Dates[0]];
        Assert.True(LostH(before.Flags) > 0);
        var note = after.Flags.Find(x => BreakTime.RosterBreakTimeNotePattern.IsMatch(x));
        Assert.NotNull(note);
        Assert.Matches(@"^Longer shifts? on the roster so .* - \w+ now \d\d:\d\d–\d\d:\d\d \((starts|finishes) ", note);
        var added = after.Shifts.Sum(s => s.End - s.Start) - before.Shifts.Sum(s => s.End - s.Start);
        Assert.True(added > 0);
        Assert.True(added <= 60);
        Assert.Equal(added, Js.Number(Regex.Match(note, @": ([\d.]+) h").Groups[1].Value) * 60);
        Assert.True(LostH(after.Flags) < LostH(before.Flags));
        Assert.True(after.Shifts.Find(s => s.EmployeeId == "dsm")!.End <= 10 * H);
        // Only what the plan left of the day's allowance.
        var spent = FortnightEngine.RunFortnightStage2(Input(days.Select((d, i) => i == 0 ? d with { BreakTimeAddedMin = 60 } : d).ToList(), employees));
        Assert.DoesNotContain(spent.ByDate[f.Dates[0]].Flags, x => BreakTime.RosterBreakTimeNotePattern.IsMatch(x));
    }
}
