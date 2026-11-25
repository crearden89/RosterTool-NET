// Port of src/app/(app)/plan/roster/SubtaskView.tsx (ALL_SUBTASK_GROUPS and the switch's tones).
// ─── "Show sub-tasks" on the Roster and Plan pages ───────────────────────────
// Off by default, so the page looks exactly as it always has. On, the tiles of
// Reset, Load Running, Cleaning and Specials show their sub-task's code, the
// hover text names both ("Reset · Re-runs") and a legend of the codes appears.
// On the Roster page the printed roster carries them too; on the Plan page the
// Benchmark Hours table opens every group's sub-tasks and edit mode offers
// each sub-task as a paint chip. A viewing choice only: nothing is saved.
//
// The choice lives in a SubtaskViewProvider around the page; components read it
// as [CascadingParameter] public SubtaskViewProvider? SubtaskView (its On).

using Roster.Engine.SubTasks;
using Roster.Web.Features.RosterPage.Grid;

namespace Roster.Web.Features.RosterPage;

public static class ShowSubtasks
{
    /// <summary>The four groups opened up into their sub-tasks, for RosterGrid's Subtasks.</summary>
    public static readonly IReadOnlySet<string> AllSubtaskGroups = new HashSet<string>(Catalogue.SubtaskGroups);

    /// <summary>The grid's sub-task labels with every group open (the Roster page while the switch is on).</summary>
    public static readonly GridSubtasks AllOpen = new(AllSubtaskGroups);
}

/// <summary>
/// The switch's colours. On the navy floor bar (the Roster page's "on the floor"
/// bar) it is gold when on, so it reads on the navy; on a white bar (the Plan
/// page's generate bar) it is the same switch in the light controls' colours.
/// </summary>
public enum SubtaskToggleTone { Navy, Light }
