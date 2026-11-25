// Port of the shared vocabulary of src/lib/stage2/engine.ts (the painter itself
// is Planning/Stage2/Engine.cs).
// ─── Stage 2 shared primitives: roles, eligibility, tiles and shifts ─────────
// The shift STRUCTURE comes from the plan shaper and real names are attached by
// the fortnight filler. This holds what both sides (and the Task view) share:
// role/task vocabulary, eligibility rules and the shapes the task painter
// colours - one painter, so plan tiles and roster tiles can never disagree.
//
// Roles, labour groups and tile groups stay strings, as in the TypeScript: they
// are keys, they appear in the engine's flags as text, and the parity corpus
// carries them as text.

namespace Roster.Engine.Core;

/// <summary>The task groups a tile can carry, plus the three tile states that are not work.</summary>
public static class TaskGroups
{
    public const string Checkout = "checkout";
    public const string LoadRunning = "load_running";
    public const string Reset = "reset";
    public const string Admin = "admin";
    public const string Specials = "specials";
    public const string Cleaning = "cleaning";
    /// <summary>Booked time off the floor (training, meetings) - generic cover.</summary>
    public const string NonOp = "non_op";
    /// <summary>A tile with no work: paid time the day had no job for.</summary>
    public const string Flex = "flex";
    public const string Break = "break";

    // Assignment priority order (spec Section 7).
    public static readonly IReadOnlyList<string> TaskPriority = [Checkout, LoadRunning, Reset, Admin, Specials, Cleaning];

    /// <summary>The six directed task groups (TaskGroup in the TypeScript).</summary>
    public static bool IsTaskGroup(string g) => g is Checkout or LoadRunning or Reset or Admin or Specials or Cleaning;

    // Role → task eligibility (spec 8.1). Checkout = Registers+SCOs; JSA can't do
    // Registers so is excluded from the merged Checkout task; SA/JSA can't do Admin.
    public static bool EligibleFor(string role, string group)
    {
        if (group == Admin) return Roles.IsManager(role);
        if (group == Checkout) return role != Roles.JSA;
        return true; // load_running / reset / specials / cleaning - all roles
    }

    /// <summary>
    /// How many roles can do this group at all. Admin is manager-only (4 of 6),
    /// everything else is open to the whole team - so admin is the SCARCE skill.
    /// </summary>
    public static int EligibleRoleCount(string group) => Roles.All.Count(r => EligibleFor(r, group));
}

/// <summary>
/// A labour group's time window for the day (stage1/engine.ts WindowRule): the
/// hard barrier its work is planned, painted and checked inside.
/// </summary>
public sealed record WindowRule
{
    public string Group { get; init; } = "";
    public double Start { get; init; } // minutes from midnight
    public double End { get; init; }
    // Optional TARGET hours for this window. Quoted windows fill first, up to
    // their quota; unquoted windows take the remainder; anything that still
    // can't fit spills back into quoted windows (the window is the hard
    // legality barrier - the quota only steers placement). This is how "1 hour
    // of cleaning at 09:00-10:00, the rest in the evening" is stated.
    public double? Hours { get; init; }
    // Work may be MADE UP here but is not planned here: the load's few minutes
    // after its window, for the work the crew's staggered breaks displace
    // (owner decision 4).
    public bool MakeUpOnly { get; init; }
    // Set on the Cleaning window run on into the close-down (owner,
    // 14/11/2026; make-up-only): the time the evening window used to end. The
    // shaper may level Cleaning in there only for the people who must be on.
    public double? CloseDownFrom { get; init; }

    public WindowRule() { }

    public WindowRule(string group, double start, double end, double? hours = null)
    {
        Group = group;
        Start = start;
        End = end;
        Hours = hours;
    }
}

/// <summary>The per-role break and work rules (break_rules).</summary>
public sealed record BreakRule
{
    public double MinShiftHours { get; init; }
    public double MaxShiftHours { get; init; }
    public double GapHours { get; init; }
    /// <summary>"flat_30_unpaid" or "tiered".</summary>
    public string BreakType { get; init; } = Pay.BreakTypes.Tiered;
    // Fortnight-ledger inputs consumed by the rostering engine (rest / OT rules).
    public double? WeeklyCap { get; init; } // scheduled-hours cap per Mon-Sun week
    public double? ConsecDayCap { get; init; } // max consecutive worked days
    public double? Ot15AfterHours { get; init; } // daily paid hours before the 1.5x tier
    public double? Ot19AfterHours { get; init; } // daily paid hours before the 1.9x tier
}

/// <summary>One painted quarter-hour of a shift. Mutable: the painter recolours tiles in place.</summary>
public sealed class ShiftBlock
{
    public double Time { get; set; }
    /// <summary>A task group, or "non_op", "flex" or "break".</summary>
    public string Group { get; set; } = TaskGroups.Flex;
    // The tile's sub-task label (SubTasks), put on after the roster is built.
    // A label only: the engine never reads or writes it.
    public string? Sub { get; set; }

    public ShiftBlock() { }

    public ShiftBlock(double time, string group, string? sub = null)
    {
        Time = time;
        Group = group;
        Sub = sub;
    }

    public ShiftBlock Clone() => new(Time, Group, Sub);

    public override string ToString() => $"{Grid.MinToTime(Time)}={Group}{(Sub is null ? "" : "/" + Sub)}";
}

/// <summary>
/// A shift the task painter can colour. The painter reads role, span and
/// restrictions and writes <see cref="Blocks"/>; on its no-blank pass it may also
/// move a non-fixed shift's edges. Reference identity matters (the painter keys
/// maps by shift), so this is a class, never a record.
/// </summary>
public class PaintableShift
{
    public string Role { get; set; } = Roles.SA;
    public double Start { get; set; }
    public double End { get; set; }
    public List<ShiftBlock> Blocks { get; set; } = new();
    public List<string>? Restricted { get; set; }
    public bool Unfilled { get; set; }
    // A fixed roster or a manually pinned shift: its times are a commitment the
    // painter may not change. Work can still be traded in and out of it.
    public bool Fixed { get; set; }
}

/// <summary>A named roster shift (the roster engine's Shift).</summary>
public class Shift : PaintableShift
{
    public string EmployeeId { get; set; } = "";
    public string Name { get; set; } = "";
    public double BreakMinutes { get; set; }
    public bool Locked { get; set; } // manually pinned
    // (Unfilled: an OPEN shift nobody could legally take - needs external cover.)
}

public sealed record Stage2Result(List<Shift> Shifts, List<string> Flags);
