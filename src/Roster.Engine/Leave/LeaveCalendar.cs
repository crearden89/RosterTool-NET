// Port of src/lib/leave/calendar.ts.
// ─── Leave calendar grid (pure) ──────────────────────────────────────────────
// The workbook's '2026' sheet: one column per date, one row per employee, a
// code per cell. Ported with the type matching FIXED - the workbook's COUNTIFS
// looked for "REQ - Unpaid" while UKG exports "REQ - Unpaid Leave", so 285 of
// 1,216 requests rendered as nothing at all.
//
// Confirmed decisions baked in here:
//   * Carers Leave folds into SIC.
//   * Cancelled / Cancel Submitted / Cancel Approved / Refused all render
//     blank - only live bookings occupy a day.
//   * Precedence: Approved AL → Submitted AL → Sick/Carers → Unpaid.

using Roster.Engine.Core;

namespace Roster.Engine.Leave;

/// <summary>The UKG leave-type buckets <see cref="LeaveCalendar.BucketFor"/> sorts a type into.</summary>
public static class LeaveBucket
{
    public const string Annual = "annual";
    public const string Sick = "sick";
    public const string Unpaid = "unpaid";
}

/// <param name="Codes">One <see cref="LeaveCode"/> per date, parallel to the caller's date list.</param>
public sealed record EmployeeGrid(string UkgEmployeeId, IReadOnlyList<string> Codes);

/// <param name="OnLeaveByDate">Headcount on APPROVED annual leave, per date.</param>
/// <param name="UnavailableDates">Dates at or over the store's cap - the workbook's "unavailable dates".</param>
public sealed record CalendarGrid(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<EmployeeGrid> Rows,
    IReadOnlyList<int> OnLeaveByDate,
    IReadOnlyList<DateOnly> UnavailableDates);

public static class LeaveCalendar
{
    /// <summary>Statuses that mean the booking is live. Everything else renders blank.</summary>
    private const string LiveApproved = "Approved";
    private const string LiveSubmitted = "Submitted";

    /// <summary>
    /// UKG leave-type buckets: <see cref="LeaveBucket"/>'s names, or null. Matching
    /// is prefix-based so " Leave" suffixes and future wording tweaks can't silently
    /// blank out a whole category again.
    /// </summary>
    public static string? BucketFor(string leaveType)
    {
        var t = leaveType.ToLowerInvariant();
        if (t.Contains("annual", StringComparison.Ordinal)) return LeaveBucket.Annual;
        // Carers folds into sick, as confirmed.
        if (t.Contains("sick", StringComparison.Ordinal) || t.Contains("carer", StringComparison.Ordinal)) return LeaveBucket.Sick;
        if (t.Contains("unpaid", StringComparison.Ordinal)) return LeaveBucket.Unpaid;
        return null;
    }

    /// <summary>Every ISO date from <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
    public static List<DateOnly> DateRange(DateOnly from, DateOnly to)
    {
        var s = from.DayNumber;
        var e = to.DayNumber;
        if (e < s) return [];
        var dates = new List<DateOnly>(e - s + 1);
        for (var d = s; d <= e; d++) dates.Add(DateOnly.FromDayNumber(d));
        return dates;
    }

    /// <summary>
    /// The code for one employee on one date. Requests are pre-filtered to that
    /// employee by the caller.
    /// </summary>
    public static string CodeForDate(IEnumerable<LeaveRequestRow> requests, DateOnly date)
    {
        var submitted = false;
        var sick = false;
        var unpaid = false;
        foreach (var r in requests)
        {
            if (date < r.StartDate || date > r.EndDate) continue;
            var bucket = BucketFor(r.LeaveType);
            if (bucket == null) continue;
            if (bucket == LeaveBucket.Annual)
            {
                if (r.Status == LiveApproved) return LeaveCode.AL; // highest precedence
                if (r.Status == LiveSubmitted) submitted = true;
                continue;
            }
            // Sick and unpaid are shown whatever their status - they are records of
            // absence, not requests competing for approval.
            if (bucket == LeaveBucket.Sick) sick = true;
            else if (bucket == LeaveBucket.Unpaid) unpaid = true;
        }
        if (submitted) return LeaveCode.S;
        if (sick) return LeaveCode.SIC;
        if (unpaid) return LeaveCode.UP;
        return LeaveCode.None;
    }

    public static CalendarGrid BuildCalendar(
        IReadOnlyList<string> employeeIds,
        IEnumerable<LeaveRequestRow> requests,
        DateOnly from,
        DateOnly to,
        double maxStaffOnLeave)
    {
        var dates = DateRange(from, to);
        var byEmp = new JsMap<string, List<LeaveRequestRow>>();
        foreach (var r in requests)
        {
            var arr = byEmp.GetOr(r.UkgEmployeeId, []);
            arr.Add(r);
            byEmp.Set(r.UkgEmployeeId, arr);
        }

        var onLeaveByDate = new int[dates.Count];
        var rows = employeeIds.Select(id =>
        {
            var mine = byEmp.GetOr(id, []);
            var codes = new List<string>(dates.Count);
            for (var i = 0; i < dates.Count; i++)
            {
                var c = CodeForDate(mine, dates[i]);
                if (c == LeaveCode.AL) onLeaveByDate[i] += 1;
                codes.Add(c);
            }
            return new EmployeeGrid(id, codes);
        }).ToList();

        var unavailableDates =
            maxStaffOnLeave > 0 ? dates.Where((_, i) => onLeaveByDate[i] >= maxStaffOnLeave).ToList() : [];

        return new CalendarGrid(dates, rows, onLeaveByDate, unavailableDates);
    }

    /// <summary>
    /// Approved annual-leave DAYS booked from <paramref name="today"/> forward. Only
    /// future leave reduces the projected balance: leave already taken has been
    /// deducted by UKG and counting it again would double-charge the employee.
    /// </summary>
    public static int FutureApprovedDays(IReadOnlyList<LeaveRequestRow> requests, DateOnly today, DateOnly horizon)
    {
        var n = 0;
        foreach (var d in DateRange(today, horizon))
        {
            if (CodeForDate(requests, d) == LeaveCode.AL) n += 1;
        }
        return n;
    }
}
