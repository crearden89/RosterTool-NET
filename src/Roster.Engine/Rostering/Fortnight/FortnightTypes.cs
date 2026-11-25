// Port of src/lib/stage2/fortnight.ts (its input and output types).
// ─── Stage 2: Fortnight Roster - PLAN-SLOT FILLER (interview model) ──────────
// The roster no longer re-solves shift shapes from demand: the PLAN (the
// shaper) is the single source of shift structure, and this engine's job is to
// put real named employees into those slots across all 14 days, honouring
// contracts and every cross-day rule.
//
// Shapes follow the TypeScript field for field (C# names are the TypeScript
// names in PascalCase), so the parity harness reads and writes them with
// RosterJson.Options. Roles, slot roles and task groups stay strings, as in
// Core. Dates stay ISO "yyyy-MM-dd" strings: the TypeScript keys its maps by
// them and the parity corpus carries them that way.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using Roster.Engine.Rostering.Shaper;

namespace Roster.Engine.Rostering.Fortnight;

// A single employee's availability + immovable-shift state for one date. The
// compute layer collapses availability flag, leave, weekend-off markers and
// granted day-off requests into the single `available` boolean here.
public sealed record DayAvail
{
    public bool Available { get; init; }
    /// <summary>Availability window start (minutes); null = open.</summary>
    public double? FromMin { get; init; }
    public double? ToMin { get; init; }
    /// <summary>Fixed roster or locked shift (immovable); null when none.</summary>
    public MinuteWindow? Fixed { get; init; }
    // Unavailable BECAUSE they're on approved leave (vs a weekend-off marker or
    // an availability window). Leave is paid to contract, so it's credited
    // against the contract rather than counting as an under-roster.
    public bool OnLeave { get; init; }
    // Hours credited per leave weekday for THIS leave entry. null means a full
    // contract day, which resolves to the employee's notional hours.
    public double? LeaveDayHours { get; init; }
}

public sealed record FortnightEmp
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>SM, ASM, DSM, RSM, SA or JSA (Core.Roles).</summary>
    public string Role { get; init; } = Roles.SA;
    /// <summary>
    /// PER FORTNIGHT - the unit contracts are written in (and what UKG holds).
    /// Anywhere the engine reasons week by week it divides by two.
    /// </summary>
    public double ContractHours { get; init; }
    public List<string> Restricted { get; init; } = new();
    // The value of one "day" toward contract (leave full-day credit, public
    // holidays not worked). null = the default, fortnight contract /10.
    public double? NotionalHours { get; init; }
}

/// <summary>A shift of the HELD plan with its approved tiles (DayContext.HeldShifts).</summary>
public sealed record HeldShift
{
    /// <summary>The plan slot role: MGR, SA or JSA.</summary>
    public string Role { get; init; } = SlotRoles.SA;
    public double Start { get; init; }
    public double End { get; init; }
    public List<ShiftBlock> Blocks { get; init; } = new();
}

public sealed record DayContext : IBreakGapDay
{
    public string Date { get; init; } = "";
    /// <summary>Mon-Sun week within the fortnight (days 0-6 / 7-13): 0 or 1.</summary>
    public int WeekIndex { get; init; }
    public bool IsSchoolDay { get; init; }
    public bool Closed { get; init; }
    // 3-tier calendar public holiday. Anyone NOT working it is credited their
    // notional hours toward contract; anyone working it earns penalty rates.
    public bool PublicHoliday { get; init; }
    public double StaffOpen { get; init; }
    public double StaffClose { get; init; }
    public double TradingOpen { get; init; }
    public double TradingClose { get; init; }
    // The day's PLAN: typed slots from the shaper - the shift structure to fill.
    public List<PlanSlot> Slots { get; init; } = new();
    // Shaper-adjusted demand (post pre-open compression) - used for task painting.
    public List<DemandBlock> DemandByBlock { get; init; } = new();
    /// <summary>Per-employee, keyed by employee id.</summary>
    public JsMap<string, DayAvail> Avail { get; init; } = new();
    // Task groups PERMITTED per block time (labour-group time barriers). When
    // provided, no tile is ever painted outside its group's allowed window.
    public JsMap<double, List<string>>? AllowedGroups { get; init; }
    // The windows of the day's NAMED admin/reset tasks. Work displaced by a
    // break is made up inside the job that asked for it, not somewhere else in
    // the group's much wider national window.
    public JsMap<string, List<MinuteWindow>>? TaskWindows { get; init; }
    // The job window of each planned Admin quarter-hour, and Monday's
    // date-check deadline - the same the plan was painted with.
    public JsMap<string, JsMap<double, List<JobWindow>>>? UnitWindows { get; init; }
    public MinuteWindow? AdminDeadline { get; init; }
    // The HELD plan's own shifts and tiles, when the roster is built from it:
    // the approved painting, which the roster keeps wherever it is legal for
    // the people who fill those shifts.
    public List<HeldShift>? HeldShifts { get; init; }
    // Minutes the plan already added that day so breaks don't cost work: the
    // roster may add only what is left of the day's allowance.
    public double? BreakTimeAddedMin { get; init; }
}

