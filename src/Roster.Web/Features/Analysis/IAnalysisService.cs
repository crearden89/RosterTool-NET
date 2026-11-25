// Port of the reads behind src/app/(app)/analysis/page.tsx (with lib/analysis/compute.ts)
// and src/app/(app)/analysis/notes/page.tsx. The figures are worked out in
// Roster.Engine.Analysis and Roster.Engine.Walk.

using Roster.Engine.Analysis;
using Roster.Engine.Walk;

namespace Roster.Web.Features.Analysis;

/// <summary>A store in the Analysis pages' pickers (the stores the login can read).</summary>
public sealed record AnalysisStore(Guid Id, string Code, string Name);

/// <summary>"completed" (the default), "open" or "all".</summary>
public enum NoteStatus { Completed, Open, All }

/// <summary>What the Notes History page shows.</summary>
/// <param name="Stores">The store picker's stores (none for a store login).</param>
/// <param name="StartDate">The range as the filter bar shows it ("" when none).</param>
public sealed record NotesPage(
    IReadOnlyList<AnalysisStore> Stores,
    Guid? StoreId,
    bool ShowStoreSelect,
    string StartDate,
    string EndDate,
    NoteStatus Status,
    DateOnly Today,
    IReadOnlyList<WalkNote> Notes,
    NotesSummary Summary);

/// <summary>What the Analysis page shows.</summary>
/// <param name="Result">The range's figures; null without a store.</param>
public sealed record AnalysisPage(
    IReadOnlyList<AnalysisStore> Stores,
    Guid? StoreId,
    bool ShowStoreSelect,
    string StartDate,
    string EndDate,
    bool FullyLoaded,
    AnalysisResult? Result);

public interface IAnalysisService
{
    /// <summary>
    /// The Analysis page: the store, the range (from the store's first benchmark day when
    /// none is given) and computeAnalysis over it. Null when nobody is signed in.
    /// </summary>
    Task<AnalysisPage?> LoadAnalysisAsync(string? store, string? start, string? end, bool fullyLoaded, CancellationToken ct = default);

    /// <summary>Notes History: store walk notes raised in the range, filtered by status. Null when nobody is signed in.</summary>
    Task<NotesPage?> LoadNotesAsync(string? store, string? start, string? end, string? status, CancellationToken ct = default);
}
