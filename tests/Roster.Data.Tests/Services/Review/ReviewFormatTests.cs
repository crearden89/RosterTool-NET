// Tests for the port of the pure helpers of src/lib/review/server.ts and
// gateFrom (src/lib/review/gate.ts). Expected labels are Node's en-AU output.

using Roster.Data.Services.Review;

namespace Roster.Data.Tests.Services.Review;

public class ReviewFormatTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd");

    [Theory]
    [InlineData("2026-11-16", "Mon 16 Nov – Sun 29 Nov 2026", "Wed 11 Nov, 6:00 pm")]
    [InlineData("2026-08-31", "Mon 31 Aug – Sun 13 Sept 2026", "Wed 26 Aug, 6:00 pm")]
    [InlineData("2026-06-08", "Mon 8 June – Sun 21 June 2026", "Wed 3 June, 6:00 pm")]
    [InlineData("2026-12-28", "Mon 28 Dec – Sun 10 Jan 2027", "Wed 23 Dec, 6:00 pm")]
    public void Period_title_and_due_label(string start, string title, string due)
    {
        Assert.Equal(title, ReviewFormat.PeriodTitle(D(start)));
        Assert.Equal(due, ReviewFormat.DueLabel(D(start)));
    }

    [Theory]
    [InlineData("2026-11-10T22:42:00Z", "Wed 11 Nov, 9:42 am")]
    [InlineData("2026-10-03T14:30:00Z", "Sun 4 Oct, 12:30 am")]
    [InlineData("2026-10-04T16:00:00Z", "Mon 5 Oct, 3:00 am")]
    [InlineData("2026-07-01T02:00:00Z", "Wed 1 July, 12:00 pm")]
    [InlineData("2026-04-04T15:59:00Z", "Sun 5 Apr, 2:59 am")]
    public void Stamp_is_in_Sydney_time(string utc, string expected) =>
        Assert.Equal(expected, ReviewFormat.Stamp(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Day_labels_and_plan_key()
    {
        Assert.Equal("3 Sept", ReviewFormat.DayMonth(D("2026-09-03")));
        Assert.Equal("Sunday 5 July", ReviewFormat.LongDay(D("2026-07-05")));
        Assert.Equal("plan:7", ReviewFormat.PlanKey(7));
        Assert.Equal("plan:0", ReviewFormat.PlanKey(null));
        Assert.Equal("/plan/review?store=6f1c1d3a-0b7e-4c55-9d2e-6a9b6f0e2a11&period=2026-11-16",
            ReviewLinks.ReviewLink(Guid.Parse("6F1C1D3A-0B7E-4C55-9D2E-6A9B6F0E2A11"), D("2026-11-16")));
    }
}

public class RosterGateTests
{
    private static readonly DateOnly Start = new(2026, 11, 16);
    private static readonly List<DateOnly> Dates = Enumerable.Range(0, 14).Select(Start.AddDays).ToList();

    private static RosterGateInput Input(string? status, string? key, int? version, int approvedDays = 0) => new()
    {
        ReviewStatus = status,
        ReviewKey = key,
        PlanVersion = version,
        Dates = Dates,
        ApprovedDates = Dates.Take(approvedDays).ToHashSet(),
    };

    [Fact]
    public void Unlocked_only_by_an_approval_of_the_plan_as_it_stands()
    {
        Assert.True(RosterGates.GateFrom(Input("approved", "plan:3", 3)).Unlocked);
        Assert.False(RosterGates.GateFrom(Input("approved", "plan:2", 3)).Unlocked);
        Assert.False(RosterGates.GateFrom(Input("submitted", "plan:3", 3)).Unlocked);
        Assert.False(RosterGates.GateFrom(Input("approved", "plan:0", null)).Unlocked);
    }

    [Fact]
    public void Published_needs_every_day_and_goes_out_of_date_when_the_plan_moves_on()
    {
        var published = RosterGates.GateFrom(Input("in_progress", null, 4, 14));
        Assert.True(published.Published);
        Assert.True(published.OutOfDate);
        Assert.False(RosterGates.GateFrom(Input("approved", "plan:4", 4, 13)).Published);
        Assert.False(RosterGates.GateFrom(Input("approved", "plan:4", 4, 14)).OutOfDate);
    }

    [Fact]
    public void A_roster_built_before_the_approval_is_out_of_date()
    {
        var decided = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var gate = RosterGates.GateFrom(Input("approved", "plan:4", 4) with { DecidedAt = decided, RosterGeneratedAt = decided.AddMinutes(-1) });
        Assert.True(gate.OutOfDate);
        Assert.False(RosterGates.GateFrom(Input("approved", "plan:4", 4) with { DecidedAt = decided, RosterGeneratedAt = decided }).OutOfDate);
        Assert.Equal("approved", gate.ReviewStatus);
    }
}
