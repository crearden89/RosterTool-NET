// Port of the feed_tokens reads and writes in src/app/api/ingest/[type]/route.ts.

using Microsoft.EntityFrameworkCore;

namespace Roster.Data.Feeds;

/// <summary>A data-feed token's row: who is pushing.</summary>
public sealed record FeedTokenInfo(Guid Id, string Label);

/// <summary>The bearer tokens IT's sync jobs authenticate with (Super User → Data Feed).</summary>
public interface IFeedTokens
{
    /// <summary>The token's row, or null when no such token exists.</summary>
    Task<FeedTokenInfo?> FindAsync(string token, CancellationToken ct = default);

    /// <summary>Stamp the token as used now.</summary>
    Task TouchAsync(Guid id, CancellationToken ct = default);
}

// feed_tokens has row-level security with no policies: only the server reads or writes it.
public sealed class FeedTokens(IRosterDbContextFactory contexts, TimeProvider clock) : IFeedTokens
{
    private const string Reason = "data-feed token check (feed_tokens is server-only)";

    public async Task<FeedTokenInfo?> FindAsync(string token, CancellationToken ct = default)
    {
        await using var db = contexts.CreateSystem(Reason);
        return await db.FeedTokens.AsNoTracking().Where(t => t.Token == token)
            .Select(t => new FeedTokenInfo(t.Id, t.Label)).FirstOrDefaultAsync(ct);
    }

    public async Task TouchAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = contexts.CreateSystem(Reason);
        var now = clock.GetUtcNow();
        await db.FeedTokens.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
    }
}