public sealed record FortnightInput
{
    /// <summary>14, in order.</summary>
    public List<DayContext> Days { get; init; } = new();
    public List<FortnightEmp> Employees { get; init; } = new();
    /// <summary>Record&lt;Role, BreakRule&gt;: one per role.</summary>
    public IReadOnlyDictionary<string, BreakRule> BreakRules { get; init; } = new Dictionary<string, BreakRule>();
    public List<string>? TaskPriority { get; init; }
    // Rostering-rule overrides (admin page) by RosteringRules key; omitted
    // fields use the defaults.
    public IReadOnlyDictionary<string, double>? Rules { get; init; }
    // The phase order Stage 2 runs (Roster Order admin page); omitted or empty
    // uses the default order - the client's approved initial order.
    public IReadOnlyList<OrderStep>? Order { get; init; }
}

/// <summary>
/// A roster shift: Core's Shift plus the slot-role class it serves. Mutable and
/// compared by reference, as the TypeScript objects are: the fill, the repair
/// and the painter all change shifts in place.
/// </summary>
public sealed class FShift : Shift
{
    // Which slot-role class this shift serves (MGR open/close, SA floor, JSA).
    // The repair pass swaps holders BETWEEN shifts, and a MGR slot may only ever
    // be handed to a manager - the shape survives every move. Null until the
    // day's fill records it.
    public string? SlotRole { get; set; }

    /// <summary>A shallow copy: the same blocks list, as <c>{ ...s }</c> in the TypeScript.</summary>
    public FShift Copy() => new()
    {
        EmployeeId = EmployeeId,
        Name = Name,
        Role = Role,
        Start = Start,
        End = End,
        BreakMinutes = BreakMinutes,
        Blocks = Blocks,
        Locked = Locked,
        Unfilled = Unfilled,
        SlotRole = SlotRole,
        Restricted = Restricted,
        Fixed = Fixed,
    };
}

public sealed class DayRoster
{
    public List<FShift> Shifts { get; set; } = new();
    public List<string> Flags { get; set; } = new();
}

// Where each person landed against their contract for the period - the input
// to MAKE-UP PAY (an employee under contract is paid the difference), and the
// numbers behind the under-contract flags.
public sealed record ContractOutcome
{
    public string EmployeeId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Role { get; init; } = Roles.SA;
    /// <summary>The fortnight contract as stored.</summary>
    public double ContractPeriodHours { get; init; }
    public double PaidHours { get; init; }
    /// <summary>Paid leave + public holidays not worked.</summary>
    public double CreditedHours { get; init; }
    /// <summary>Contract - paid - credited, floored at 0.</summary>
    public double ShortfallHours { get; init; }
}

public sealed class FortnightResult
{
    /// <summary>Keyed by ISO date, in the order of the input days.</summary>
    public JsMap<string, DayRoster> ByDate { get; init; } = new();
    /// <summary>Fortnight-level (e.g. under-contract).</summary>
    public List<string> Flags { get; init; } = new();
    public List<ContractOutcome> Outcomes { get; init; } = new();
}

// Running per-employee state across the fortnight, updated as shifts are placed.
public sealed class Ledger
{
    /// <summary>Paid hours in week 0 and week 1.</summary>
    public double[] PaidHoursByWeek { get; init; } = [0, 0];
    /// <summary>Date indices worked.</summary>
    public JsSet<int> DaysWorked { get; init; } = new();
    /// <summary>Date index → shift end (minutes).</summary>
    public JsMap<int, double> ShiftEndByDay { get; init; } = new();
}
