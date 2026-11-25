// Port of the edit mode of src/app/(app)/plan/generate/DayPlanBlock.tsx: its state and
// handlers (start, cancel, paint, stretch, trim, add, remove, cycle the role, the checks
// before Save), kept apart from the component so they can be tested on their own.

using Roster.Engine.Core;
using Roster.Engine.Pay;
using Roster.Engine.Planning.Stage1;
using Roster.Engine.SubTasks;

namespace Roster.Web.Features.Plan;

/// <summary>
/// The paint tools: a labour group ("registers", "load_running", "reset", "admin",
/// "specials", "cleaning", "non_op"), "break", "erase", or a sub-task key (an opened group's
/// chip: paints its group, labelled). Breaks are paintable/erasable like any tile so they can
/// be MOVED - but Save refuses a day whose shifts don't still carry their exact break
/// entitlement (right minutes, right segments).
/// </summary>
public static class PaintTools
{
    public const string Registers = "registers";
    public const string Break = "break";
    public const string Erase = "erase";

    /// <summary>
    /// What a paint tool puts on a tile. Painting with a group alone leaves the tile
    /// unlabelled; Save labels it by the normal rules.
    /// </summary>
    public static (string Group, string? Sub) PaintOf(string tool)
    {
        if (tool == Erase) return (TaskGroups.Flex, null);
        return Catalogue.SubtaskByKey.TryGetValue(tool, out var st) ? (st.Group, st.Key) : (tool, null);
    }

    /// <summary>A tile painted with <paramref name="tool"/>: its group, and the sub-task if the tool is one (any old label goes).</summary>
    public static DisplayBlock Painted(DisplayBlock b, string tool)
    {
        var (group, sub) = PaintOf(tool);
        return b with { Group = group, Sub = sub };
    }
}

/// <summary>The Plan page's edit mode for one day: the draft of the day's shifts and the paint tool.</summary>
public sealed class DayPlanEditor
{
    private static readonly string[] RoleCycle = [RoleBadges.Mgr, RoleBadges.Sa, RoleBadges.Jsa];

    public bool Editing { get; private set; }

    /// <summary>The selected paint tool, or null.</summary>
    public string? Tool { get; private set; }

    /// <summary>The day's shifts as edited so far (null outside edit mode).</summary>
    public List<PlanShift>? Draft { get; private set; }

    public string? Error { get; set; }

    /// <summary>The shifts to draw: the draft while editing, else the held plan's.</summary>
    public IReadOnlyList<PlanShift> ShiftsOf(PlanShiftComputation plan) => Editing && Draft is not null ? Draft : plan.Shifts;

    public void Start(PlanShiftComputation plan)
    {
        Draft = plan.Shifts.Select(s => s with { Blocks = s.Blocks.Select(b => b with { }).ToList() }).ToList();
        Tool = null;
        Error = null;
        Editing = true;
    }

    /// <summary>Leave edit mode, dropping the draft (Cancel, a save that went through, another day).</summary>
    public void Stop()
    {
        Editing = false;
        Draft = null;
        Tool = null;
        Error = null;
    }

    /// <summary>setTool(tool).</summary>
    public void SetTool(string? tool) => Tool = tool;

    /// <summary>A chip pressed: picks its tool, or puts it down when it is already picked.</summary>
    public void ToggleTool(string tool) => Tool = Tool == tool ? null : tool;

    // ── Whole shifts: the two things painting can't express ───────────────────

    public void RemoveShift(string slotId)
    {
        if (Draft is null) return;
        Error = null;
        Draft = Draft.Where(s => s.SlotId != slotId).ToList();
    }

    /// <summary>
    /// A new row lands as a plain SA shift over the middle of the staffed day; its real shape
    /// is then painted on the grid like any other. It goes where the hierarchy order puts it
    /// (managers, assistants, juniors).
    /// </summary>
    public void AddShift(Stage1Computation result, string slotId)
    {
        if (Draft is null) return;
        var dayLen = result.StaffClose - result.StaffOpen;
        if (dayLen < PlanEdit.MinPlanShiftMin)
        {
            Error = "The staffed day is too short to add a shift.";
            return;
        }
        var start = Math.Min(Math.Max(result.StaffOpen, 9 * 60), result.StaffClose - PlanEdit.MinPlanShiftMin);
        var end = Math.Min(start + 8 * 60, result.StaffClose);
        Error = null;
        Draft = PlanEdit.PlaceNewShift(Draft, PlanEdit.ReshapePlanShift(null, slotId, RoleBadges.Sa, start, end));
    }

    /// <summary>
    /// The role badge cycles MGR → SA → JSA. Break entitlement follows the role (managers use
    /// the flat template), so the shift is rebuilt rather than relabelled.
    /// </summary>
    public void CycleRole(string slotId)
    {
        if (Draft is null) return;
        Error = null;
        Draft = Draft.Select(s =>
        {
            if (s.SlotId != slotId) return s;
            var next = RoleCycle[(Array.IndexOf(RoleCycle, s.PlanRole) + 1) % RoleCycle.Length];
            return PlanEdit.ReshapePlanShift(s, s.SlotId, next, s.Start, s.End);
        }).ToList();
    }

