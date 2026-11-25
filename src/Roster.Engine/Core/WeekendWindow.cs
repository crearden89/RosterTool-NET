// Port of windowSaturdays (app/(app)/setup/weekends/page.tsx), the rule
// src/lib/weekendWindow.test.ts pins down.
// The Weekends page shows four Saturdays: the previous fortnight's two as
// context and the rostered fortnight's own two, editable. They come from the
// period grid, never from today's date: rolling its own window off today and
// stepping it by 14 days put the fortnight's own Saturdays in different
// windows, and markers saved against them showed as blank.

namespace Roster.Engine.Core;

public static class WeekendWindow
{
    /// <summary>The previous fortnight's two Saturdays, then this fortnight's two.</summary>
    public static List<DateOnly> WindowSaturdays(DateOnly periodStart)
    {
        var prev = Period.PeriodDatesFrom(Period.AddPeriods(periodStart, -1));
        var cur = Period.PeriodDatesFrom(periodStart);
        return [prev[5], prev[12], cur[5], cur[12]];
    }
}
