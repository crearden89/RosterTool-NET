// Port of the database work in src/app/api/cron/keep-alive/route.ts (the HTTP and
// schedule parts are in src/Roster.Web/Features/Jobs).
namespace Roster.Data.Jobs;

// Daily keep-alive. Supabase paused a free-tier project after 7 days with no
// activity, and the TypeScript app's only other scheduled traffic was the EMSO
// check - once a week, which sat exactly ON that threshold with no margin. One
// skipped run, or one quiet week, and the database went offline mid-planning-week.
// It did, on 12/09/2026. The same holds for a SQL database that pauses itself when
// idle (an auto-pausing serverless tier); on a server that never pauses it is harmless.
//
// This touches the database on purpose: a real query, and a heartbeat write, so
// the database registers both read and write activity.

/// <summary>The outcome of one keep-alive: the store count, or the error that stopped it.</summary>
public sealed record KeepAliveResult(bool Ok, int Stores, string? Error)
{
    public static KeepAliveResult Success(int stores) => new(true, stores, null);
    public static KeepAliveResult Failure(string error) => new(false, 0, error);
}

/// <summary>The keep-alive's read: count the stores (as the server).</summary>
public interface IKeepAlive
{
    /// <summary>
    /// Count the stores. A failure is returned, not thrown: a keep-alive that quietly fails
    /// is worse than none at all, because it looks like cover that is not there. The caller
    /// records the heartbeat on success.
    /// </summary>
    Task<KeepAliveResult> TouchAsync(CancellationToken cancellationToken = default);
}

internal sealed class KeepAlive : IKeepAlive
{
    public Task<KeepAliveResult> TouchAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
