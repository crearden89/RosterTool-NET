// Port of src/lib/leave/dedupe.ts.
// ─── Collapsing duplicate leave requests (pure) ──────────────────────────────
// leave_requests_ukg is unique on (store, employee, leave type, start, end) -
// deliberately NOT including status, so one booking is one row however many
// times it is touched upstream.
//
// A UKG Leave Request dataview can carry the SAME booking twice with different
// statuses: submitted and then approved, or approved and then cancelled. Handed
// straight to the database those two rows collide inside a single upsert (in
// Postgres it refused with "ON CONFLICT DO UPDATE command cannot affect row a
// second time"; a SQL Server MERGE refuses the same way) - one statement may not
// update the same row twice. The whole file is rejected and nothing loads.
//
// So the batch is collapsed to one row per booking BEFORE it is written.

using Roster.Engine.Core;

namespace Roster.Engine.Leave;

/// <summary>Only the fields the unique key and the precedence rule need.</summary>
public interface IDedupableRequest
{
    /// <summary>Optional in the TypeScript (<c>storeCode?</c>); null reads as "".</summary>
    string? StoreCode { get; }
    string UkgEmployeeId { get; }
    string LeaveType { get; }
    DateOnly StartDate { get; }
    DateOnly EndDate { get; }
    string Status { get; }
}

/// <param name="Rows">One row per booking, in first-appearance order.</param>
/// <param name="Dropped">Rows dropped as duplicates of a kept row, in file order.</param>
public sealed record DedupeResult<T>(IReadOnlyList<T> Rows, IReadOnlyList<T> Dropped);

public static class LeaveDedupe
{
    /// <summary>
    /// The booking's unique key: store, employee, type, start, end - joined by a
    /// space, the dates in ISO form, exactly as the TypeScript builds it.
    /// </summary>
    public static string RequestKey(IDedupableRequest r) =>
        string.Join(" ", r.StoreCode ?? "", r.UkgEmployeeId, r.LeaveType, r.StartDate.ToIso(), r.EndDate.ToIso());

    /// <summary>
    /// Which status wins when one booking appears more than once. LOWER wins.
    ///
    /// A LIVE status beats a cancelled or refused one. The export carries no
    /// sequence or timestamp, so there is no way to tell "approved then cancelled"
    /// from "cancelled then re-booked and approved" - and of the two ways to be
    /// wrong, hiding leave somebody is genuinely taking is the one that puts a
    /// manager on the floor short-handed. Approved beats Submitted as the more
    /// settled of the two.
    /// </summary>
    public static int StatusRank(string status)
    {
        var s = status.Trim().ToLowerInvariant();
        if (s == "approved") return 0;
        if (s == "submitted") return 1;
        // Cancelled / Cancel Submitted / Cancel Approved / Refused, and anything new
        // UKG starts emitting: all render blank on the calendar anyway.
        return 2;
    }

    /// <summary>
    /// One row per booking, keeping the highest-ranking status. Ties keep the LAST
    /// occurrence: file order runs oldest to newest in every export seen so far, so
    /// the later row is the closer of the two to the booking's current state.
    ///
    /// Order of the kept rows follows first appearance, so an error report's line
    /// numbers still read in the order someone scrolling the file would expect.
    /// </summary>
    public static DedupeResult<T> DedupeLeaveRequests<T>(IEnumerable<T> rows) where T : IDedupableRequest
    {
        var best = new JsMap<string, (T Row, int At)>();
        var dropped = new List<T>();

        var i = 0;
        foreach (var row in rows)
        {
            var key = RequestKey(row);
            if (!best.TryGet(key, out var held))
            {
                best.Set(key, (row, i));
                i++;
                continue;
            }
            // <= keeps the later row on a tie, per the doc comment above.
            if (StatusRank(row.Status) <= StatusRank(held.Row.Status))
            {
                dropped.Add(held.Row);
                best.Set(key, (row, held.At));
            }
            else
            {
                dropped.Add(row);
            }
            i++;
        }

        // [...best.values()].sort((a, b) => a.at - b.at): the positions are distinct,
        // so any stable sort gives the same order.
        var kept = best.Values().OrderBy(v => v.At).Select(v => v.Row).ToList();
        return new DedupeResult<T>(kept, dropped);
    }
}