    /// <summary>
    /// One click on one 15-min cell. Inside the shift it paints (or erases) the tile. At the
    /// EDGES it changes the shift itself, which is the natural way to reshape a plan: paint an
    /// hour before a 07:00 start and the shift simply becomes 06:00.
    /// <list type="bullet">
    /// <item>paint OUTSIDE the span → stretch the shift out to reach that cell;</item>
    /// <item>erase the FIRST or LAST tile → trim the shift back by that cell.</item>
    /// </list>
    /// Anything else is an ordinary recolour.
    /// </summary>
    public void CellClick(Stage1Computation result, string rowKey, double time)
    {
        if (!Editing || Tool is not { } tool || Draft is null) return;
        var target = Draft.FirstOrDefault(s => s.SlotId == rowKey);
        if (target is null) return;
        var inSpan = time >= target.Start && time < target.End;

        // ── Stretch: painting beyond either end of the shift.
        if (!inSpan)
        {
            if (tool == PaintTools.Erase) return; // nothing to erase off the shift
            var start = Math.Min(target.Start, time);
            var end = Math.Max(target.End, time + 15);
            var problem = PlanEdit.PlanShiftError(start, end, result.StaffOpen, result.StaffClose);
            if (problem is not null)
            {
                Error = $"{target.PlanRole} {Grid.MinToTime(start)}-{Grid.MinToTime(end)} {problem}.";
                return;
            }
            Error = null;
            Draft = Draft.Select(s =>
            {
                if (s.SlotId != rowKey) return s;
                var grown = PlanEdit.ReshapePlanShift(s, s.SlotId, s.PlanRole, start, end);
                // The cell that was clicked takes the selected group; the rest of any
                // newly-covered time stays unpainted.
                return grown with { Blocks = grown.Blocks.Select(b => b.Time == time ? PaintTools.Painted(b, tool) : b).ToList() };
            }).ToList();
            return;
        }

        // ── Trim: erasing the shift's first or last tile pulls the edge in.
        var isEdge = time == target.Start || time == target.End - 15;
        if (tool == PaintTools.Erase && isEdge)
        {
            var start = time == target.Start ? target.Start + 15 : target.Start;
            var end = time == target.End - 15 ? target.End - 15 : target.End;
            var problem = PlanEdit.PlanShiftError(start, end, result.StaffOpen, result.StaffClose);
            if (problem is not null)
            {
                Error = $"{target.PlanRole} can't be trimmed any further - it {problem}.";
                return;
            }
            Error = null;
            Draft = Draft.Select(s => s.SlotId == rowKey ? PlanEdit.ReshapePlanShift(s, s.SlotId, s.PlanRole, start, end) : s).ToList();
            return;
        }

        // ── Ordinary recolour.
        Draft = Draft.Select(s =>
            s.SlotId != rowKey ? s : s with { Blocks = s.Blocks.Select(b => b.Time == time ? PaintTools.Painted(b, tool) : b).ToList() }).ToList();
    }

    /// <summary>
    /// Each shift must still carry EXACTLY its break entitlement before the day can be saved -
    /// moving a break is fine, shrinking or growing it is not.
    /// </summary>
    public static List<string> BreakViolations(IEnumerable<PlanShift> shifts)
    {
        var out_ = new List<string>();
        foreach (var s in shifts)
        {
            var times = s.Blocks.Where(b => b.Group == TaskGroups.Break).Select(b => b.Time);
            if (Breaks.BreakTileError(times, s.BreakMinutes, s.Start, s.End) is { } problem)
                out_.Add($"{s.PlanRole} {Grid.MinToTime(s.Start)}-{Grid.MinToTime(s.End)} {problem}");
        }
        return out_;
    }

    /// <summary>
    /// What stops the draft being saved, before it is sent (save()'s own checks): no shifts,
    /// a shift outside the rules, or a break entitlement not met. Null when it may be sent.
    /// </summary>
    public string? SaveProblem(Stage1Computation result)
    {
        if (Draft is null) return null;
        if (Draft.Count == 0) return "A day needs at least one shift.";
        foreach (var s in Draft)
        {
            if (PlanEdit.PlanShiftError(s.Start, s.End, result.StaffOpen, result.StaffClose) is { } problem)
                return $"{s.PlanRole} {Grid.MinToTime(s.Start)}-{Grid.MinToTime(s.End)} {problem}.";
        }
        var violations = BreakViolations(Draft);
        return violations.Count > 0 ? $"Break requirements not met: {string.Join("; ", violations)}" : null;
    }

    /// <summary>The draft as the save sends it: each shift's slot, role, span and painted tiles.</summary>
    public IReadOnlyList<PlanShiftEdit> Edits() =>
        (Draft ?? []).Select(s => new PlanShiftEdit(s.SlotId, s.PlanRole, s.Start, s.End, s.Blocks.Select(b => new PlanBlockEdit(b.Time, b.Group, string.IsNullOrEmpty(b.Sub) ? null : b.Sub)).ToList())).ToList();

    /// <summary>
    /// A new shift's slot id, in its own id space so it can never collide with a shaper slot id
    /// (`manual-${Date.now().toString(36)}`).
    /// </summary>
    public static string ManualSlotId(DateTimeOffset now)
    {
        var n = now.ToUnixTimeMilliseconds();
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var chars = new Stack<char>();
        do
        {
            chars.Push(digits[(int)(n % 36)]);
            n /= 36;
        } while (n > 0);
        return "manual-" + new string([.. chars]);
    }
}
