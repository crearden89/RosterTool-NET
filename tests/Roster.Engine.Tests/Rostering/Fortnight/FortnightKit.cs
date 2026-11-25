// New: the fixtures the fortnight tests share (the helpers at the top of
// src/lib/stage2/fortnight.test.ts and its sibling test files), plus a port of
// src/test/slotDemand.ts.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Fortnight;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Tests.Rostering.Fortnight;

internal static class FortnightKit
{
    public const double H = 60;

    public static BreakRule Rule(
        double minShiftHours = 3,
        double maxShiftHours = 9.5,
        string breakType = "tiered",
        double? weeklyCap = null,
        double? ot15AfterHours = 38) => new()
        {
            MinShiftHours = minShiftHours,
            MaxShiftHours = maxShiftHours,
            GapHours = 11,
            BreakType = breakType,
            WeeklyCap = weeklyCap,
            ConsecDayCap = null,
            Ot15AfterHours = ot15AfterHours,
            Ot19AfterHours = null,
        };

    /// <summary>The break rules of fortnight.test.ts.</summary>
    public static readonly IReadOnlyDictionary<string, BreakRule> BreakRules = new Dictionary<string, BreakRule>
    {
        ["SM"] = Rule(breakType: "flat_30_unpaid", maxShiftHours: 12),
        ["ASM"] = Rule(breakType: "flat_30_unpaid", maxShiftHours: 12),
        ["DSM"] = Rule(breakType: "flat_30_unpaid"),
        ["RSM"] = Rule(),
        ["SA"] = Rule(),
        ["JSA"] = Rule(maxShiftHours: 6),
    };

    public static readonly DayAvail OPEN = new() { Available = true, FromMin = null, ToMin = null, Fixed = null };

    public static int Wk(int i) => i < 7 ? 0 : 1;

    public static FortnightEmp Emp(string id, string role, double contractHours) => new()
    {
        Id = id,
        Name = id,
        Role = role,
        ContractHours = contractHours,
        Restricted = [],
    };

    public static PlanSlot Slot(string id, string role, double start, double end) => new(id, role, start, end);

    // 14 consecutive ISO dates from a known Monday.
    public static List<string> IsoDates(string monday = "2026-08-03")
    {
        var start = DateOnlyExtensions.ParseIso(monday);
        return Enumerable.Range(0, 14).Select(i => start.AddDays(i).ToIso()).ToList();
    }

    public static JsMap<string, DayAvail> AvailOf(IEnumerable<string> empIds, DayAvail? a = null) =>
        new(empIds.Select(id => (id, a ?? OPEN)));

    public static JsMap<string, DayAvail> Avail(params (string Id, DayAvail A)[] entries) => new(entries.Select(e => (e.Id, e.A)));

    public static DayContext ClosedDay(string date, int i, IEnumerable<string> empIds) => new()
    {
        Date = date,
        WeekIndex = Wk(i),
        IsSchoolDay = false,
        Closed = true,
        StaffOpen = 0,
        StaffClose = 0,
        TradingOpen = 0,
        TradingClose = 0,
        Slots = [],
        DemandByBlock = [],
        Avail = AvailOf(empIds),
    };

    public static DayContext OpenDay(string date, int i, List<PlanSlot> slots, IEnumerable<string> empIds, JsMap<string, DayAvail>? avail = null) => new()
    {
        Date = date,
        WeekIndex = Wk(i),
        IsSchoolDay = false,
        Closed = false,
        StaffOpen = 6 * H,
        StaffClose = 21 * H + 30,
        TradingOpen = 8 * H,
        TradingClose = 20 * H,
        Slots = slots,
        DemandByBlock = SlotDemand(slots),
        Avail = avail ?? AvailOf(empIds),
    };

    // A fortnight with the given slots on day 0 (rest closed).
    public static (List<string> Dates, List<DayContext> Days) OneDay(List<PlanSlot> slots, IEnumerable<string> empIds, JsMap<string, DayAvail>? avail = null)
    {
        var ids = empIds.ToList();
        var dates = IsoDates();
        var days = dates.Select((d, i) => i == 0 ? OpenDay(d, 0, slots, ids, avail) : ClosedDay(d, i, ids)).ToList();
        return (dates, days);
    }

    public static List<PlanSlot> MgrPair() => [Slot("mgr-open", "MGR", 9 * H, 13 * H), Slot("mgr-close", "MGR", 12 * H, 16 * H)];

    /// <summary>
    /// Port of src/test/slotDemand.ts. The work a day's plan slots stand for: one
    /// unit of work per slot in every quarter-hour it covers (as Reset, which every
    /// role may do).
    ///
    /// Fortnight-fill tests are about who takes which slot and how many hours each
    /// person ends up with. The painter cuts every shift to the day's work, so a
    /// fixture with NO work leaves it nothing to paint, and every shift is cut to
    /// the three-hour minimum - a test of the fixture, not of the fill. Giving
    /// each slot its own work keeps them about the fill.
    /// </summary>
    public static List<DemandBlock> SlotDemand(IEnumerable<PlanSlot> slots)
    {
        var byTime = new JsMap<double, double>();
        foreach (var s in slots)
            for (var m = s.Start; m < s.End; m += 15) byTime.Set(m, byTime.GetOr(m, 0) + 1);
        return byTime.Entries()
            .OrderBy(e => e.Key)
            .Select(e => new DemandBlock(e.Key, e.Value, new JsMap<string, double>([(TaskGroups.Reset, e.Value)])))
            .ToList();
    }

    public static FortnightInput Input(List<DayContext> days, IEnumerable<FortnightEmp> employees, IReadOnlyDictionary<string, BreakRule>? breakRules = null) => new()
    {
        Days = days,
        Employees = employees.ToList(),
        BreakRules = breakRules ?? BreakRules,
    };

    public static List<FShift> ShiftsOn(FortnightResult r, string date) => r.ByDate[date].Shifts;
}
