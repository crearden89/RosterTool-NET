// New: compute.ts has no TypeScript tests; these pin the assembly the .NET edition
// splits out of it (the loading is in the page service).

using Roster.Engine.Core;
using Roster.Engine.Leave;

namespace Roster.Engine.Tests.Leave;

public class LeaveComputeTests
{
    private static DateOnly D(string iso) => DateOnlyExtensions.ParseIso(iso);

    private static readonly LeaveConfig Config = new(
        [
            new AccrualBand(90, "SM", 9, 5, 225),
            new AccrualBand(70, "SA", 7.25, 4, 145),
        ],
        76,
        76);

    private static LiabilityRow Row(string id, string role, double contract, double annual, double lieu = 0) => new()
    {
        UkgEmployeeId = id,
        EmployeeName = $"4-Person, {id}",
        Job = null,
        RoleClass = role,
        ContractHours = contract,
        NotionalHours = 7,
        AnnualHours = annual,
        LieuHours = lieu,
    };

    [Fact]
    public void Picks_the_asked_period_when_held_else_the_newest_and_the_one_before_it()
    {
        DateOnly[] periods = [D("2026-08-08"), D("2026-07-25"), D("2026-07-12")];
        Assert.Equal((D("2026-08-08"), D("2026-07-25")), LeaveCompute.PickPeriods(periods, null));
        Assert.Equal((D("2026-07-25"), D("2026-07-12")), LeaveCompute.PickPeriods(periods, D("2026-07-25")));
        Assert.Equal((D("2026-07-12"), (DateOnly?)null), LeaveCompute.PickPeriods(periods, D("2026-07-12")));
        // A period that is not held falls back to the newest.
        Assert.Equal((D("2026-08-08"), D("2026-07-25")), LeaveCompute.PickPeriods(periods, D("2026-01-01")));
        Assert.Equal(((DateOnly?)null, (DateOnly?)null), LeaveCompute.PickPeriods([], D("2026-01-01")));
    }

    [Fact]
    public void Store_view_projects_the_accruing_staff_and_counts_casuals_only_in_the_total()
    {
        LiabilityRow[] rows =
        [
            Row("1", "SM", 90, 135.67, -12.75),
            Row("2", "SA", 70, 50),
            Row("3", "CASUAL", 0, 0),
            Row("4", "SA", 20, 10), // no band for a 20h SA
            Row("5", "ASM", 37.5, 10), // no ASM bands at all
        ];
        LeaveRequestRow[] requests =
        [
            new("1", "1-Person, 1", "REQ - Annual Leave", D("2026-09-01"), D("2026-09-06"), "Approved"),
            new("2", "4-Person, 2", "REQ - Annual Leave", D("2026-01-05"), D("2026-01-09"), "Approved"),
        ];
        var view = LeaveCompute.BuildStoreView(Config, D("2026-08-08"), D("2026-08-08"), null, rows, null, requests, 3, []);

        Assert.Equal(["1", "2", "4", "5"], view.Members.Select(m => m.UkgEmployeeId).ToArray());
        Assert.Equal(6, view.Members[0].PlannedDays); // 1-6 Sep, future
        Assert.Equal(0, view.Members[1].PlannedDays); // January is already taken
        Assert.Equal(["20h SA", "37.5h ASM"], view.UnbandedCombos.ToArray());
        Assert.Equal(4, view.Liability.Headcount);
        Assert.Equal(1, view.Liability.CasualCount);
        Assert.Null(view.PreviousLiability);
        Assert.Null(view.ChangePct);
        // The calendar covers the whole year, one row per accruing member.
        Assert.Equal(365, view.Calendar.Dates.Count);
        Assert.Equal(D("2026-01-01"), view.Calendar.Dates[0]);
        Assert.Equal(4, view.Calendar.Rows.Count);
        Assert.Equal("1", view.TopCurrent[0].UkgEmployeeId);
    }

    [Fact]
    public void Network_rows_run_highest_per_FTE_first_with_no_FTE_last_and_ties_in_code_order()
    {
        var a = new LeaveStoreRef(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "G001", "Alpha");
        var b = new LeaveStoreRef(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "G002", "Bravo");
        var c = new LeaveStoreRef(Guid.Parse("00000000-0000-0000-0000-00000000000c"), "G003", "Charlie");
        var d = new LeaveStoreRef(Guid.Parse("00000000-0000-0000-0000-00000000000d"), "G004", "Delta");
        (Guid, LiabilityRow)[] snaps =
        [
            (a.Id, Row("1", "SA", 76, 80)),
            (b.Id, Row("2", "CASUAL", 0, 0)),
            (c.Id, Row("3", "SA", 76, 120)),
            (d.Id, Row("4", "SA", 76, 80)),
        ];
        var rows = LeaveCompute.BuildNetworkRows(Config, [a, b, c, d], snaps);
        Assert.Equal(["G003", "G001", "G004", "G002"], rows.Select(r => r.Code).ToArray());
        Assert.Null(rows[3].HoursPerFte);
    }

    [Fact]
    public void Network_rows_leave_out_stores_with_no_snapshot()
    {
        var a = new LeaveStoreRef(Guid.NewGuid(), "G001", "Alpha");
        var b = new LeaveStoreRef(Guid.NewGuid(), "G002", "Bravo");
        var rows = LeaveCompute.BuildNetworkRows(Config, [a, b], [(b.Id, Row("1", "SA", 76, 80))]);
        Assert.Equal(["G002"], rows.Select(r => r.Code).ToArray());
    }

    [Fact]
    public void Trend_runs_oldest_first_and_drops_periods_with_no_figure()
    {
        (DateOnly, LiabilityRow)[] snaps =
        [
            (D("2026-08-08"), Row("1", "SA", 76, 100)),
            (D("2026-07-12"), Row("1", "SA", 76, 90)),
            (D("2026-07-25"), Row("1", "CASUAL", 0, 0)),
            (D("2026-07-12"), Row("2", "SA", 76, 70)),
        ];
        var trend = LeaveCompute.BuildTrend(Config, snaps);
        Assert.Equal([new TrendPoint(D("2026-07-12"), 80), new TrendPoint(D("2026-08-08"), 100)], trend.ToArray());
    }

    [Fact]
    public void Display_name_strips_the_rank_prefix()
    {
        Assert.Equal("Smith, Jo", LeaveMetrics.DisplayName("1-Smith, Jo"));
        Assert.Equal("Someone, B", LeaveMetrics.DisplayName("4L- Someone, B"));
        Assert.Equal("Someone, D", LeaveMetrics.DisplayName("6c-Someone, D"));
        Assert.Equal("Shaw-Fletcher, Grace", LeaveMetrics.DisplayName("5-Shaw-Fletcher, Grace"));
        Assert.Equal("No Prefix", LeaveMetrics.DisplayName("No Prefix"));
    }
}
