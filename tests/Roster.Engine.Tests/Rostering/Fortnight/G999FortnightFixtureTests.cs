// New: the G999 fortnight fixture reads back as the TypeScript holds it (its
// data is used by the production harness, which has no test of its own here).

using Roster.Engine.Core;

namespace Roster.Engine.Tests.Rostering.Fortnight;

public class G999FortnightFixtureTests
{
    [Fact]
    public void Holds_the_whole_fortnight_of_benchmark_and_a_curve_for_every_weekday()
    {
        Assert.Equal(14, G999FortnightFixture.Bench.Count);
        Assert.Equal([11.9553, 4.0, 1.440302, 6.804774, 9.515, 11.5], G999FortnightFixture.Bench["2026-08-31"]);
        Assert.Equal(Enumerable.Range(0, 7), G999FortnightFixture.Curve.Keys.Order());
        // 06:00 → 21:45 by the quarter-hour.
        Assert.All(G999FortnightFixture.Curve.Values, v => Assert.Equal(64, v.Length));
    }

    [Fact]
    public void Reads_the_windows_and_the_admin_and_reset_catalogues_verbatim()
    {
        Assert.Equal(7, G999FortnightFixture.Windows.Count);
        Assert.Equal(("admin", 420.0, 1020.0), (G999FortnightFixture.Windows[0].Group, G999FortnightFixture.Windows[0].Start, G999FortnightFixture.Windows[0].End));
        Assert.Equal(24, G999FortnightFixture.AdminTasks.Count);
        var apm = G999FortnightFixture.AdminTasks[0];
        Assert.Equal(("a1", "APM", "weekly", 0.0, "fixed", 15.0, "clock", 540.0, 10.0),
            (apm.Id, apm.Name, apm.ScheduleType, apm.DayOfWeek!.Value, apm.DurationType, apm.FixedMinutes!.Value, apm.WindowStartKind, apm.WindowStartTime!.Value, apm.SortOrder));
        Assert.Equal(4, G999FortnightFixture.ResetTasks.Count);
        var r1 = G999FortnightFixture.ResetTasks[0];
        Assert.Equal(("r1", "Re-run 1", 15.0, "clock", 600.0, "spread", 10.0),
            (r1.Id, r1.Name, r1.SharePct!.Value, r1.WindowStartKind, r1.WindowStartTime!.Value, r1.Placement, r1.SortOrder));
    }
}
