// New: the cron expressions of vercel.json fire at the same minutes in the in-process
// scheduler (UTC, as Vercel Cron).
using Roster.Web.Features.Jobs;

namespace Roster.Web.Tests.Jobs;

public sealed class CronScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    [Fact]
    public void The_emso_check_fires_at_eight_utc_on_wednesdays()
    {
        var cron = CronSchedule.Parse(ScheduledJobs.EmsoCheck.Schedule);
        // Tuesday 24/11/2026 10:00 -> Wednesday 25/11 08:00.
        Assert.Equal(Utc(2026, 11, 25, 8), cron.GetNextOccurrence(Utc(2026, 11, 24, 10)));
        // Exactly at the minute: the next one is a week later.
        Assert.Equal(Utc(2026, 12, 2, 8), cron.GetNextOccurrence(Utc(2026, 11, 25, 8)));
        // A second into the minute counts as past it.
        Assert.Equal(Utc(2026, 12, 2, 8), cron.GetNextOccurrence(Utc(2026, 11, 25, 8, 0, 1)));
        Assert.Equal(Utc(2026, 11, 25, 8), cron.GetNextOccurrence(Utc(2026, 11, 25, 7, 59, 59)));
        Assert.True(cron.Matches(Utc(2026, 11, 25, 8, 0, 30)));
        Assert.False(cron.Matches(Utc(2026, 11, 26, 8)));
    }

    [Fact]
    public void The_keep_alive_fires_at_five_pm_utc_every_day()
    {
        var cron = CronSchedule.Parse(ScheduledJobs.KeepAlive.Schedule);
        Assert.Equal(Utc(2026, 11, 25, 17), cron.GetNextOccurrence(Utc(2026, 11, 25, 9)));
        Assert.Equal(Utc(2026, 11, 26, 17), cron.GetNextOccurrence(Utc(2026, 11, 25, 17)));
        Assert.Equal(Utc(2027, 1, 1, 17), cron.GetNextOccurrence(Utc(2026, 12, 31, 17, 30)));
    }

    [Fact]
    public void Times_with_an_offset_are_read_as_the_instant_they_are()
    {
        var cron = CronSchedule.Parse("0 8 * * 3");
        // 18:00 AEST Wednesday is 08:00 UTC Wednesday.
        var sydney = new DateTimeOffset(2026, 11, 25, 17, 59, 0, TimeSpan.FromHours(10));
        Assert.Equal(Utc(2026, 11, 25, 8), cron.GetNextOccurrence(sydney));
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-11-25T10:07:00Z", "2026-11-25T10:15:00Z")]
    [InlineData("5/20 * * * *", "2026-11-25T10:26:00Z", "2026-11-25T10:45:00Z")]
    [InlineData("0 9-17/4 * * *", "2026-11-25T13:01:00Z", "2026-11-25T17:00:00Z")]
    [InlineData("30 6 1,15 * *", "2026-11-25T00:00:00Z", "2026-12-01T06:30:00Z")]
    [InlineData("0 0 * * 7", "2026-11-25T00:00:00Z", "2026-11-29T00:00:00Z")] // 7 is Sunday
    [InlineData("0 0 * * 0", "2026-11-25T00:00:00Z", "2026-11-29T00:00:00Z")]
    [InlineData("0 0 29 2 *", "2026-03-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 12 13 * 5", "2026-11-25T00:00:00Z", "2026-11-27T12:00:00Z")] // either day field: Friday 27/11
    [InlineData("0 12 */10 * 1", "2026-11-25T00:00:00Z", "2026-11-30T12:00:00Z")] // "*/" day of month: weekday only
    public void Fields_take_ranges_lists_and_steps(string expression, string after, string expected)
    {
        var next = CronSchedule.Parse(expression).GetNextOccurrence(DateTimeOffset.Parse(after));
        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }

    [Fact]
    public void A_schedule_that_never_fires_has_no_next_occurrence()
    {
        Assert.Null(CronSchedule.Parse("0 0 31 2 *").GetNextOccurrence(Utc(2026, 1, 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 8 * *")]
    [InlineData("0 8 * * 3 2026")]
    [InlineData("60 8 * * 3")]
    [InlineData("0 24 * * *")]
    [InlineData("0 8 0 * *")]
    [InlineData("0 8 * 13 *")]
    [InlineData("0 8 * * 8")]
    [InlineData("0 8 * * MON")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("1,,2 * * * *")]
    public void Anything_else_is_refused(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _));
        if (expression.Length > 0) Assert.Throws<FormatException>(() => CronSchedule.Parse(expression));
    }
}
