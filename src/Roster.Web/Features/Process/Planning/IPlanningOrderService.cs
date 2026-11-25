// Port of src/app/(app)/admin/process/planning/actions.ts and the reads in its page.tsx.

using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Rules;

namespace Roster.Web.Features.Process.Planning;

/// <summary>
/// What the Planning Order page reads: the saved rules (empty = the shipped default),
/// the settings (the defaults when the settings row is missing) and the step order
/// (the saved order, with any step it lacks appended).
/// </summary>
public sealed record PlanningOrderData(IReadOnlyList<PlacementRule> Saved, PlacementSettings Settings, IReadOnlyList<string> StepOrder);

/// <summary>The Planning Order page's read and actions (Super User only).</summary>
public interface IPlanningOrderService
{
    Task<PlanningOrderData> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// savePlacement: replace the whole configuration (the page edits it as one list). An
    /// empty rule list clears the table, restoring the shipped default. Saving rules leaves
    /// the master switch alone: Enabled flips only through <see cref="SetPlacementEnabledAsync"/>,
    /// so an edit can never silently switch the pass on.
    /// </summary>
    Task<SaveResult> SavePlacementAsync(IReadOnlyList<PlacementRule> rules, PlacementSettings settings, CancellationToken cancellationToken = default);

    /// <summary>resetPlacement: the shipped default rules, with these settings.</summary>
    Task<SaveResult> ResetPlacementAsync(PlacementSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// setPlacementEnabled: the MASTER SWITCH. While off, Stage 1 ignores this page
    /// entirely and levels every movable group evenly across its window. Its own action -
    /// never a side effect of saving rules - so switching the engine over is always a
    /// deliberate, visible act.
    /// </summary>
    Task<SaveResult> SetPlacementEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// saveStepOrder: which group gets the best of the day (steps 1-6), as a plain ordered
    /// list on the single settings row; the reader appends anything missing, so a partial
    /// save can never drop a group's hours.
    /// </summary>
    Task<SaveResult> SaveStepOrderAsync(IReadOnlyList<string> order, CancellationToken cancellationToken = default);
}

/// <summary>The actions' checks and the values they store.</summary>
public static class PlanningOrderChecks
{
    private static readonly HashSet<string> AnchorKinds = ["none", "clock", "open", "close"];

    /// <summary>savePlacement's refusals: an unknown group, strategy or anchor, or a group twice.</summary>
    public static string? CheckRules(IReadOnlyList<PlacementRule> rules)
    {
        var strategies = Placement.Strategies.Select(m => m.Strategy).ToHashSet();
        foreach (var r in rules)
        {
            if (!Placement.PlacementGroups.Contains(r.Group)) return $"Unknown labour group {r.Group}";
            if (!strategies.Contains(r.Strategy)) return $"Unknown strategy {r.Strategy}";
            if (!AnchorKinds.Contains(r.AnchorKind)) return $"Unknown anchor kind {r.AnchorKind}";
            if (!string.IsNullOrEmpty(r.NightTraderStrategy) && !strategies.Contains(r.NightTraderStrategy))
                return $"Unknown night-trader strategy {r.NightTraderStrategy}";
        }
        var groups = rules.Select(r => r.Group).ToList();
        var dupes = groups.Where((g, i) => groups.IndexOf(g) != i).ToList();
        return dupes.Count > 0 ? $"{dupes[0]} appears more than once - each group needs one rule." : null;
    }

    /// <summary>A share held to 0..100; null (or NaN) stays null.</summary>
    public static double? Pct(double? n) => n is not { } v || double.IsNaN(v) ? null : Js.Min(100, Js.Max(0, v));

    /// <summary>The peak smoothing as stored: whole blocks, 1..16.</summary>
    public static double SmoothingBlocks(double blocks) => Js.Min(16, Js.Max(1, Js.Round(blocks)));

    /// <summary>The peak threshold as stored: 1..100%.</summary>
    public static double ThresholdPct(double pct) => Js.Min(100, Js.Max(1, pct));

    /// <summary>
    /// minToSqlTime for a time column: the whole-minute time, or why the database would
    /// refuse it (a cleared time box reads NaN).
    /// </summary>
    public static (TimeOnly? Time, string? Error) SqlTime(double minutes)
    {
        var whole = Js.Round(minutes);
        if (!double.IsFinite(whole) || whole < 0 || whole >= 24 * 60)
            return (null, $"invalid input syntax for type time: \"{Grid.MinToSqlTime(minutes)}\"");
        return (new TimeOnly((int)Math.Floor(whole / 60), (int)(whole % 60)), null);
    }

    /// <summary>saveStepOrder: the known steps, each exactly once - or why not.</summary>
    public static (IReadOnlyList<string>? Order, string? Error) CheckStepOrder(IReadOnlyList<string> order)
    {
        var clean = order.Where(g => Placement.StepGroups.Contains(g)).ToList();
        if (clean.Distinct().Count() != clean.Count) return (null, "A group appears twice in the order.");
        if (clean.Count != Placement.StepGroups.Count) return (null, "Every step must appear exactly once.");
        return (clean, null);
    }

    /// <summary>The page's step order: the saved one (known groups only), then any group it lacks.</summary>
    public static IReadOnlyList<string> StepOrderFrom(IReadOnlyList<string>? saved)
    {
        var known = (saved ?? []).Where(g => WindowGroups.All.Contains(g)).ToList();
        return [.. known, .. WindowGroups.DefaultStepOrder.Where(g => !known.Contains(g))];
    }
}
