// Port of src/lib/roster/weekendOff.test.ts.

using Roster.Engine.RosterOps;
using static Roster.Engine.Core.DateOnlyExtensions;

namespace Roster.Engine.Tests.RosterOps;

public class WeekendOffTests
{
    // 05/09/2026 is a Saturday.
    private static WeekendMarker Marker(string id, bool satOff, bool sunOff, string weekendStart = "2026-09-05") =>
        new(id, ParseIso(weekendStart), satOff, sunOff);

    [Fact]
    public void Finds_the_weekends_saturday_and_nothing_on_a_weekday()
    {
        Assert.Equal(ParseIso("2026-09-05"), WeekendOff.WeekendSaturday(ParseIso("2026-09-05")));
        Assert.Equal(ParseIso("2026-09-05"), WeekendOff.WeekendSaturday(ParseIso("2026-09-06")));
        Assert.Null(WeekendOff.WeekendSaturday(ParseIso("2026-09-04")));
    }

    [Fact]
    public void Takes_a_saturday_off_by_sat_off_and_a_sunday_off_by_sun_off()
    {
        WeekendMarker[] markers = [Marker("ana", true, false), Marker("ben", false, true), Marker("cal", true, true)];
        Assert.Equal(["ana", "cal"], WeekendOff.MarkedOffOn(ParseIso("2026-09-05"), markers).Order());
        Assert.Equal(["ben", "cal"], WeekendOff.MarkedOffOn(ParseIso("2026-09-06"), markers).Order());
    }

    [Fact]
    public void Ignores_other_weekends_and_weekdays()
    {
        Assert.Equal(0, WeekendOff.MarkedOffOn(ParseIso("2026-09-05"), [Marker("ana", true, true, "2026-09-12")]).Size);
        Assert.Equal(0, WeekendOff.MarkedOffOn(ParseIso("2026-09-04"), [Marker("ana", true, true)]).Size);
    }
}
