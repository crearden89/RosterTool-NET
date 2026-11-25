// Port of src/app/(app)/admin/rules/rostering/actions.ts and the reads in its page.tsx.

using Roster.Engine.Core;

namespace Roster.Web.Features.Rules.Rostering;

/// <summary>
/// One stored override (rostering_rules): a RosteringRules field name for a built-in
/// rule, or any custom key the super user chose. Deleting it restores the default.
/// </summary>
public sealed record RuleOverrideRow(string Key, double Value, string? Note);

/// <summary>
/// One role's pay-rule thresholds. Stored on break_rules but EDITED here - they are
/// rostering rules in the client's mental model. Null switches the rule off for the
/// role, which is how the salaried SM/ASM are kept out of overtime and double time.
/// </summary>
/// <param name="OtShiftAfterHours">1.5x after this many PAID hours in a shift (ot_1_5_after_hours).</param>
/// <param name="OtPeriodAfterHours">1.5x after this many PAID hours in the fortnight.</param>
/// <param name="DoubleTimeUnderGapHours">2x when the break before a shift is shorter.</param>
public sealed record PayRuleInput(string Role, double? OtShiftAfterHours, double? OtPeriodAfterHours, double? DoubleTimeUnderGapHours);

/// <summary>What the Rostering Rules page reads: every override (by key) and each role's pay thresholds.</summary>
public sealed record RosteringRulesData(IReadOnlyList<RuleOverrideRow> Overrides, IReadOnlyList<PayRuleInput> PayRules);

/// <summary>The Rostering Rules page's reads and actions (Super User only).</summary>
public interface IRosteringRulesService
{
    /// <summary>Every override ordered by key; the pay thresholds in role order SM, ASM, DSM, RSM, SA, JSA.</summary>
    Task<RosteringRulesData> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// setRuleOverride: create or update a rule override. <paramref name="key"/> is a
    /// RosteringRules field name for recognised rules, or any custom identifier.
    /// </summary>
    Task<SaveResult> SetRuleOverrideAsync(string key, double value, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>clearRuleOverride: remove an override - the engine returns to the shipped default for that rule.</summary>
    Task<SaveResult> ClearRuleOverrideAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// savePayRules: each role's three thresholds. Only these three columns of
    /// break_rules are touched, so the Break &amp; Work Rules page's own save can never
    /// clobber them (and vice versa).
    /// </summary>
    Task<SaveResult> SavePayRulesAsync(IReadOnlyList<PayRuleInput> rules, CancellationToken cancellationToken = default);
}

/// <summary>The actions' checks, before anything is written.</summary>
public static class RosteringRulesChecks
{
    /// <summary>setRuleOverride: the key as stored (trimmed), or why it was refused.</summary>
    public static (string? Key, string? Error) CheckOverride(string key, double value)
    {
        var k = Js.Trim(key);
        if (k.Length == 0 || k.Length > 64) return (null, "Enter a rule key (max 64 chars).");
        if (!double.IsFinite(value)) return (null, "Enter a numeric value.");
        return (k, null);
    }

    /// <summary>savePayRules: every threshold positive and at most 200, or blank (off).</summary>
    public static string? CheckPayRules(IReadOnlyList<PayRuleInput> rules)
    {
        foreach (var r in rules)
        {
            foreach (var v in new[] { r.OtShiftAfterHours, r.OtPeriodAfterHours, r.DoubleTimeUnderGapHours })
            {
                if (v is { } n && !(n > 0 && n <= 200))
                    return $"{r.Role}: thresholds must be positive (or blank for off).";
            }
        }
        return null;
    }
}
