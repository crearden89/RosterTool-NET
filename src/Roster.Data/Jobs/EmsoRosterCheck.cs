// Port of src/lib/emso/rosterCheck.ts.
namespace Roster.Data.Jobs;

// The EMSO unapproved-roster check (spec §8): for the period stores should
// currently have finished (the planning period), any store in an EMSO's area
// that has benchmark demand but no fully-approved roster generates an in-app
// notification for that EMSO, shown on their next login. Idempotent per EMSO +
// period: an existing unread alert for the same period is refreshed, not
// duplicated. Meant to be run at Wed 18:00 of the planning week (via a scheduled
// job) but is also runnable on demand by a super user (Admin → EMSO, "Run now").

/// <summary>What one run of the check wrote: alerts created, refreshed, and writes that failed.</summary>
public sealed record EmsoCheckResult(int Created, int Updated, int Failed);

/// <summary>runEmsoUnapprovedCheck. Runs as the server (the TypeScript app used the service role).</summary>
public interface IEmsoRosterCheck
{
    /// <summary>
    /// Raise or refresh one "emso_unapproved" notification per EMSO whose area has a store
    /// with benchmark demand but no approved roster on some day of the planning period
    /// (the period after the one containing today in Sydney). The caller checks who may run
    /// it (the admin action requires a super user; the scheduled job is the server).
    /// </summary>
    Task<EmsoCheckResult> RunEmsoUnapprovedCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>The notification the check writes.</summary>
public static class EmsoAlert
{
    /// <summary>notifications.kind of the alert.</summary>
    public const string Kind = "emso_unapproved";

    /// <summary>Where the alert links to.</summary>
    public const string Link = "/overview";

    /// <summary>"3 store(s) without an approved roster for 07/12 – 20/12: G101, G102, G205".</summary>
    public static string Body(IReadOnlyList<string> behindStoreCodes, string periodLabel) =>
        $"{behindStoreCodes.Count} store(s) without an approved roster for {periodLabel}: {string.Join(", ", behindStoreCodes)}";
}

internal sealed class EmsoRosterCheck : IEmsoRosterCheck
{
    public Task<EmsoCheckResult> RunEmsoUnapprovedCheckAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
