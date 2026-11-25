// New: the scheduled jobs' settings. The TypeScript app read CRON_SECRET from the
// environment and took its schedules from vercel.json; here both are settings.
namespace Roster.Web.Features.Jobs;

/// <summary>Settings for the scheduled jobs (section "Roster:Jobs").</summary>
public sealed class JobsOptions
{
    public const string Section = "Roster:Jobs";

    /// <summary>
    /// The bearer token an outside scheduler sends to /api/cron/*. With no secret both
    /// endpoints refuse every request. Set it with user secrets or the environment
    /// (Roster__Jobs__CronSecret); the TypeScript app's CRON_SECRET variable is read too.
    /// Never in appsettings.json.
    /// </summary>
    public string? CronSecret { get; set; }

    /// <summary>
    /// Run the jobs in this process on their schedules. On a farm of several servers, leave it
    /// on for one of them (or turn it off everywhere and call /api/cron/* from a scheduler).
    /// </summary>
    public bool RunScheduler { get; set; } = true;

    /// <summary>The EMSO check's schedule, cron syntax in UTC (vercel.json: "0 8 * * 3").</summary>
    public string EmsoCheckSchedule { get; set; } = ScheduledJobs.EmsoCheck.Schedule;

    /// <summary>The keep-alive's schedule, cron syntax in UTC (vercel.json: "0 17 * * *").</summary>
    public string KeepAliveSchedule { get; set; } = ScheduledJobs.KeepAlive.Schedule;
}

/// <summary>One scheduled job: its name (the cron_runs key), default schedule (UTC) and endpoint.</summary>
public sealed record ScheduledJobDefinition(string Name, string Schedule, string Path);

/// <summary>The two jobs of vercel.json.</summary>
public static class ScheduledJobs
{
    /// <summary>08:00 UTC every Wednesday: about 6 pm in Sydney, the evening of the planning week's Wednesday.</summary>
    public static readonly ScheduledJobDefinition EmsoCheck = new("emso-check", "0 8 * * 3", "/api/cron/emso-check");

    /// <summary>17:00 UTC every day.</summary>
    public static readonly ScheduledJobDefinition KeepAlive = new("keep-alive", "0 17 * * *", "/api/cron/keep-alive");

    public static readonly IReadOnlyList<ScheduledJobDefinition> All = [EmsoCheck, KeepAlive];
}
