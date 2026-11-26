// Port of src/app/(app)/admin/uploads/page.tsx (its reads) and src/app/api/uploads/[type]/route.ts,
// which the Uploader called over HTTP; the Blazor page calls the same pipeline in-process
// (Roster.Data.Feeds.IUploadIngest, the port of lib/csv/ingest).

using Microsoft.EntityFrameworkCore;
using Roster.Data;
using Roster.Data.Entities;
using Roster.Data.Feeds;
using Roster.Data.Scope;
using Roster.Engine.Core;
using Roster.Web.Features.Admin.Shared;

namespace Roster.Web.Features.Admin.Uploads;

public sealed record UploadBatchRow(
    Guid Id, string Type, string Filename, DateTimeOffset UploadedAt, int TotalRows, int AcceptedRows, int RejectedRows,
    IReadOnlyList<UploadRowError> Errors);

/// <param name="ExpectedStores">Stores already trading (opening date on or before today): those expected in uploads.</param>
public sealed record UploadsView(IReadOnlyList<UploadBatchRow> Batches, int ExpectedStores);

/// <summary>What the upload API answered (the Uploader's UploadResult): a validation, a commit, or an error.</summary>
public sealed record UploadOutcome
{
    public int TotalRows { get; init; }
    public int ValidCount { get; init; }
    public IReadOnlyList<RowError> Errors { get; init; } = [];
    public IReadOnlyList<string> FileErrors { get; init; } = [];
    public bool Committed { get; init; }
    public int? AcceptedRows { get; init; }
    public int? DuplicatesCollapsed { get; init; }
    public int? CreatedRows { get; init; }
    public int? UpdatedRows { get; init; }
    public IReadOnlyList<PosDerivationReport>? PosReports { get; init; }
    public string? Error { get; init; }

    public static UploadOutcome Failed(string error) => new() { Error = error };

    public static UploadOutcome From(IngestValidation v) => v switch
    {
        IngestCommit c => new UploadOutcome
        {
            TotalRows = c.TotalRows,
            ValidCount = c.ValidCount,
            Errors = c.Errors,
            FileErrors = c.FileErrors,
            DuplicatesCollapsed = c.DuplicatesCollapsed,
            Committed = c.Committed,
            AcceptedRows = c.AcceptedRows,
            CreatedRows = c.CreatedRows,
            UpdatedRows = c.UpdatedRows,
            PosReports = c.PosReports,
        },
        _ => new UploadOutcome
        {
            TotalRows = v.TotalRows,
            ValidCount = v.ValidCount,
            Errors = v.Errors,
            FileErrors = v.FileErrors,
            DuplicatesCollapsed = v.DuplicatesCollapsed,
        },
    };
}

public interface IUploadsService
{
    /// <summary>The last 20 batches and the number of stores expected in uploads.</summary>
    Task<UploadsView> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /api/uploads/[type]: validate (mode "validate") or import (mode "commit") a
    /// file. Super User only. Leave Liability carries the pay period it belongs to.
    /// </summary>
    Task<UploadOutcome> SendAsync(
        string type, string mode, bool acceptPartial, string? periodEnd, string text, string filename, CancellationToken ct = default);
}

internal sealed class UploadsService(IServiceProvider services, TimeProvider clock) : IUploadsService
{
    // Resolved on first use, not when the page is built: a page opened with no database
    // reachable then shows its empty state (AdminLoad) instead of failing.
    private IRosterDbContextFactory contexts => services.GetRequiredService<IRosterDbContextFactory>();
    private IStoreScope scope => services.GetRequiredService<IStoreScope>();
    private IUploadIngest ingest => services.GetRequiredService<IUploadIngest>();

    public async Task<UploadsView> LoadAsync(CancellationToken ct = default)
    {
        scope.RequireSuperUser();
        var today = Dates.TodayAu(clock.GetUtcNow());
        await using var db = contexts.Create();
        var batches = await db.UploadBatches.AsNoTracking()
            .OrderByDescending(b => b.UploadedAt)
            .Take(20)
            .ToListAsync(ct);
        // Stores expected to appear in uploads = those already trading (opening date on
        // or before today). Stores opening later aren't expected yet.
        var expected = await db.StoreAttributes.AsNoTracking().CountAsync(a => a.OpeningDate != null && a.OpeningDate <= today, ct);
        return new UploadsView(
            batches.Select(b => new UploadBatchRow(b.Id, b.Type, b.Filename, b.UploadedAt, b.TotalRows, b.AcceptedRows, b.RejectedRows, b.ErrorReport)).ToList(),
            expected);
    }

    public async Task<UploadOutcome> SendAsync(
        string type, string mode, bool acceptPartial, string? periodEnd, string text, string filename, CancellationToken ct = default)
    {
        if (!scope.HasTier(Tier.SuperUser) || scope.User is not { } user) return UploadOutcome.Failed("Not authorised");
        if (!FeedTypes.Uploads.Contains(type)) return UploadOutcome.Failed($"Unknown upload type '{type}'");
        try
        {
            if (mode == "commit")
                return UploadOutcome.From(await ingest.CommitUploadAsync(
                    type, text, filename, user.Id, acceptPartial, new UploadOptions(string.IsNullOrEmpty(periodEnd) ? null : periodEnd), ct));
            return UploadOutcome.From(await ingest.ValidateUploadAsync(type, text, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return UploadOutcome.Failed(string.IsNullOrEmpty(e.Message) ? "Upload failed" : e.Message);
        }
    }
}

public sealed class UploadsModule : IFeatureModule
{
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IUploadsService, UploadsService>();
}
