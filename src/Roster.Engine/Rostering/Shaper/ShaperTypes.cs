// Port of the types of src/lib/stage2/shaper.ts (the shaper itself is PlanShaper.cs).
// The shaper's output is a set of typed slots (MGR / SA / JSA) the roster then
// fills with real names; the demand blocks are the curve those slots cover.
//
// Roles and task groups stay strings, as in the TypeScript and in Core: they
// are keys, they appear in flags as text, and the parity corpus carries them
// as text. JSON (RosterJson): the TypeScript field names, camelCase.

using System.Text.Json.Serialization;
using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Shaper;

/// <summary>The plan's three slot types (SlotRole).</summary>
public static class SlotRoles
{
    public const string MGR = "MGR";
    public const string SA = "SA";
    public const string JSA = "JSA";

    public static readonly IReadOnlyList<string> All = [MGR, SA, JSA];
}

/// <summary>
/// One shift of the plan, to be filled with a real name by the roster. Mutable,
/// and compared by reference, as the TypeScript objects are: the shaper moves
/// slot edges and relabels slots in place.
/// </summary>
public sealed class PlanSlot
{
    public string Id { get; set; } = "";
    /// <summary>"MGR", "SA" or "JSA" (<see cref="SlotRoles"/>).</summary>
    public string Role { get; set; } = SlotRoles.SA;
    public double Start { get; set; } // minutes from midnight
    public double End { get; set; }

    public PlanSlot() { }

    public PlanSlot(string id, string role, double start, double end)
    {
        Id = id;
        Role = role;
        Start = start;
        End = end;
    }

    /// <summary><c>{ ...slot }</c></summary>
    public PlanSlot Clone() => new(Id, Role, Start, End);

    /// <summary><c>{ ...slot, start, end }</c></summary>
    public PlanSlot With(double start, double end) => new(Id, Role, start, end);

    public override string ToString() => $"{Id} {Role} {Grid.MinToTime(Start)}-{Grid.MinToTime(End)}";
}

/// <summary>
/// One quarter-hour of the day's demand: the bodies wanted (<see cref="Total"/>)
/// and the directed task groups among them. Total may exceed the groups' sum:
/// the rest is undirected (Non-Op) cover. Mutable, as in the TypeScript.
/// </summary>
public sealed class DemandBlock
{
    public double Time { get; set; }
    public double Total { get; set; }
    /// <summary>Task group → bodies, in insertion order (a TypeScript object's key order).</summary>
    public JsMap<string, double> Groups { get; set; } = new();

    public DemandBlock() { }

    public DemandBlock(double time, double total, JsMap<string, double>? groups = null)
    {
        Time = time;
        Total = total;
        Groups = groups ?? new JsMap<string, double>();
    }

    /// <summary><c>d.groups[group] ?? 0</c></summary>
    public double GroupCount(string group) => Groups.GetOr(group, 0);

    /// <summary><c>{ time, total, groups: { ...groups } }</c></summary>
    public DemandBlock Clone() => new(Time, Total, Groups.Copy());

    public override string ToString() =>
        $"{Grid.MinToTime(Time)} {Js.NumberToString(Total)} [{string.Join(", ", Groups.Select(g => $"{g.Key}={Js.NumberToString(g.Value)}"))}]";
}

/// <summary>A slot type's shift rule as the shaper reads it.</summary>
public sealed record ShaperRule
{
    public double MinShiftHours { get; init; }
    public double MaxShiftHours { get; init; }
    /// <summary>Break entitlement structure ("flat_30_unpaid" or "tiered"); defaults: MGR flat 30, SA/JSA tiered.</summary>
    public string? BreakType { get; init; }

    public ShaperRule() { }

    public ShaperRule(double minShiftHours, double maxShiftHours, string? breakType = null)
    {
        MinShiftHours = minShiftHours;
        MaxShiftHours = maxShiftHours;
        BreakType = breakType;
    }

    /// <summary>A role's break rule read as a shaper rule (the TypeScript passed the BreakRule itself).</summary>
    public static ShaperRule From(BreakRule rule) => new(rule.MinShiftHours, rule.MaxShiftHours, rule.BreakType);
}

/// <summary>A shift already COMMITTED for the date by a fixed roster.</summary>
public sealed record FixedShift(double Start, double End, string Role);

/// <summary>A Labour Rules window that carries its own hours.</summary>
public sealed record QuotedWindow(string Group, double Start, double End, double Hours);

