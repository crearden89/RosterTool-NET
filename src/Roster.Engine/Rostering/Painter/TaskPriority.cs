// Port of src/lib/stage2/priority.ts (the pure part; reading labour_group_priority,
// ordered by priority, and caching it per request are the data layer's).

using Roster.Engine.Core;

namespace Roster.Engine.Rostering.Painter;

public static class TaskPriority
{
    /// <summary>
    /// The config-driven task-assignment priority order (labour_group_priority),
    /// falling back to the engine default if the table is empty/unavailable.
    /// <paramref name="labourGroupsInPriorityOrder"/> is the table's labour_group
    /// column, ordered by priority (null when the table could not be read).
    /// Unknown groups are dropped, and any task group missing from the config is
    /// appended so none are ever dropped.
    /// </summary>
    public static List<string> FromConfig(IEnumerable<string?>? labourGroupsInPriorityOrder)
    {
        var known = new HashSet<string>(TaskGroups.TaskPriority);
        var ordered = (labourGroupsInPriorityOrder ?? []).Where(g => g != null && known.Contains(g)).Select(g => g!).ToList();
        // Append any task groups missing from the config so none are ever dropped.
        foreach (var g in TaskGroups.TaskPriority)
            if (!ordered.Contains(g)) ordered.Add(g);
        return ordered.Count > 0 ? ordered : [.. TaskGroups.TaskPriority];
    }
}
