// Port of src/app/(app)/plan/non-op/page.tsx (the reads) and actions.ts (the writes).

namespace Roster.Web.Features.NonOp;

/// <summary>A store in the Non-Op page's store picker (every store: the page is peer-visible).</summary>
public sealed record NonOpStore(Guid Id, string Code, string Name);

/// <summary>Everything the Non-Op page shows (page.tsx's props for NonOpClient).</summary>
/// <param name="ReadOnly">The viewer may not add or remove this store's entries.</param>
/// <param name="Peer">Another store's page (a peer), rather than a read-only login.</param>
public sealed record NonOpPageData(
    Guid? StoreId,
    string StoreName,
    IReadOnlyList<NonOpStore> Stores,
    DateOnly StartDate,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<NonOpCategoryRow> Categories,
    IReadOnlyList<NonOpSub> Subs,
    IReadOnlyList<NonOpEmployee> Employees,
    IReadOnlyList<NonOpEntryRow> Entries,
    bool ReadOnly,
    bool Peer);

/// <summary>addNonOpEntry's input.</summary>
public sealed record AddNonOpEntryInput(
    Guid StoreId,
    DateOnly Date,
    Guid? SubcategoryId,
    Guid? EmployeeId,
    int Minutes,
    string Description);

/// <summary>A Non-Op action's answer: ok, or the reason it was refused.</summary>
public sealed record NonOpResult(bool Ok, string? Error = null)
{
    public static readonly NonOpResult Success = new(true);
}

/// <summary>The Non-Op page's reads and its three actions, as the signed-in user.</summary>
public interface INonOpService
{
    /// <summary>
    /// The page: any store (Non-Op is peer-visible: every login may look at every store;
    /// only the store-write scope may add or remove), the fortnight from
    /// <paramref name="start"/> (else the store's first benchmark date, else today), the
    /// catalogue, the team and the entries.
    /// </summary>
    Task<NonOpPageData> LoadAsync(Guid? store, DateOnly? start, CancellationToken ct = default);

    /// <summary>addNonOpEntry: one entry (stores record their Non-Op hours one at a time).</summary>
    Task<NonOpResult> AddNonOpEntryAsync(AddNonOpEntryInput input, CancellationToken ct = default);

    /// <summary>deleteNonOpEntry, checked against the entry's own store.</summary>
    Task<NonOpResult> DeleteNonOpEntryAsync(Guid id, CancellationToken ct = default);

    /// <summary>clearNonOpEntriesForDate: every entry of the store on the date.</summary>
    Task<NonOpResult> ClearNonOpEntriesForDateAsync(Guid storeId, DateOnly date, CancellationToken ct = default);
}
