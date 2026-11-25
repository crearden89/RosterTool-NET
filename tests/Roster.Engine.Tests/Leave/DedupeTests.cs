// Port of src/lib/leave/dedupe.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Engine.Tests.Leave;

public class DedupeTests
{
    private sealed record Req(
        string? StoreCode,
        string UkgEmployeeId,
        string LeaveType,
        DateOnly StartDate,
        DateOnly EndDate,
        string Status,
        string? Approver = null) : IDedupableRequest;

    private static Req R(
        string ukgEmployeeId,
        string startDate,
        string endDate,
        string status,
        string leaveType = "REQ - Annual Leave",
        string storeCode = "G999") =>
        new(storeCode, ukgEmployeeId, leaveType, DateOnlyExtensions.ParseIso(startDate), DateOnlyExtensions.ParseIso(endDate), status);

    // ── status precedence ────────────────────────────────────────────────────

    [Fact]
    public void Ranks_a_live_booking_above_a_cancelled_or_refused_one()
    {
        Assert.True(LeaveDedupe.StatusRank("Approved") < LeaveDedupe.StatusRank("Submitted"));
        Assert.True(LeaveDedupe.StatusRank("Submitted") < LeaveDedupe.StatusRank("Cancelled"));
        foreach (var s in new[] { "Cancelled", "Cancel Submitted", "Cancel Approved", "Refused" })
            Assert.Equal(2, LeaveDedupe.StatusRank(s));
    }

    [Fact]
    public void Is_case_and_whitespace_tolerant_UKG_wording_drifts()
    {
        Assert.Equal(0, LeaveDedupe.StatusRank(" approved "));
        Assert.Equal(0, LeaveDedupe.StatusRank("APPROVED"));
    }

    [Fact]
    public void Treats_an_unrecognised_status_as_non_live_rather_than_guessing()
    {
        Assert.Equal(2, LeaveDedupe.StatusRank("Pending Manager Review"));
    }

    // ── dedupe ───────────────────────────────────────────────────────────────

    [Fact]
    public void Leaves_a_clean_file_untouched()
    {
        var rows = new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
            R("1002", "2026-09-01", "2026-09-05", "Approved"),
        };
        var out_ = LeaveDedupe.DedupeLeaveRequests(rows);
        Assert.Equal(2, out_.Rows.Count);
        Assert.Empty(out_.Dropped);
    }

    [Fact]
    public void Collapses_the_exact_same_booking_listed_twice()
    {
        // The plain case: the export just repeats a row. Without this the whole
        // upload dies on "cannot affect row a second time".
        var rows = new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
        };
        var out_ = LeaveDedupe.DedupeLeaveRequests(rows);
        Assert.Single(out_.Rows);
        Assert.Single(out_.Dropped);
    }

    [Fact]
    public void Keeps_the_live_status_when_one_booking_carries_two()
    {
        var submittedFirst = LeaveDedupe.DedupeLeaveRequests(new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Submitted"),
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
        });
        Assert.Equal("Approved", submittedFirst.Rows[0].Status);

        // Same answer whichever order the two arrive in.
        var approvedFirst = LeaveDedupe.DedupeLeaveRequests(new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
            R("1001", "2026-09-01", "2026-09-05", "Submitted"),
        });
        Assert.Equal("Approved", approvedFirst.Rows[0].Status);
    }

    [Fact]
    public void Keeps_approved_leave_over_a_cancellation_of_the_same_dates()
    {
        // Hiding leave somebody is genuinely taking is the error that leaves a
        // store short-handed, so the live row wins.
        var out_ = LeaveDedupe.DedupeLeaveRequests(new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Cancelled"),
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
        });
        Assert.Single(out_.Rows);
        Assert.Equal("Approved", out_.Rows[0].Status);
    }

    [Fact]
    public void Keeps_the_later_row_when_two_share_a_status()
    {
        var out_ = LeaveDedupe.DedupeLeaveRequests(new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Approved") with { Approver = "A" },
            R("1001", "2026-09-01", "2026-09-05", "Approved") with { Approver = "B" },
        });
        Assert.Single(out_.Rows);
        Assert.Equal("B", out_.Rows[0].Approver);
    }

    [Fact]
    public void Does_not_merge_bookings_that_differ_on_any_part_of_the_key()
    {
        var rows = new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Approved"),
            R("1001", "2026-09-01", "2026-09-06", "Approved"), // different end
            R("1001", "2026-09-02", "2026-09-05", "Approved"), // different start
            R("1001", "2026-09-01", "2026-09-05", "Approved", "REQ - Sick Leave"), // type
            R("1002", "2026-09-01", "2026-09-05", "Approved"), // employee
            R("1001", "2026-09-01", "2026-09-05", "Approved", "REQ - Annual Leave", "G998"), // store
        };
        Assert.Equal(6, LeaveDedupe.DedupeLeaveRequests(rows).Rows.Count);
    }

    [Fact]
    public void Keeps_the_surviving_rows_in_first_appearance_order()
    {
        var out_ = LeaveDedupe.DedupeLeaveRequests(new[]
        {
            R("1001", "2026-09-01", "2026-09-05", "Submitted"),
            R("1002", "2026-10-01", "2026-10-03", "Approved"),
            R("1001", "2026-09-01", "2026-09-05", "Approved"), // upgrades row 1 in place
        });
        Assert.Equal(["1001", "1002"], out_.Rows.Select(r => r.UkgEmployeeId).ToArray());
    }

    [Fact]
    public void Builds_a_key_that_separates_every_field()
    {
        Assert.Contains("1001", LeaveDedupe.RequestKey(R("1001", "2026-09-01", "2026-09-05", "Approved")));
        Assert.DoesNotContain("Approved", LeaveDedupe.RequestKey(R("1001", "2026-09-01", "2026-09-05", "Approved")));
    }

    // ── beyond the TypeScript's cases: the key's exact shape ────────────────

    [Fact]
    public void Key_is_the_typescript_join_with_iso_dates_and_a_missing_store_as_empty()
    {
        Assert.Equal(
            "G999 1001 REQ - Annual Leave 2026-09-01 2026-09-05",
            LeaveDedupe.RequestKey(R("1001", "2026-09-01", "2026-09-05", "Approved")));
        var noStore = R("1001", "2026-09-01", "2026-09-05", "Approved") with { StoreCode = null };
        Assert.Equal(" 1001 REQ - Annual Leave 2026-09-01 2026-09-05", LeaveDedupe.RequestKey(noStore));
    }

    [Fact]
    public void Drops_rows_in_file_order()
    {
        var a = R("1001", "2026-09-01", "2026-09-05", "Approved") with { Approver = "A" };
        var b = R("1001", "2026-09-01", "2026-09-05", "Cancelled") with { Approver = "B" };
        var c = R("1001", "2026-09-01", "2026-09-05", "Approved") with { Approver = "C" };
        var out_ = LeaveDedupe.DedupeLeaveRequests(new[] { a, b, c });
        // b loses to a; then c ties a and replaces it.
        Assert.Equal([b, a], out_.Dropped.ToArray());
        Assert.Equal([c], out_.Rows.ToArray());
    }
}