public sealed record ShaperInput
{
    public double StaffOpen { get; init; }
    public double StaffClose { get; init; }
    public double TradingOpen { get; init; }
    public double TradingClose { get; init; }
    public bool IsSchoolDay { get; init; }
    public IReadOnlyList<DemandBlock> DemandByBlock { get; init; } = [];
    public ShaperRule MgrRule { get; init; } = new();
    public ShaperRule SaRule { get; init; } = new();
    public ShaperRule JsaRule { get; init; } = new();
    // Shifts already COMMITTED for this date by a fixed roster - a standing
    // day-of-week arrangement (e.g. "Grace works Mon 06:00-15:00"). Only
    // people actually available that day belong here (leave, a weekend-off
    // marker or a granted day-off removes theirs). The shaper reserves each as
    // a slot and cuts the rest of the curve AROUND it: without this the plan is
    // blind to commitments the roster is then forced to honour, so the fixed
    // shift lands on whatever slot happens to overlap it and its hours arrive
    // where the benchmark did not ask for them.
    public IReadOnlyList<FixedShift>? FixedShifts { get; init; }
    // Task groups PERMITTED at a block (labour-group time barriers) - the
    // levelling pass may move a group's hours anywhere its window allows. When
    // absent, a group's observed placement span is used as its window.
    [JsonIgnore] public Func<double, IReadOnlyList<string>?>? AllowedAt { get; init; }
    // Where work may be PLANNED (levelled), when narrower than allowedAt: the
    // load's make-up minutes after its window are for displaced work only.
    [JsonIgnore] public Func<double, IReadOnlyList<string>?>? PlaceAt { get; init; }
    // Groups Stage 1 placed TASK-FIRST (named admin / reset jobs with their own
    // deadlines). The levelling pass leaves these exactly where Stage 1 put
    // them instead of pooling them across the group's whole legal window.
    public IReadOnlyList<string>? PinnedGroups { get; init; }
    // Labour Rules windows that carry their own hours ("1h of cleaning at
    // 09:00-10:00"). Stage 1 fills these first; levelling keeps those hours
    // inside the window instead of pooling them with the group's other hours.
    public IReadOnlyList<QuotedWindow>? QuotedWindows { get; init; }
    // The least slack each group's break window must hold, in blocks - set
    // from what the PAINTER did with the last shape. The estimate below can
    // only approximate where the painter's breaks land, so computeDaySlots
    // paints each shape as a trial and, where work was still lost, asks for
    // the slack that shape had (ShapedDay.breakSlack) plus what it lost.
    public JsMap<string, double>? MinBreakSlack { get; init; }
    // Rostering-rule overrides (admin page), by the TypeScript key; omitted
    // fields use the defaults.
    public IReadOnlyDictionary<string, double>? Rules { get; init; }
    // Which way levelling breaks a tie for a group (its Planning Order
    // strategy): "early" or "late"; toward the middle of the day when absent.
    public JsMap<string, string>? LevelLean { get; init; }
    // Let the shave MOVE a store assistant's shift that cannot be shaved (see
    // slideShave). computeDaySlots asks for it when a painted day still has
    // shift time with no work, and keeps the result only if it is better.
    public bool SlideIdle { get; init; }
    // Where a window was run on into the close-down (evening Cleaning after a
    // 21:00 close; WindowRule.closeDownFrom): from here on, levelling fills a
    // quarter-hour only up to the people who must be on.
    public double? CloseDownFrom { get; init; }
}

public sealed record ShapedDay
{
    public List<PlanSlot> Slots { get; init; } = new();
    // Demand after pre-open compression - the curve the slots cover. Task
    // painting and the roster must use THIS, not the raw input, so tiles and
    // coverage line up with the (possibly later) floating pre-open start.
    public List<DemandBlock> DemandByBlock { get; init; } = new();
    public List<string> Flags { get; init; } = new();
    // Blocks of slack (bodies beyond the work) in each group's break window, as
    // shaped - the base computeDaySlots builds minBreakSlack from. Null (absent
    // in the TypeScript) for a day with no demand.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsMap<string, double>? BreakSlack { get; init; }
}

/// <summary>A shift's times, before or after a change: <c>{ start, end }</c>.</summary>
public sealed record ShiftTimes(double Start, double End);

/// <summary>What a trial paint returns: <c>{ painted, score }</c>.</summary>
public sealed record ScoredPaint<TPainted, TScore>(TPainted Painted, TScore Score);
