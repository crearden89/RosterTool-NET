// Port of src/lib/plan/pageFigures.test.ts.

using Roster.Data.Services.Held;
using Roster.Engine.Core;
using Roster.Engine.Planning.Plan;
using Roster.Engine.Planning.Stage1;
using Roster.Web.Features.Plan;

namespace Roster.Web.Tests.Plan;

public class PageFiguresTests
{
    private static List<DisplayBlock> Tiles(int n, string group = "checkout") =>
        Enumerable.Range(0, n).Select(i => new DisplayBlock(360 + i * 15, group)).ToList();

    private static PlanSnapshotDay Day(
        string date, bool ok = true, bool closed = false, double worked = 4, double benchmark = 4,
        string[]? flags = null, int shifts = 2, bool noHours = false, double nonOp = 0)
    {
        var shiftList = Enumerable.Range(0, shifts).Select(_ => new PlanShift { Blocks = [.. Tiles(8), new DisplayBlock(600, "break")] }).ToList();
        return new PlanSnapshotDay
        {
            Date = DateOnlyExtensions.ParseIso(date),
            Ok = ok,
            Result = new Stage1Computation
            {
                HasData = !closed,
                Closed = closed,
                Flags = [],
                TotalBenchmark = benchmark,
                Allocations = [new GroupAllocation("non_op", nonOp, 0)],
            },
            Plan = closed
                ? null
                : new PlanShiftComputation
                {
                    HasData = true,
                    Flags = [.. flags ?? []],
                    Shifts = shiftList,
                    Hours = noHours ? null : new HoursCheck { Worked = worked, Benchmark = benchmark },
                },
        };
    }

    [Fact]
    public void Counts_hours_days_within_the_tolerance_shifts_and_checks_as_the_Review_does()
    {
        var f = PageFigures.PlanFortnightFigures(
        [
            Day("2026-11-23", worked: 51, benchmark: 50.91, nonOp: 2.25),
            Day("2026-11-24", worked: 40, benchmark: 42.5, ok: false, flags: ["Planned hours are 2.50 h under the benchmark (40.00 h planned, 42.50 h benchmark)"]),
            Day("2026-11-25", flags: ["Longer shifts for the crew's breaks: 2.00 h added to existing shifts", "Longer shifts for the crew's breaks: 2.00 h added to existing shifts"], nonOp: 1),
            Day("2026-11-26", closed: true),
            Day("2026-11-27", noHours: true, shifts: 3),
        ]);
        Assert.Equal(4, f.OkDays);
        Assert.Equal(1, f.ErrorDays);
        Assert.Equal(4, f.PlannedDays);
        Assert.Equal(2, f.WithinDays);
        Assert.Equal(50.91 + 42.5 + 4 + 4 + 4, f.Benchmark, 6);
        // A plan held without the hours check counts its painted tiles.
        Assert.Equal(6, f.Days[4].Worked);
        Assert.Equal(51 + 40 + 4 + 6, f.Worked, 6);
        Assert.Equal(3.25, f.NonOp);
        Assert.Equal(2 + 2 + 2 + 3, f.Shifts);
        // The hours error is an error; a repeated warning is one check.
        Assert.Equal((1, 1), (f.Errors, f.Warnings));
        Assert.True(f.Days[3] is { Closed: true, Planned: false, Shifts: 0, Errors: 0 });
    }

    [Fact]
    public void Writes_a_signed_figure_and_a_days_long_label()
    {
        Assert.Equal(["+0.09", "−0.13", "+0.00", "+1.26"], new[] { PageFigures.Signed(0.0912), PageFigures.Signed(-0.13), PageFigures.Signed(-0.001), PageFigures.Signed(1.257) });
        Assert.Equal("Tuesday 24 November", PageFigures.LongDayLabel(new DateOnly(2026, 11, 24)));
        Assert.Equal("Sunday 6 December", PageFigures.LongDayLabel(new DateOnly(2026, 12, 6)));
    }
}
