// Port of src/app/(app)/admin/process/roster/actions.ts and the read in its page.tsx.

using Roster.Engine.Core;
using Roster.Engine.Rostering.Painter;
using Roster.Web.Features.Rules;

namespace Roster.Web.Features.Process.RosterPhases;

/// <summary>The Roster Order page's read and actions (Super User only).</summary>
public interface IRosterOrderService
{
    /// <summary>
    /// The configured phases, normalised the way the engine normalises them before it runs
    /// (a stale step type or malformed day order is dropped); empty when nothing is saved,
    /// which means the default order.
    /// </summary>
    Task<IReadOnlyList<OrderStep>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// saveRosterOrder: replace the whole configured order (the page edits it as one list).
    /// An empty list clears the table, restoring the shipped default order.
    /// </summary>
    Task<SaveResult> SaveAsync(IReadOnlyList<OrderStep> steps, CancellationToken cancellationToken = default);

    /// <summary>resetRosterOrder: back to the shipped default order.</summary>
    Task<SaveResult> ResetAsync(CancellationToken cancellationToken = default);
}

public static class RosterOrderChecks
{
    /// <summary>saveRosterOrder's refusals: an unknown step, or a day order that is not the seven days once each.</summary>
    public static string? CheckSteps(IReadOnlyList<OrderStep> steps)
    {
        foreach (var s in steps)
        {
            if (!OrderStepTypes.All.Contains(s.Type)) return $"Unknown step {s.Type}";
            if (s.DayOrder is { } d && !IsWeek(d)) return "Day order must use each of the 7 days exactly once.";
        }
        return null;
    }

    /// <summary>Each of the seven days (Monday = 0) exactly once.</summary>
    public static bool IsWeek(IReadOnlyList<int> d) => d.Count == 7 && d.Order().SequenceEqual([0, 1, 2, 3, 4, 5, 6]);
}

/// <summary>The day-order box's text (OrderClient.tsx dayOrderText / parseDayOrder).</summary>
public static class DayOrderText
{
    /// <summary>"Sat, Sun, Tue, Fri, Mon, Wed, Thu".</summary>
    public static string Format(IReadOnlyList<int>? d) => string.Join(", ", (d ?? []).Select(i => RosterOrder.DowLabels[i]));

    /// <summary>The seven days, each once, in the order typed (any case); null otherwise.</summary>
    public static List<int>? Parse(string text)
    {
        var parts = text.Split(',').Select(p => Js.Trim(p).ToLowerInvariant()).Where(p => p != "").ToList();
        var idx = parts.Select(p => RosterOrder.DowLabels.ToList().FindIndex(l => l.ToLowerInvariant() == p)).ToList();
        if (idx.Count != 7 || idx.Any(i => i < 0)) return null;
        return RosterOrderChecks.IsWeek(idx) ? idx : null;
    }
}
