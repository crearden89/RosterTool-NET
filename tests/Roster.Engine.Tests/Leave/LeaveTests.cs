// Port of src/lib/leave/leave.test.ts.

using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Engine.Tests.Leave;

public class LeaveTests
{
    private static readonly AccrualBand[] Bands =
    [
        new(90, "SM", 9, 5, 225),
        new(70, "DSM", 7.5, 5, 187.5),
        new(70, "SA", 7.25, 4, 145),
        new(30, "SA", 4.75, 4, 60),
    ];

    private static LiabilityRow Emp() => new()
    {
        UkgEmployeeId = "1",
        EmployeeName = "1-Test, A",
        Job = "ST MANAGER",
        RoleClass = "SM",
        ContractHours = 90,
        NotionalHours = 9,
        AnnualHours = 100,
        SickHours = 0,
        LieuHours = 0,
        BankedHours = 0,
        Banked2Hours = 0,
    };

    private static DateOnly D(string iso) => DateOnlyExtensions.ParseIso(iso);

    /// <summary>vitest's toBeCloseTo: |expected − actual| &lt; 10^−digits / 2.</summary>
    private static void CloseTo(double expected, double actual, int digits = 2) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"expected {actual} to be close to {expected} ({digits} digits)");

    // ── role classification ─────────────────────────────────────────────────

    [Fact]
    public void Reads_UKG_job_titles()
    {
        Assert.Equal("SM", LeaveMetrics.RoleClassFromJob("ST MANAGER"));
        Assert.Equal("SM", LeaveMetrics.RoleClassFromJob("ST MANAGER EXTENDED LEAVE"));
        Assert.Equal("ASM", LeaveMetrics.RoleClassFromJob("ST ASSIST MANAGER"));
        Assert.Equal("DSM", LeaveMetrics.RoleClassFromJob("ST DUTY STORE MANAGER"));
        Assert.Equal("SA", LeaveMetrics.RoleClassFromJob("ST ASSIST"));
        Assert.Equal("CASUAL", LeaveMetrics.RoleClassFromJob("ST ASSIST JNR CASUAL"));
    }

    [Fact]
    public void Falls_back_to_the_names_rank_prefix()
    {
        Assert.Equal("SM", LeaveMetrics.RoleClassFromName("1-Archer, Val"));
        Assert.Equal("ASM", LeaveMetrics.RoleClassFromName("2-Sato, Mina"));
        Assert.Equal("DSM", LeaveMetrics.RoleClassFromName("3-Someone, A"));
        Assert.Equal("SA", LeaveMetrics.RoleClassFromName("4L-Someone, B"));
        Assert.Equal("SA", LeaveMetrics.RoleClassFromName("5-Someone, C"));
        Assert.Equal("CASUAL", LeaveMetrics.RoleClassFromName("6C-Someone, D"));
    }

    // ── accrual bands ───────────────────────────────────────────────────────

    [Fact]
    public void Splits_the_two_70_hour_bands_by_role_the_workbook_could_only_reach_one()
    {
        Assert.Equal(7.5, LeaveAccrual.BandFor(Bands, 70, "DSM")!.LeaveNotionalHours);
        Assert.Equal(7.25, LeaveAccrual.BandFor(Bands, 70, "SA")!.LeaveNotionalHours);
        // And they carry different entitlements: 5 weeks vs 4.
        Assert.Equal(187.5, LeaveAccrual.BandFor(Bands, 70, "DSM")!.AnnualAccrualHours);
        Assert.Equal(145, LeaveAccrual.BandFor(Bands, 70, "SA")!.AnnualAccrualHours);
    }

    [Fact]
    public void Falls_back_DOWN_to_the_nearest_smaller_contract_never_up()
    {
        Assert.Equal(30, LeaveAccrual.BandFor(Bands, 50, "SA")!.ContractHours);
        Assert.Null(LeaveAccrual.BandFor(Bands, 20, "SA")); // nothing smaller - flagged
    }

    [Fact]
    public void Has_no_band_for_a_role_class_it_was_never_given()
    {
        Assert.Null(LeaveAccrual.BandFor(Bands, 90, "ASM"));
    }

    [Fact]
    public void Spreads_the_annual_entitlement_over_365_days()
    {
        CloseTo(0.6164, LeaveAccrual.DailyAccrualHours(Bands[0]), 4);
    }

    // ── projection ──────────────────────────────────────────────────────────

    [Fact]
    public void Projects_from_TODAY_not_the_start_of_the_month()
    {
        // 8 Aug → 31 Dec inclusive = 146 days × 0.6164 = 90.00h
        Assert.Equal(146, LeaveAccrual.DaysInclusive(D("2026-08-08"), D("2026-12-31")));
        var p = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 135.67 }, Bands, 6, D("2026-08-08"), 76);
        CloseTo(54, p.PlannedHours, 2); // 6 days × 9.00
        CloseTo(90.0, p.EoyAccrualHours, 1);
        CloseTo(171.67, p.EoyBalanceHours, 1);
        CloseTo(3.81, p.EoyBalanceWeeks, 2);
    }

    [Fact]
    public void Days_to_take_closes_the_gap_to_the_target_exactly()
    {
        var p = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 135.67 }, Bands, 6, D("2026-08-08"), 76);
        // Taking that many more days lands the balance on the target.
        var landed = p.EoyBalanceHours - p.DaysToTake * p.LeaveNotional;
        CloseTo(76, landed, 6);
    }

    [Fact]
    public void Is_zero_days_to_take_when_already_under_target()
    {
        var p = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 10 }, Bands, 0, D("2026-12-30"), 76);
        Assert.Equal(0, p.DaysToTake);
    }

    [Fact]
    public void Flags_an_unbanded_employee_instead_of_silently_zeroing_them()
    {
        var p = LeaveAccrual.ProjectEmployee(Emp() with { ContractHours = 20, RoleClass = "SA" }, Bands, 0, D("2026-08-08"), 76);
        Assert.True(p.Unbanded);
        Assert.Equal(0, p.EoyAccrualHours);
    }

    // ── calendar codes ──────────────────────────────────────────────────────

    private static LeaveRequestRow Req(
        string ukgEmployeeId = "1",
        string leaveType = "REQ - Annual Leave",
        string startDate = "2026-08-10",
        string endDate = "2026-08-14",
        string status = "Approved") =>
        new(ukgEmployeeId, "1-Test, A", leaveType, D(startDate), D(endDate), status);

    [Fact]
    public void Matches_UKGs_real_type_strings_the_workbook_missed_every_unpaid_row()
    {
        // The workbook's COUNTIFS sought "REQ - Unpaid"; UKG exports
        // "REQ - Unpaid Leave", so 285 of 1,216 requests rendered as blank.
        Assert.Equal("unpaid", LeaveCalendar.BucketFor("REQ - Unpaid Leave"));
        Assert.Equal("annual", LeaveCalendar.BucketFor("REQ - Annual Leave"));
        Assert.Equal("sick", LeaveCalendar.BucketFor("REQ - Sick Leave"));
    }

    [Fact]
    public void Folds_carers_leave_into_sick()
    {
        Assert.Equal("sick", LeaveCalendar.BucketFor("REQ - Carers Leave"));
        Assert.Equal("SIC", LeaveCalendar.CodeForDate([Req(leaveType: "REQ - Carers Leave")], D("2026-08-12")));
    }

    [Fact]
    public void Applies_approved_over_submitted()
    {
        var rows = new[] { Req(status: "Submitted"), Req(status: "Approved") };
        Assert.Equal("AL", LeaveCalendar.CodeForDate(rows, D("2026-08-12")));
        Assert.Equal("S", LeaveCalendar.CodeForDate([Req(status: "Submitted")], D("2026-08-12")));
    }

    [Fact]
    public void Renders_every_cancelled_or_refused_status_blank()
    {
        foreach (var status in new[] { "Cancelled", "Cancel Submitted", "Cancel Approved", "Refused" })
            Assert.Equal("", LeaveCalendar.CodeForDate([Req(status: status)], D("2026-08-12")));
    }

    [Fact]
    public void Is_blank_outside_the_requests_date_span()
    {
        Assert.Equal("", LeaveCalendar.CodeForDate([Req()], D("2026-08-09")));
        Assert.Equal("", LeaveCalendar.CodeForDate([Req()], D("2026-08-15")));
        Assert.Equal("AL", LeaveCalendar.CodeForDate([Req()], D("2026-08-10"))); // inclusive start
        Assert.Equal("AL", LeaveCalendar.CodeForDate([Req()], D("2026-08-14"))); // inclusive end
    }

    [Fact]
    public void Counts_heads_on_leave_and_marks_dates_at_the_cap()
    {
        var rows = new[]
        {
            Req(ukgEmployeeId: "a", startDate: "2026-08-10", endDate: "2026-08-11"),
            Req(ukgEmployeeId: "b", startDate: "2026-08-11", endDate: "2026-08-12"),
        };
        var g = LeaveCalendar.BuildCalendar(["a", "b"], rows, D("2026-08-10"), D("2026-08-12"), 2);
        Assert.Equal([1, 2, 1], g.OnLeaveByDate.ToArray());
        Assert.Equal([D("2026-08-11")], g.UnavailableDates.ToArray());
    }

    [Fact]
    public void Counts_only_FUTURE_approved_days_past_leave_is_already_deducted()
    {
        var rows = new[]
        {
            Req(startDate: "2026-01-05", endDate: "2026-01-09"), // taken, ignored
            Req(startDate: "2026-09-01", endDate: "2026-09-03"), // 3 future days
        };
        Assert.Equal(3, LeaveCalendar.FutureApprovedDays(rows, D("2026-08-08"), D("2026-12-31")));
    }

    // ── lieu in the liability ───────────────────────────────────────────────

    [Fact]
    public void Adds_lieu_to_the_employees_current_liability_signed()
    {
        var owed = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 100, LieuHours = 20 }, Bands, 0, D("2026-12-31"), 76);
        var owes = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 100, LieuHours = -20 }, Bands, 0, D("2026-12-31"), 76);
        Assert.Equal(120, owed.CurrentLiabilityHours);
        Assert.Equal(80, owes.CurrentLiabilityHours);
        // And it flows straight through to the projection.
        CloseTo(40, owed.EoyBalanceHours - owes.EoyBalanceHours, 6);
    }

    [Fact]
    public void Carries_lieu_forward_flat_it_is_worked_not_accrued()
    {
        var a = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 100, LieuHours = 0 }, Bands, 0, D("2026-08-08"), 76);
        var b = LeaveAccrual.ProjectEmployee(Emp() with { AnnualHours = 100, LieuHours = 30 }, Bands, 0, D("2026-08-08"), 76);
        // Same accrual either way: lieu does not earn more lieu over time.
        CloseTo(b.EoyAccrualHours, a.EoyAccrualHours, 6);
        CloseTo(30, b.EoyBalanceHours - a.EoyBalanceHours, 6);
    }

    [Fact]
    public void Counts_lieu_in_the_store_total_netting_the_two_directions()
    {
        LiabilityRow[] rows =
        [
            Emp() with { UkgEmployeeId = "a", ContractHours = 76, AnnualHours = 100, LieuHours = 40 },
            Emp() with { UkgEmployeeId = "b", ContractHours = 76, AnnualHours = 100, LieuHours = -10 },
        ];
        var s = LeaveMetrics.StoreLiability(rows, 76, 76);
        Assert.Equal(200, s.TotalAnnualHours);
        Assert.Equal(30, s.TotalLieuHours);
        Assert.Equal(230, s.TotalLiabilityHours);
        Assert.Equal(115, s.HoursPerFte); // 230 / 2 FTE, not 100
    }

    [Fact]
    public void Excludes_sick_and_banked_entirely_they_are_not_leave_liability()
    {
        var plain = LeaveMetrics.StoreLiability([Emp() with { ContractHours = 76, AnnualHours = 100 }], 76, 76);
        var noisy = LeaveMetrics.StoreLiability(
            [Emp() with { ContractHours = 76, AnnualHours = 100, SickHours = 500, BankedHours = 90, Banked2Hours = 40 }],
            76, 76);
        Assert.Equal(plain.TotalLiabilityHours, noisy.TotalLiabilityHours);
        Assert.Equal(plain.HoursPerFte, noisy.HoursPerFte);
    }

    // ── store liability ─────────────────────────────────────────────────────

    [Fact]
    public void Reproduces_the_live_figures_for_a_23_person_store()
    {
        // 23 staff on the file, 1,361.48 AL hours over 12.3816 FTE. Stated with
        // zero lieu so the annual-only arithmetic stays checkable.
        LiabilityRow[] rows = [Emp() with { UkgEmployeeId = "a", ContractHours = 940.0, AnnualHours = 1361.48, LieuHours = 0 }];
        var s = LeaveMetrics.StoreLiability(rows, 76, 76);
        CloseTo(12.3684, s.Fte, 3);
        CloseTo(110.07, s.HoursPerFte!.Value, 1);
        Assert.True(s.VsTarget!.Value > 0); // over the 76h target
    }

    [Fact]
    public void Excludes_casuals_from_both_sides_of_the_ratio()
    {
        LiabilityRow[] rows =
        [
            Emp() with { UkgEmployeeId = "a", ContractHours = 76, AnnualHours = 76 },
            Emp() with { UkgEmployeeId = "b", RoleClass = "CASUAL", ContractHours = 0, AnnualHours = 0, Job = "ST ASSIST JNR CASUAL" },
        ];
        var s = LeaveMetrics.StoreLiability(rows, 76, 76);
        Assert.Equal(1, s.Headcount);
        Assert.Equal(1, s.CasualCount);
        Assert.Equal(1, s.Fte);
        Assert.Equal(76, s.HoursPerFte);
    }

    [Fact]
    public void Is_null_rather_than_infinite_when_a_store_has_no_FTE()
    {
        var s = LeaveMetrics.StoreLiability([Emp() with { RoleClass = "CASUAL", ContractHours = 0 }], 76, 76);
        Assert.Null(s.HoursPerFte);
        Assert.Null(s.VsTarget);
    }

    // ── period movement ─────────────────────────────────────────────────────

    [Fact]
    public void Is_a_signed_fraction_against_the_previous_period()
    {
        CloseTo(0.1, LeaveMetrics.LiabilityChange(110, 100)!.Value);
        CloseTo(-0.1, LeaveMetrics.LiabilityChange(90, 100)!.Value);
    }

    [Fact]
    public void Is_null_with_no_comparable_prior_period()
    {
        Assert.Null(LeaveMetrics.LiabilityChange(110, null));
        Assert.Null(LeaveMetrics.LiabilityChange(110, 0));
    }

    // ── top N ───────────────────────────────────────────────────────────────

    private sealed record Named(string EmployeeName, double V) : IEmployeeNamed;

    [Fact]
    public void Ranks_descending_and_breaks_ties_by_name()
    {
        Named[] rows = [new("B", 5), new("A", 5), new("C", 9)];
        Assert.Equal(["C", "A"], LeaveMetrics.TopBy(rows, r => r.V, 2).Select(r => r.EmployeeName).ToArray());
    }
}
