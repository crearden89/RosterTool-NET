// New: the in-process scheduler that stands in for Vercel Cron (vercel.json). Each job
// fires at the minutes of its cron schedule (UTC), in its own scope, exactly as the
// scheduler's call to the job's endpoint did. A run that is missed while the app is down
// is not made up afterwards, as with Vercel Cron.
using Microsoft.Extensions.Options;

namespace Roster.Web.Features.Jobs;

/// <summary>Runs one <see cref="ICronJob"/> on its schedule.</summary>
public sealed class CronJobHostedService<TJob>(
    IServiceScopeFactory scopes,
    IOptionsMonitor<JobsOptions> options,
    TimeProvider clock,
    ILogger<CronJobHostedService<TJob>> logger) : BackgroundService
    where TJob : ICronJob
{
    /// <summary>The longest single wait: the clock is read again after it, so a sleep or a clock change can't skip a run.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var definition = DefinitionOf();
        var expression = ScheduleOf(definition, options.CurrentValue);
        if (!CronSchedule.TryParse(expression, out var schedule) || schedule is null)
        {
            logger.LogError("Scheduled job {Job}: \"{Schedule}\" is not a cron expression; the job will not run.", definition.Name, expression);
            return;
        }

        var next = schedule.GetNextOccurrence(clock.GetUtcNow());
        while (!stoppingToken.IsCancellationRequested && next is { } due)
        {
            var wait = due - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait < MaxWait ? wait : MaxWait, clock, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunOnceAsync(definition, stoppingToken).ConfigureAwait(false);
            // Late (the process was paused past `due`) or long-running: one run, then on.
            var now = clock.GetUtcNow();
            next = schedule.GetNextOccurrence(now > due ? now : due);
        }
    }

    /// <summary>One run, in its own scope. Never throws: a failed run is logged and the schedule carries on.</summary>
    internal async Task<CronJobOutcome?> RunOnceAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<TJob>();
            var outcome = await job.RunAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Ok)
                logger.LogInformation("Scheduled job {Job} ran: {Result}", definition.Name, outcome.Body.ToJsonString());
            else
                logger.LogError("Scheduled job {Job} failed: {Result}", definition.Name, outcome.Body.ToJsonString());
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job {Job} threw.", definition.Name);
            return null;
        }
    }

    private static ScheduledJobDefinition DefinitionOf() =>
        typeof(TJob) == typeof(EmsoCheckJob) ? ScheduledJobs.EmsoCheck
        : typeof(TJob) == typeof(KeepAliveJob) ? ScheduledJobs.KeepAlive
        : throw new InvalidOperationException($"No schedule for {typeof(TJob).Name}.");

    /// <summary>The configured schedule for a job (the vercel.json one unless changed).</summary>
    internal static string ScheduleOf(ScheduledJobDefinition definition, JobsOptions options) =>
        definition.Name == ScheduledJobs.EmsoCheck.Name ? options.EmsoCheckSchedule
        : definition.Name == ScheduledJobs.KeepAlive.Name ? options.KeepAliveSchedule
        : definition.Schedule;
}
