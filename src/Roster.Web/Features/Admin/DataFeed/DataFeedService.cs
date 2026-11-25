// Port of src/app/(app)/admin/data-feed/page.tsx (its reads) and actions.ts.

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Scope;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.DataFeed;

public sealed record FeedTokenRow(Guid Id, string Label, string Token, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>A batch pushed through the API (filename "api:&lt;label&gt;").</summary>
public sealed record FeedBatchRow(
    Guid Id, string Type, string Filename, DateTimeOffset UploadedAt, int TotalRows, int AcceptedRows, int RejectedRows)
{
    /// <summary>The token label that pushed it: the filename without "api:".</summary>
    public string Source => Filename.StartsWith("api:", StringComparison.Ordinal) ? Filename[4..] : Filename;
}

public sealed record DataFeedView(IReadOnlyList<FeedTokenRow> Tokens, IReadOnlyList<FeedBatchRow> Batches);

public interface IDataFeedService
{
    /// <summary>The tokens (oldest first) and the last 20 batches received through the API.</summary>
    Task<DataFeedView> LoadAsync(CancellationToken ct = default);

    Task<AdminResult> CreateFeedTokenAsync(string label, CancellationToken ct = default);

    /// <summary>Rotate: the old token stops authenticating immediately.</summary>
    Task<AdminResult> RotateFeedTokenAsync(Guid id, CancellationToken ct = default);

    Task<AdminResult> DeleteFeedTokenAsync(Guid id, CancellationToken ct = default);
}

internal sealed class DataFeedService(IRosterDbContextFactory contexts, IStoreScope scope) : IDataFeedService
{
    private static string NewToken() => AdminStores.RandomHex(32);

    public async Task<DataFeedView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Data Feed: tokens and recent API pushes");
        var tokens = await db.FeedTokens.AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new FeedTokenRow(t.Id, t.Label, t.Token, t.CreatedAt, t.LastUsedAt))
            .ToListAsync(ct);
        // Batches pushed through the API are recorded like manual uploads, with
        // an api:<label> filename - the recent list shows the feed is alive.
        var batches = await db.UploadBatches.AsNoTracking()
            .Where(b => b.Filename.StartsWith("api:"))
            .OrderByDescending(b => b.UploadedAt)
            .Take(20)
            .Select(b => new FeedBatchRow(b.Id, b.Type, b.Filename, b.UploadedAt, b.TotalRows, b.AcceptedRows, b.RejectedRows))
            .ToListAsync(ct);
        return new DataFeedView(tokens, batches);
    }

    public async Task<AdminResult> CreateFeedTokenAsync(string label, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var l = label.Trim();
        if (l.Length == 0) return AdminResult.Fail("Give the token a label (e.g. 'area-db-sync').");
        await using var db = contexts.CreateSystem("Data Feed: create a token");
        db.FeedTokens.Add(new FeedToken { Label = l, Token = NewToken() });
        return await AdminDb.WriteAsync(() => db.SaveChangesAsync(ct));
    }

    public async Task<AdminResult> RotateFeedTokenAsync(Guid id, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Data Feed: rotate a token");
        var token = NewToken();
        return await AdminDb.WriteAsync(() =>
            db.FeedTokens.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.Token, token), ct));
    }

    public async Task<AdminResult> DeleteFeedTokenAsync(Guid id, CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        await using var db = contexts.CreateSystem("Data Feed: delete a token");
        return await AdminDb.WriteAsync(() => db.FeedTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct));
    }
}

public sealed class DataFeedModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IDataFeedService, DataFeedService>();
}
