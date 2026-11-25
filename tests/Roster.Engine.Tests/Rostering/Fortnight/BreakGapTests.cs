// Port of src/lib/stage2/breakGap.test.ts.
// A manager in a store assistant's shift over six hours takes one break where
// the plan priced two: the half-hour of the second has no work. The roster
// fills it with work from the first or last quarter-hours of another shift,
// which is cut to match (the owner's decision of 14/11/2026).

using System.Text.RegularExpressions;
using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning;
using Roster.Engine.RosterOps;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class BreakGapTests
{
    private static readonly BreakGapRules RULES = new()
    {
        MinPeopleOnFloor = 2,
        MinAdultsWithJsa = 3,
        MinShiftMin = 180,
        OverlapMinMin = 60,
        BreakEndBy = 18 * 60,
    };

    private static double Hm(string s) => Js.Number(s[..2]) * 60 + Js.Number(s.Substring(3, 2));

    // One character a quarter-hour: L load, C till, R reset, S specials, K
    // cleaning, A admin, N non-op, b break, _ blank.
    private static readonly (char Code, string Group)[] CODE =
    [
        ('L', "load_running"),
        ('C', "checkout"),
        ('R', "reset"),
        ('S', "specials"),
        ('K', "cleaning"),
        ('A', "admin"),
        ('N', "non_op"),
        ('b', "break"),
        ('_', "flex"),
    ];

    private static List<ShiftBlock> Tiles(double start, string s) =>
        s.Select((c, i) => new ShiftBlock(start + i * 15, CODE.First(x => x.Code == c).Group)).ToList();

    private static string TileString(FShift sh) => string.Concat(sh.Blocks.Select(b => CODE.First(x => x.Group == b.Group).Code));

    private static FShift Shift(string id, string role, string slotRole, string from, string s, Action<FShift>? over = null)
    {
        var start = Hm(from);
        var sh = new FShift
        {
            EmployeeId = id,
            Name = id,
            Role = role,
            SlotRole = slotRole,
            Start = start,
            End = start + s.Length * 15,
            Blocks = Tiles(start, s),
            Locked = false,
        };
        over?.Invoke(sh);
        return sh;
    }

    // The owner's example (G001, Saturday 14/11/2026): an assistant manager in a
    // store assistant's 07:00-16:15 shift, blank 13:45-14:15 where the plan had the
    // second break; a store assistant 13:30-17:30 ends on two quarter-hours of Reset.
    private static FShift GapShift() => Shift("p8", "ASM", "SA", "07:00", "LLLLLLLLLLLLRRRRbbCCCCCCCCC__CCCCCCCC");
    private static FShift LateTail(string tail = "RR") => Shift("p9", "SA", "SA", "13:30", "CCCCCbCCCCCCCC" + tail);

    private static List<FShift> Day() =>
    [
        Shift("m1", "SM", "MGR", "07:00", "AAAAAAAAAAAAbbAAAAAAAAAAAAAAAAAA"),
        Shift("m2", "DSM", "MGR", "13:00", "AAAAAAAAbbAAAAAAAAAAAAAAAAAAAA"),
        GapShift(),
        LateTail(),
        Shift("p16", "SA", "SA", "16:00", "CCCCbCCCCCCCCCCC"),
    ];

    private static readonly Standing ABOVE = new(5, 4);

    private static BreakGapResult? Run(
        List<FShift> shifts,
        BreakGapDay? day = null,
        Func<string, Standing?>? standing = null,
        string[]? @fixed = null,
        JsMap<string, double>? cuts = null) =>
        BreakGaps.FillBreakGaps(
            shifts,
            day ?? new BreakGapDay(),
            BreakRules,
            RULES,
            _ => [],
            id => (@fixed ?? []).Contains(id),
            standing ?? (_ => ABOVE),
            cuts ?? new JsMap<string, double>());

    private static FShift ById(List<FShift> shifts, string id) => shifts.Find(s => s.EmployeeId == id)!;

    [Fact]
    public void Knows_which_shifts_are_owed_less_break_than_the_plan_priced()
    {
        Assert.Equal(30, BreakGaps.BreakShortfallMin(GapShift(), BreakRules));
        Assert.Equal(0, BreakGaps.BreakShortfallMin(LateTail(), BreakRules));
        Assert.Equal(0, BreakGaps.BreakShortfallMin(Shift("m", "ASM", "MGR", "07:00", new string('L', 37)), BreakRules));
        // A manager in a 3-hour assistant's shift is owed MORE (30 against 15): no gap of this kind.
        Assert.Equal(0, BreakGaps.BreakShortfallMin(Shift("d", "DSM", "SA", "07:00", new string('L', 12)), BreakRules));
    }

    [Fact]
    public void Fills_from_a_tail_the_owners_example()
    {
        var shifts = Day();
        var r = Run(shifts)!;
        Assert.Equal(2, r.Filled);
        Assert.Equal("LLLLLLLLLLLLRRRRbbCCCCCCCCCRRCCCCCCCC", TileString(ById(shifts, "p8")));
        Assert.Equal((Hm("13:30"), Hm("17:00")), (ById(shifts, "p9").Start, ById(shifts, "p9").End));
        Assert.Equal("CCCCCbCCCCCCCC", TileString(ById(shifts, "p9")));
        var note = BreakGaps.RosterBreakCutNote(r)!;
        Assert.Equal(
            "Shorter shift on the roster so a break leaves no time empty: 0.50 h - p9 now 13:30–17:00 (finishes 30 minutes earlier); p8 does that work (Reset) at 13:45–14:15, where the plan priced a break p8 is not owed",
            note);
        Assert.Equal(0.5, BreakTime.RosterBreakCutHours([note]));
        Assert.Matches(BreakTime.RosterBreakCutNotePattern, note);
    }

    [Fact]
    public void Fills_from_a_head_moving_the_work_to_the_gaps_time()
    {
        var shifts = Day();
        shifts[3] = Shift("p9", "SA", "SA", "13:30", "SSCCCCbCCCCCCCCC"); // 13:30-17:30, break 15:00
        var allowed = new JsMap<double, List<string>>(Enumerable.Range(0, 60).Select(i => (6 * H + i * 15, new List<string> { "specials", "checkout", "reset" })));
        var r = Run(shifts, new BreakGapDay { AllowedGroups = allowed })!;
        Assert.Equal(2, r.Filled);
        Assert.Equal((Hm("14:00"), Hm("17:30")), (ById(shifts, "p9").Start, ById(shifts, "p9").End));
        Assert.Equal("SS", TileString(ById(shifts, "p8")).Substring(27, 2));
        Assert.Contains("p9 now 14:00–17:30 (starts 30 minutes later); p8 does that work (Specials) at 13:45–14:15", BreakGaps.RosterBreakCutNote(r));
    }

    [Fact]
    public void Never_takes_from_the_gaps_own_shift_and_fills_at_most_the_difference_in_break_time()
    {
        var shifts = Day();
        // Three blank quarter-hours, but the manager is owed only 30 minutes less.
        shifts[2] = Shift("p8", "ASM", "SA", "07:00", "LLLLLLLLLLLLRRRRbbCCCCCCCCC___CCCCCCR");
        shifts[3] = LateTail("RRRR"[..2]);
        var r = Run(shifts)!;
        Assert.Equal(2, r.Filled);
        Assert.Equal(Hm("16:15"), ById(shifts, "p8").End);
        Assert.Equal(1, TileString(ById(shifts, "p8")).Split('_').Length - 1);
    }

    [Fact]
    public void Leaves_time_the_held_plan_already_had_blank()
    {
        var shifts = Day();
        var held = new HeldShift { Start = Hm("07:00"), End = Hm("16:15"), Blocks = Tiles(Hm("07:00"), "LLLLLLLLLLLLRRRRbbCCCCCCCCC__CCCCCCCC") };
        Assert.Null(Run(shifts, new BreakGapDay { HeldShifts = [held] }));
    }

    [Fact]
    public void Refuses_a_cut_under_the_minimum_length()
    {
        var shifts = Day();
        shifts[3] = Shift("p9", "SA", "SA", "14:30", "CCCCbCCCCCRR"); // 3 hours
        Assert.Null(Run(shifts));
        Assert.Equal(Hm("17:30"), ById(shifts, "p9").End);
    }

    [Fact]
    public void Refuses_a_cut_across_a_break_tier()
    {
        var shifts = Day();
        // 10:15-16:30: 6.25 hours, two breaks; cut to 6 it would be owed one.
        shifts[3] = Shift("p9", "SA", "SA", "10:15", "CCCCCbbCCCCCCCCbbCCCCCCRR");
        shifts.Add(Shift("p20", "SA", "SA", "16:00", "CCCCbCCCCCCCCCCC"));
        Assert.Equal(2, Breaks.BreakForShift("tiered", 6.25).Segments);
        Assert.Null(Run(shifts));
        // From 6.5 hours, one quarter-hour may go; the second would cross the tier.
        var longer = Day();
        longer[3] = Shift("p9", "SA", "SA", "10:15", "CCCCCbbCCCCCCCCbbCCCCCCCRR");
        longer.Add(Shift("p20", "SA", "SA", "16:00", "CCCCbCCCCCCCCCCC"));
        Assert.Equal(1, Run(longer)!.Filled);
        Assert.Equal(Hm("16:30"), ById(longer, "p9").End);
    }

    [Fact]
    public void Refuses_a_cut_that_would_leave_someone_alone()
    {
        var shifts = Day();
        shifts[4] = Shift("p16", "SA", "SA", "17:30", "CCCCbCCCCCCC"); // starts after the tail
        Assert.Null(Run(shifts));
    }

    [Fact]
    public void Never_cuts_fixed_locked_juniors_or_the_managers_shifts()
    {
        (string What, Action<List<FShift>> Change, string[]? Fixed)[] cases =
        [
            ("fixed", _ => { }, ["p9"]),
            ("locked", s => s[3].Locked = true, null),
            ("junior's slot", s => s[3].SlotRole = "JSA", null),
            ("junior", s => s[3].Role = "JSA", null),
            ("manager slot", s => s[3].SlotRole = "MGR", null),
        ];
        foreach (var (what, change, @fixed) in cases)
        {
            var shifts = Day();
            change(shifts);
            Assert.True(Run(shifts, @fixed: @fixed) is null, what);
        }
    }

    [Fact]
    public void Never_moves_a_till_a_break_or_Admin()
    {
        foreach (var tail in new[] { "CC", "AA" })
        {
            var shifts = Day();
            shifts[3] = LateTail(tail);
            Assert.True(Run(shifts) is null, tail);
        }
        var s2 = Day();
        s2[3] = Shift("p9", "SA", "SA", "13:30", "CCCCCbCCCCCCCCCb");
        Assert.Null(Run(s2));
    }

    [Fact]
    public void Respects_the_windows_work_goes_only_where_the_painters_own_rules_allow_it()
    {
        // Specials allowed 15:00-20:00 only: not at 13:45.
        var allowed = new JsMap<double, List<string>>();
        for (var t = 6 * H; t < 21 * H; t += 15)
            allowed.Set(t, t >= 15 * H ? ["specials", "checkout", "reset"] : ["checkout", "reset", "load_running"]);
        var shifts = Day();
        shifts[3] = LateTail("SS");
        Assert.Null(Run(shifts, new BreakGapDay { AllowedGroups = allowed }));
        // Cleaning outside its own task window (the morning hour is a window of its own).
        var clean = Day();
        clean[3] = LateTail("KK");
        var taskWindows = new JsMap<string, List<MinuteWindow>>([("cleaning", new List<MinuteWindow> { new(9 * H, 10 * H), new(16 * H, 21 * H) })]);
        Assert.Null(Run(clean, new BreakGapDay { TaskWindows = taskWindows }));
        // Reset is loose.
        Assert.Equal(2, Run(Day(), new BreakGapDay { AllowedGroups = allowed })!.Filled);
    }

    [Fact]
    public void Moves_a_donors_break_inward_when_the_cut_brings_it_into_the_first_hour_never_onto_a_till()
    {
        var shifts = Day();
        shifts[3] = Shift("p9", "SA", "SA", "13:30", "RRRRbRRCCCCCCCCC"); // break 14:30, start + 60
        var r = Run(shifts)!;
        Assert.Equal(2, r.Filled);
        var p9 = ById(shifts, "p9");
        Assert.Equal(Hm("14:00"), p9.Start);
        Assert.Equal(Hm("15:00"), p9.Blocks.First(b => b.Group == "break").Time);
        Assert.Equal("RRRRbCCCCCCCCC", TileString(p9));
        Assert.Contains("(starts 30 minutes later, break 30 minutes later)", BreakGaps.RosterBreakCutNote(r));
        var till = Day();
        till[3] = Shift("p9", "SA", "SA", "13:30", "RRRRbCCCCCCCCCCC");
        Assert.Null(Run(till));
    }

    [Fact]
    public void Prefers_the_donor_most_above_contract_and_spreads_the_cuts()
    {
        var shifts = Day();
        shifts.Add(Shift("p10", "SA", "SA", "13:30", "CCCCCbCCCCCCCCRR"));
        Run(shifts, standing: id => new Standing(id == "p10" ? 6 : id == "p9" ? -1 : 0, 4));
        Assert.Equal(Hm("17:00"), ById(shifts, "p10").End);
        Assert.Equal(Hm("17:30"), ById(shifts, "p9").End);
        // Two donors equally above contract: one quarter-hour each.
        var even = Day();
        even.Add(Shift("p10", "SA", "SA", "13:30", "CCCCCbCCCCCCCCRR"));
        var cuts = new JsMap<string, double>();
        Run(even, cuts: cuts);
        Assert.Equal(Hm("17:15"), ById(even, "p9").End);
        Assert.Equal(Hm("17:15"), ById(even, "p10").End);
        Assert.Equal([new KeyValuePair<string, double>("p9", 1), new KeyValuePair<string, double>("p10", 1)], cuts.Entries());
    }

    [Fact]
    public void Never_makes_anyone_far_short_of_contract_and_keeps_half_a_gap_where_only_half_can_be_filled()
    {
        var shifts = Day();
        // 3.85 h short of a 4-hour limit: the first quarter-hour (0.25 h) tips them over.
        Assert.Null(Run(shifts, standing: _ => new Standing(-3.85, 4)));
        var half = Day();
        var r = Run(half, standing: _ => new Standing(-3.7, 4))!;
        Assert.Equal(1, r.Filled);
        Assert.Equal(Hm("17:15"), ById(half, "p9").End);
        Assert.Contains("0.25 h - p9 now 13:30–17:15 (finishes 15 minutes earlier); p8 does that work (Reset) at 13:45–14:00", BreakGaps.RosterBreakCutNote(r));
    }

    [Fact]
    public void Grades_its_note_as_a_warning_on_the_Roster_page()
    {
        var note = BreakGaps.RosterBreakCutNote(Run(Day())!)!;
        Assert.Equal(Severity.Warning, ReviewChecks.SeverityOf(note));
        var split = DayStatus.SplitRosterFlags([note]);
        Assert.Empty(split.Errors);
        Assert.Equal([note], split.Warnings);
    }

    // ── the cut on the roster: hours, ledger and contract flags follow ──

    private static readonly (string Id, string Role, string From, string Tiles)[] HELD =
    [
        ("mgr-open", "MGR", "07:00", "LLLLLLLLLLLLAAAAbbCCCCCCCCCCCCCC"),
        ("mgr-close", "MGR", "12:00", "AAAAAAAACCCCCCbbAAAAAAAAAAAAAAAA"),
        ("sa-long", "SA", "07:00", "LLLLLLLLLLLLRRRRbbCCCCCCCCCbbCCCCCCCC"),
        ("sa-late", "SA", "13:30", "CCCCCbCCCCCCCCRR"),
        ("sa-eve", "SA", "16:00", "CCCCbCCCCCCCCCCC"),
    ];

    private static (List<string> Dates, List<DayContext> Days) HeldFortnight()
    {
        var dates = IsoDates();
        var held = HELD.Select(h => new HeldShift { Role = h.Role, Start = Hm(h.From), End = Hm(h.From) + h.Tiles.Length * 15, Blocks = Tiles(Hm(h.From), h.Tiles) }).ToList();
        var slots = HELD.Select((h, i) => Slot(h.Id, h.Role, held[i].Start, held[i].End)).ToList();
        var ids = new[] { "sm", "asm", "dsm", "sa1", "sa2" };
        var days = dates.Select((date, i) => new DayContext
        {
            Date = date,
            WeekIndex = i < 7 ? 0 : 1,
            IsSchoolDay = false,
            Closed = i != 0,
            StaffOpen = 6 * H,
            StaffClose = 21 * H + 30,
            TradingOpen = 8 * H,
            TradingClose = 20 * H,
            Slots = i == 0 ? slots : [],
            DemandByBlock = i == 0 ? HeldPlan.DemandFromPlanShifts(held.Select(h => h.Blocks.Cast<ITile>())) : [],
            Avail = new JsMap<string, DayAvail>(ids.Select(id => (id,
                i != 0 ? OPEN with { Available = false } : id == "dsm" ? OPEN with { FromMin = 7 * H, ToMin = 16 * H + 15 } : OPEN))),
            HeldShifts = i == 0 ? held : null,
        }).ToList();
        return (dates, days);
    }

    [Fact]
    public void Cuts_the_store_assistants_tail_says_so_and_the_hours_and_flags_follow()
    {
        var (dates, days) = HeldFortnight();
        FortnightEmp[] employees = [Emp("sm", "SM", 16), Emp("asm", "ASM", 16), Emp("dsm", "DSM", 16), Emp("sa1", "SA", 2), Emp("sa2", "SA", 2)];
        var result = FortnightEngine.RunFortnightStage2(Input(days, employees));
        var d = result.ByDate[dates[0]];
        var dsm = d.Shifts.Find(s => s.EmployeeId == "dsm")!;
        Assert.Equal((7 * H, 16 * H + 15), (dsm.Start, dsm.End));
        Assert.DoesNotContain(dsm.Blocks, b => b.Group == "flex");
        var cut = d.Shifts.Find(s => s.Start == 13 * H + 30)!;
        Assert.Equal(17 * H, cut.End);
        var note = d.Flags.Find(f => BreakTime.RosterBreakCutNotePattern.IsMatch(f))!;
        Assert.Matches(new Regex($"^Shorter shift on the roster so a break leaves no time empty: 0\\.50 h - {cut.EmployeeId} now 13:30–17:00 \\(finishes 30 minutes earlier\\); dsm does that work \\(Reset\\) at "), note);
        Assert.DoesNotContain(d.Flags, f => Regex.IsMatch(f, "quarter-hours? with no work"));
        Assert.Empty(DayStatus.SplitRosterFlags(d.Flags).Errors);
        // Paid hours and the above-contract flag are read from the cut shift.
        foreach (var o in result.Outcomes)
        {
            var s = d.Shifts.Find(x => x.EmployeeId == o.EmployeeId);
            var paid = s is not null ? (s.End - s.Start - Breaks.BreakForShift(BreakRules[s.Role].BreakType, (s.End - s.Start) / 60).UnpaidMinutes) / 60 : 0;
            Assert.Equal(paid, o.PaidHours, 6);
        }
        Assert.Contains($"{cut.EmployeeId} is 1.50h above contract this fortnight", result.Flags);
        Assert.Equal(15, cut.BreakMinutes);
        // Rostered hours: the plan's less the cut, as the note says.
        var plan = days[0].HeldShifts!.Sum(s => s.End - s.Start);
        var roster = d.Shifts.Sum(s => s.End - s.Start);
        Assert.Equal(BreakTime.RosterBreakCutHours(d.Flags) * 60, plan - roster);
    }
}
