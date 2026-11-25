// Port of src/app/(app)/admin/rules/breaks/actions.ts and the read in its page.tsx.

namespace Roster.Web.Features.Rules.Breaks;

/// <summary>
/// One role's award limits, as the Break &amp; Work Rules table shows and saves them
/// (break_rules). The pay-rule thresholds on the same table are edited on the
/// Rostering Rules page and are not part of this row.
/// </summary>
/// <param name="BreakType">"flat_30_unpaid" or "tiered".</param>
public sealed record BreakRuleInput(
    string Role,
    double MinShiftHours,
    double MaxShiftHours,
    double GapHours,
    double? WeeklyCap,
    double? ConsecDayCap,
    string BreakType);

/// <summary>The Break &amp; Work Rules page's read and its one action (Super User only).</summary>
public interface IBreakRulesService
{
    /// <summary>Every role's row, SM, ASM, DSM, RSM, SA, JSA (any other role first, by name).</summary>
    Task<IReadOnlyList<BreakRuleInput>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// saveBreakRules: the whole table, for every store. Upserts by role and writes only
    /// these columns, so the Rostering Rules page's pay thresholds are never clobbered.
    /// </summary>
    Task<SaveResult> SaveAsync(IReadOnlyList<BreakRuleInput> rules, CancellationToken cancellationToken = default);
}

public static class BreakRulesOrder
{
    /// <summary>The page's role order: SM, ASM, DSM, RSM, SA, JSA.</summary>
    public static readonly IReadOnlyList<string> Roles = Roster.Engine.Core.Roles.All;

    /// <summary>
    /// The rows as the page lists them: read ordered by role, then a stable sort by the
    /// fixed role order, where a role outside it (indexOf = -1) sorts first.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> rowsByRole, Func<T, string> role) =>
        rowsByRole.OrderBy(r => IndexOf(role(r))).ToList();

    private static int IndexOf(string role)
    {
        for (var i = 0; i < Roles.Count; i++)
            if (Roles[i] == role) return i;
        return -1;
    }
}
