// Port of src/app/api/cron/emso-check/route.ts and src/app/api/cron/keep-alive/route.ts
// (the work each route did; CronEndpoints.cs has the HTTP side, CronJobHostedService.cs the
// schedule).
using System.Text.Json.Nodes;
using Roster.Data.Jobs;

namespace Roster.Web.Features.Jobs;

/// <summary>A job's outcome: the HTTP status and the JSON body the TypeScript route answered with.</summary>
public sealed record CronJobOutcome(int StatusCode, JsonObject Body)
{
    /// <summary>The body's "ok" flag.</summary>
    public bool Ok => Body["ok"]?.GetValue<bool>() ?? false;
}

/// <summary>One scheduled job, run by the scheduler or by its endpoint.</summary>
public interface ICronJob
{
    ScheduledJobDefinition Definition { get; }

    Task<CronJobOutcome> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Scheduled EMSO unapproved-roster check (spec §8), at 08:00 UTC each Wednesday (~6pm
/// AEST of the planning week).
/// </summary>
public sealed class EmsoCheckJob(IEmsoRosterCheck check, ICronHeartbeat heartbeat) : ICronJob
{
    public ScheduledJobDefinition Definition => ScheduledJobs.EmsoCheck;

    public async Task<CronJobOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = await check.RunEmsoUnapprovedCheckAsync(cancellationToken);
        // Heartbeat: this check writes a notification only when a store is actually
        // behind, so an empty notifications table cannot distinguish "ran, nothing to
        // report" from "never ran". The heartbeat can.
        var beat = await heartbeat.RecordCronRunAsync(Definition.Name, result, cancellationToken);
        return new CronJobOutcome(200, new JsonObject
        {
            ["ok"] = true,
            ["created"] = result.Created,
            ["updated"] = result.Updated,
            ["failed"] = result.Failed,
            ["heartbeat"] = beat,
        });
    }
}

/// <summary>Daily keep-alive at 17:00 UTC: a real query and a heartbeat write.</summary>
public sealed class KeepAliveJob(IKeepAlive keepAlive, ICronHeartbeat heartbeat) : ICronJob
{
    public ScheduledJobDefinition Definition => ScheduledJobs.KeepAlive;

    public async Task<CronJobOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var touched = await keepAlive.TouchAsync(cancellationToken);
        if (!touched.Ok)
        {
            // Surface it: a keep-alive that quietly fails is worse than none at all,
            // because it looks like cover that is not there.
            return new CronJobOutcome(500, new JsonObject { ["ok"] = false, ["error"] = touched.Error });
        }

        var result = new KeepAliveHeartbeat(touched.Stores);
        var beat = await heartbeat.RecordCronRunAsync(Definition.Name, result, cancellationToken);
        return new CronJobOutcome(200, new JsonObject
        {
            ["ok"] = true,
            ["stores"] = result.Stores,
            ["heartbeat"] = beat,
        });
    }

    /// <summary>What the keep-alive records as its last result: { stores }.</summary>
    public sealed record KeepAliveHeartbeat(int Stores);
}
