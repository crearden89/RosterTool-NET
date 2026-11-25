// Port of src/lib/stage2/fortnight.legality.test.ts.
// Junior and rest-gap legality in the roster fill. Each scenario was found by
// the 07/11/2026 adversarial review (findings S2-S4) and reproduced a real
// breach before it was fixed: a junior made the closing manager or put on the
// registers, a junior past the 15h term-week cap via a weekend shift, and a
// shift extension that left a junior with one adult or broke tomorrow's 11h
// rest gap.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;
using static Roster.Engine.Tests.Rostering.Fortnight.FortnightKit;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class FortnightLegalityTests
{
    private static BreakRule R9(string breakType = "tiered", double max = 9) =>
        Rule(maxShiftHours: max, breakType: breakType, ot15AfterHours: null);

    // The G999 production break rules.
    private static readonly IReadOnlyDictionary<string, BreakRule> BR = new Dictionary<string, BreakRule>
    {
        ["SM"] = R9("flat_30_unpaid", 12),
        ["ASM"] = R9("flat_30_unpaid", 12),
        ["DSM"] = R9("flat_30_unpaid", 9),
        ["RSM"] = R9(max: 9),
        ["SA"] = R9(max: 9),
        ["JSA"] = R9(max: 6),
    };

    private static readonly DayAvail OFF = new() { Available = false };
    private static readonly List<string> DATES = IsoDates(); // Mon 03/08

    private static DayContext DayOf(int i, List<PlanSlot> slots, JsMap<string, DayAvail> avail) => new()
    {
        Date = DATES[i],
        WeekIndex = i < 7 ? 0 : 1,
        IsSchoolDay = i % 7 < 5,
        Closed = slots.Count == 0,
        StaffOpen = 6 * H,
        StaffClose = 21 * H + 30,
        TradingOpen = 8 * H + 30,
        TradingClose = 21 * H,
        Slots = slots,
        DemandByBlock = SlotDemand(slots),
        Avail = avail,
    };

    private static JsMap<string, DayAvail> AvailAll(IEnumerable<string> ids, Func<string, DayAvail>? f = null) =>
        new(ids.Select(id => (id, f is null ? OPEN : f(id))));

    private static List<FShift> Holder(List<DayContext> days, FortnightResult res, int i) => res.ByDate[days[i].Date].Shifts;

    private static string Show(IEnumerable<FShift> shifts) =>
        string.Join("; ", shifts.Select(s => $"{(s.Unfilled ? "UNFILLED" : s.Name)}({s.Role})[{s.SlotRole}] {Grid.MinToTime(s.Start)}-{Grid.MinToTime(s.End)}"));

    // ── junior rescue respects the slot's role ──

    [Fact]
    public void Never_gives_a_junior_an_SA_slot_juniors_cannot_man_the_registers()
    {
        // School day. SM opens, ASM closes; three adult SAs cover the evening; one
        // more SA slot 16:00-21:00 that no adult can take. The interview/file
        // header: 'SA slots <- adults ... Never a JSA'; 'A JSA never moves up'.
        var ids = new[] { "sm", "asm", "sa1", "sa2", "sa3", "jsa" };
        List<PlanSlot> slots =
        [
            Slot("mgr-open", "MGR", 6 * H, 15 * H),
            Slot("mgr-close", "MGR", 13 * H, 21 * H + 30),
            Slot("sa-1", "SA", 15 * H, 21 * H),
            Slot("sa-2", "SA", 15 * H, 21 * H),
            Slot("sa-3", "SA", 15 * H, 21 * H),
            Slot("sa-4", "SA", 16 * H, 21 * H), // nobody adult left for this one
        ];
        var days = DATES.Select((_, i) => DayOf(i, i == 0 ? slots : [], AvailAll(ids))).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 76), Emp("jsa", "JSA", 0)], BR));
        var shifts = Holder(days, res, 0);
        Assert.False(shifts.Any(s => s.Role == "JSA" && s.SlotRole == "SA"), $"a junior was rescued into an SA slot: {Show(shifts)}");
    }

    [Fact]
    public void Never_gives_a_junior_a_MANAGER_slot()
    {
        // Saturday (non-school: junior may work 06:00-21:00, up to 6h). The SM
        // opens; nobody can close. Three adult SAs work the evening, so the
        // rescue's only test - >=3 adults present - passes, and the closer goes
        // to a 15-year-old.
        var ids = new[] { "sm", "sa1", "sa2", "sa3", "jsa" };
        List<PlanSlot> slots =
        [
            Slot("mgr-open", "MGR", 6 * H, 15 * H),
            Slot("mgr-close", "MGR", 15 * H, 21 * H),
            Slot("sa-1", "SA", 13 * H, 21 * H),
            Slot("sa-2", "SA", 13 * H, 21 * H),
            Slot("sa-3", "SA", 13 * H, 21 * H),
        ];
        var days = DATES.Select((_, i) => DayOf(i, i == 5 ? slots : [], AvailAll(ids))).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 90), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76), Emp("sa3", "SA", 76), Emp("jsa", "JSA", 0)], BR));
        var shifts = Holder(days, res, 5);
        Assert.False(shifts.Any(s => s.Role == "JSA" && s.SlotRole == "MGR"), $"a junior was rescued into the closing MANAGER slot: {Show(shifts)}");
    }

    // ── junior weekly cap on the weekend of a term week ──

    [Fact]
    public void Unit_a_Saturday_in_a_term_week_is_priced_against_the_term_cap()
    {
        // The week's term status comes from the latest run (the TypeScript's
        // module state): in the original file that is the previous test's
        // fortnight, whose weekdays are all school days.
        FortnightEngine.RunFortnightStage2(Input(DATES.Select((_, i) => DayOf(i, [], new())).ToList(), [], BR));
        var led = new Ledger { PaidHoursByWeek = [14.5, 0] };
        // The compute layer sets IsSchoolDay=false on EVERY Saturday/Sunday, term or not.
        var sat = DayOf(5, [], new());
        Assert.False(sat.IsSchoolDay);
        // 15h term-week cap, 14.5h already worked Mon-Fri -> 0.5h left.
        Assert.Equal(0.5, FortnightEngine.JsaWeeklyRemaining(sat, led), 9); // weekend of a term week uses the TERM cap
    }

    [Fact]
    public void End_to_end_the_junior_rescue_keeps_a_term_week_junior_within_15h()
    {
        // Week 0 is a school-term week (Mon-Fri school days). Weekday evenings:
        // SM/ASM pair + 2 adult SAs + a JSA slot 16:00-21:00 the junior takes.
        // Saturday: a 5.75h JSA slot the fill cannot give the junior (adults not
        // yet placed when it is visited) and no adult can down-cover; the rescue
        // gives it to the junior afterwards - against a 30h cap.
        var ids = new[] { "sm", "asm", "dsm", "e1", "e2", "a", "b", "c", "d", "jsa" };
        List<PlanSlot> Weekday(int i) =>
        [
            Slot($"d{i}-open", "MGR", 6 * H, 15 * H),
            Slot($"d{i}-close", "MGR", 13 * H, 21 * H + 30),
            Slot($"d{i}-sa1", "SA", 15 * H + 30, 21 * H + 30),
            Slot($"d{i}-sa2", "SA", 15 * H + 30, 21 * H + 30),
            Slot($"d{i}-jsa", "JSA", 16 * H, 21 * H),
        ];
        List<PlanSlot> saturday =
        [
            Slot("sat-open", "MGR", 6 * H, 15 * H),
            Slot("sat-close", "MGR", 13 * H, 21 * H + 30),
            Slot("sat-jsa", "JSA", 12 * H, 17 * H + 45),
            Slot("sat-a", "SA", 11 * H, 16 * H + 30),
            Slot("sat-c", "SA", 12 * H, 17 * H + 30),
            Slot("sat-b", "SA", 12 * H + 15, 17 * H + 45),
            Slot("sat-d", "SA", 12 * H + 15, 17 * H + 45),
        ];
        DayAvail Win(double from, double to) => OPEN with { FromMin = from, ToMin = to };
        var days = DATES.Select((_, i) =>
        {
            if (i < 5) return DayOf(i, Weekday(i), AvailAll(ids, id => new[] { "a", "b", "c", "d" }.Contains(id) ? OFF : OPEN));
            if (i == 5)
                return DayOf(i, saturday, AvailAll(ids, id =>
                    id == "a" ? Win(11 * H, 16 * H + 30)
                    : id == "c" ? Win(12 * H, 17 * H + 30)
                    : id == "b" || id == "d" ? Win(12 * H + 15, 17 * H + 45)
                    : new[] { "e1", "e2", "dsm" }.Contains(id) ? OFF : OPEN));
            return DayOf(i, [], AvailAll(ids));
        }).ToList();
        FortnightEmp[] employees =
        [
            Emp("sm", "SM", 90), Emp("asm", "ASM", 90), Emp("dsm", "DSM", 70),
            Emp("e1", "SA", 10), Emp("e2", "SA", 10), // tiny contracts -> under-resourced, juniors used
            Emp("a", "SA", 10), Emp("b", "SA", 10), Emp("c", "SA", 10), Emp("d", "SA", 10),
            Emp("jsa", "JSA", 0),
        ];
        var res = FortnightEngine.RunFortnightStage2(Input(days, employees, BR));
        double paid = 0;
        for (var i = 0; i < 7; i++)
        {
            foreach (var s in Holder(days, res, i))
            {
                if (s.EmployeeId != "jsa") continue;
                var len = s.End - s.Start;
                paid += (len - Breaks.BreakForShift("tiered", len / 60).UnpaidMinutes) / 60;
            }
        }
        Assert.True(paid <= 15 + 1e-9, $"junior exceeds the 15h TERM-week cap: {paid}");
    }

    // ── extending a neighbour's shift obeys the junior and rest-gap rules ──

    [Fact]
    public void Never_extends_a_JUNIORs_shift_into_a_span_with_fewer_than_3_adults()
    {
        // Saturday. Junior holds JSA slot 12:00-15:00 (3 adults present). Adjacent
        // JSA slot 15:00-18:00 has only 2 adults and nobody can take it; the
        // 'merge into a neighbour's shift' path stretched the junior to 12-18
        // with no >=3-adults check.
        var ids = new[] { "sm", "asm", "sa1", "jsa" };
        List<PlanSlot> slots =
        [
            Slot("mgr-open", "MGR", 6 * H, 15 * H),
            Slot("mgr-close", "MGR", 12 * H, 21 * H),
            Slot("sa-1", "SA", 9 * H, 15 * H),
            Slot("jsa-1", "JSA", 12 * H, 15 * H),
            Slot("jsa-2", "JSA", 15 * H, 18 * H),
        ];
        var days = DATES.Select((_, i) => DayOf(i, i == 5 ? slots : [], AvailAll(ids))).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days,
            [Emp("sm", "SM", 10), Emp("asm", "ASM", 10), Emp("sa1", "SA", 10), Emp("jsa", "JSA", 0)], BR));
        var shifts = Holder(days, res, 5);
        var j = shifts.Find(s => s.EmployeeId == "jsa");
        if (j is not null)
        {
            for (var m = j.Start; m < j.End; m += 15)
            {
                var adults = shifts.Count(s => !s.Unfilled && s.Role != "JSA" && s.Start <= m && m < s.End);
                Assert.True(adults >= 3, $"junior at {Grid.MinToTime(m)} with {adults} adults: {Show(shifts)}");
            }
        }
    }

    [Fact]
    public void Never_extends_a_shifts_END_past_tomorrows_committed_start_forward_11h_rest_gap()
    {
        // Fri SA slot 13:00-19:00 + adjacent unfillable 19:00-21:30 slot; Sat
        // (filled FIRST by the day order) the same SA opens 08:00. 19:00 -> 08:00
        // is 13h (legal); the extension to 21:30 makes it 10.5h. The extension
        // used to check only the BACKWARD gap.
        var ids = new[] { "sa" };
        List<PlanSlot> fri = [Slot("fri-sa", "SA", 13 * H, 19 * H), Slot("fri-late", "SA", 19 * H, 21 * H + 30)];
        List<PlanSlot> sat = [Slot("sat-sa", "SA", 8 * H, 14 * H)];
        var days = DATES.Select((_, i) => DayOf(i, i == 4 ? fri : i == 5 ? sat : [], AvailAll(ids))).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sa", "SA", 76)], BR));
        var f = Holder(days, res, 4).Find(s => s.EmployeeId == "sa");
        var s = Holder(days, res, 5).Find(x => x.EmployeeId == "sa");
        if (f is not null && s is not null)
            Assert.True(s.Start + 24 * H - f.End >= 11 * H, $"rest gap Fri->Sat: {Show(Holder(days, res, 4))} | {Show(Holder(days, res, 5))}");
    }

    // ── the finished roster names every cover gap ──

    [Fact]
    public void Flags_the_stretch_an_unfillable_manager_slot_leaves_without_a_manager()
    {
        // One open day and one manager, so one of the two manager slots must stay
        // open. Store assistants are on regardless - the roster used to call this
        // "Understaffed" and never said that no manager is in the store.
        var ids = new[] { "sm", "sa1", "sa2" };
        var days = DATES.Select((_, i) => DayOf(
            i,
            i == 0
                ?
                [
                    Slot("mgr-open", "MGR", 6 * H, 14 * H),
                    Slot("mgr-close", "MGR", 13 * H, 21 * H),
                    Slot("sa-a", "SA", 6 * H, 14 * H),
                    Slot("sa-b", "SA", 13 * H, 21 * H),
                ]
                : [],
            AvailAll(ids))).ToList();
        var res = FortnightEngine.RunFortnightStage2(Input(days, [Emp("sm", "SM", 76), Emp("sa1", "SA", 76), Emp("sa2", "SA", 76)], BR));
        var day0 = res.ByDate[DATES[0]];
        Assert.True(day0.Shifts.Any(s => s.Unfilled && s.SlotRole == "MGR"), Show(day0.Shifts));
        Assert.True(day0.Flags.Any(f => f.StartsWith("No manager rostered", StringComparison.Ordinal)), string.Join(" | ", day0.Flags));
    }
}
